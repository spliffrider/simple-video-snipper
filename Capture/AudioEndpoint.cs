using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SimpleVideoSnipper;

internal readonly record struct AudioSelection(string? MicrophoneId, string? OutputId);

internal sealed class AudioEndpoint : IDisposable
{
    public const int Rate = 48_000;
    public const int Channels = 2;
    private readonly ManualResetEvent stopping = new(false);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private float peak;
    private string? error;

    public AudioEndpoint(string deviceId, bool loopback = false)
    {
        worker = Task.Factory.StartNew(() => ReadDevice(deviceId, loopback), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public AudioTimeline Timeline { get; } = new();
    public Task Ready => ready.Task;
    public float Peak => Volatile.Read(ref peak);
    public string? Error => Volatile.Read(ref error);

    public static long Clock => Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks;

    public static long FrameAt(long ticks) =>
        ticks / TimeSpan.TicksPerSecond * Rate + ticks % TimeSpan.TicksPerSecond * Rate / TimeSpan.TicksPerSecond;

    private void ReadDevice(string deviceId, bool loopback)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDevice(deviceId);
            using var wake = new AutoResetEvent(false);
            using var client = device.CreateAudioClient();
            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm |
                        AudioClientStreamFlags.SrcDefaultQuality;
            if (loopback)
                flags |= AudioClientStreamFlags.Loopback;

            var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
            client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, format, Guid.Empty);
            client.SetEventHandle(wake.SafeWaitHandle.DangerousGetHandle());
            using var input = client.AudioCaptureClient;
            client.Start();
            try
            {
                ready.TrySetResult();
                WaitHandle[] events = [stopping, wake];
                while (WaitHandle.WaitAny(events, 50) != 0)
                {
                    while (input.GetNextPacketSize() > 0 && !stopping.WaitOne(0))
                    {
                        using var packet = input.GetBufferLease(format.BlockAlign);
                        if ((packet.Flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            Volatile.Write(ref peak, 0);
                            continue;
                        }

                        var timestamp = (packet.Flags & AudioClientBufferFlags.TimestampError) == 0
                            ? packet.QPCPosition
                            : Clock - (long)packet.FrameCount * TimeSpan.TicksPerSecond / Rate;
                        var maximum = Timeline.Store(FrameAt(timestamp), MemoryMarshal.Cast<byte, float>(packet.Buffer));
                        Volatile.Write(ref peak, maximum);
                    }
                }
            }
            finally
            {
                client.Stop();
            }
        }
        catch (Exception exception)
        {
            var label = loopback ? "System audio" : "Microphone";
            var message = $"{label} became unavailable.";
            Volatile.Write(ref error, message);
            ready.TrySetException(new InvalidOperationException($"Could not start {label.ToLowerInvariant()}: {exception.Message}", exception));
        }
    }

    public void Dispose()
    {
        stopping.Set();
        worker.GetAwaiter().GetResult();
        stopping.Dispose();
    }
}

internal sealed class AudioTimeline
{
    private const int Capacity = AudioEndpoint.Rate * 2;
    private const int Channels = AudioEndpoint.Channels;
    private readonly float[] samples = new float[Capacity * Channels];
    private readonly long[] positions = new long[Capacity];
    private readonly object gate = new();

    public AudioTimeline() => Array.Fill(positions, -1);

    public float Store(long start, ReadOnlySpan<float> data)
    {
        var maximum = 0f;
        lock (gate)
        {
            for (var frame = 0; frame < data.Length / Channels; frame++)
            {
                var position = start + frame;
                var slot = (int)(position % Capacity);
                positions[slot] = position;
                for (var channel = 0; channel < Channels; channel++)
                {
                    var input = data[frame * Channels + channel];
                    var value = float.IsFinite(input) ? input : 0;
                    samples[slot * Channels + channel] = value;
                    maximum = Math.Max(maximum, Math.Abs(value));
                }
            }
        }
        return maximum;
    }

    public void AddTo(long start, Span<float> mix)
    {
        lock (gate)
        {
            for (var frame = 0; frame < mix.Length / Channels; frame++)
            {
                var position = start + frame;
                var slot = (int)(position % Capacity);
                if (positions[slot] != position)
                    continue;

                for (var channel = 0; channel < Channels; channel++)
                    mix[frame * Channels + channel] += samples[slot * Channels + channel];
            }
        }
    }
}
