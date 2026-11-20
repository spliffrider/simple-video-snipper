using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace SimpleVideoSnipper;

internal static class ScreenRecorder
{
    private const int FrameRate = 30;
    private const int AudioPacketFrames = AudioEndpoint.Rate / 50;
    private static readonly TimeSpan AudioBufferTime = TimeSpan.FromMilliseconds(25);

    public static async Task<string?> RecordAsync(CaptureTarget target, AudioSelection options, string folder,
        Task stopRequested, IProgress<string>? audioStatus)
    {
        using var frames = new CaptureFrames(target);
        using var microphone = options.MicrophoneId is { } id ? new AudioEndpoint(id) : null;
        using var loopback = options.OutputId is { } outputId ? new AudioEndpoint(outputId, loopback: true) : null;
        var audioWarnings = new HashSet<string>();
        try
        {
            await Task.WhenAll(microphone?.Ready ?? Task.CompletedTask, loopback?.Ready ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A device saved in Settings may have been disconnected before recording.
            ReportAudioWarning(microphone?.Error);
            ReportAudioWarning(loopback?.Error);
        }
        var mic = microphone?.Ready.IsCompletedSuccessfully == true ? microphone : null;
        var system = loopback?.Ready.IsCompletedSuccessfully == true ? loopback : null;
        var hasAudio = mic is not null || system is not null;
        var stop = Task.WhenAny(stopRequested, frames.Closed);
        var firstFrame = frames.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        if (await Task.WhenAny(firstFrame, stop).ConfigureAwait(false) == stop)
            return null;
        await firstFrame.ConfigureAwait(false);
        var frameTimeout = Task.Delay(TimeSpan.FromSeconds(3));
        while (!frames.UpdateFrame())
        {
            if (stop.IsCompleted)
                return null;
            if (frameTimeout.IsCompleted)
                throw new InvalidOperationException("Windows did not deliver a frame from the selected source.");
            await Task.WhenAny(Task.Delay(20), stop).ConfigureAwait(false);
        }

        var width = (uint)frames.OutputSize.Width;
        var height = (uint)frames.OutputSize.Height;
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height);
        input.FrameRate.Numerator = FrameRate;
        input.FrameRate.Denominator = 1;
        var video = new VideoStreamDescriptor(input);
        var pcm = AudioEncodingProperties.CreatePcm(AudioEndpoint.Rate, AudioEndpoint.Channels, 16);
        var source = hasAudio
            ? new MediaStreamSource(video, new AudioStreamDescriptor(pcm))
            : new MediaStreamSource(video);
        source.BufferTime = TimeSpan.Zero;
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Audio = hasAudio ? AudioEncodingProperties.CreateAac(AudioEndpoint.Rate, AudioEndpoint.Channels, 192_000) : null;
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = Math.Clamp(width * height * 5, 2_000_000u, 35_000_000u);

        long epoch = 0;
        long frameCount = 0;
        long audioFrame = 0;
        Exception? captureError = null;
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += async (_, args) =>
        {
            var request = args.Request;
            var deferral = request.GetDeferral();
            try
            {
                Interlocked.CompareExchange(ref epoch, AudioEndpoint.Clock, 0);
                var wantsAudio = request.StreamDescriptor is AudioStreamDescriptor;
                var timestamp = wantsAudio ? SampleTime(audioFrame, AudioEndpoint.Rate) : SampleTime(frameCount, FrameRate);
                var deadline = wantsAudio
                    ? SampleTime(audioFrame + AudioPacketFrames, AudioEndpoint.Rate) + AudioBufferTime
                    : timestamp;
                var wait = deadline - TimeSpan.FromTicks(AudioEndpoint.Clock - epoch);
                if (wait > TimeSpan.Zero)
                    await Task.WhenAny(Task.Delay(wait), stop).ConfigureAwait(false);
                if (stop.IsCompleted)
                    return;

                if (wantsAudio)
                {
                    ReportAudioWarning(mic?.Error);
                    ReportAudioWarning(system?.Error);
                    request.Sample = CreateAudioSample(mic?.Timeline, system?.Timeline,
                        AudioEndpoint.FrameAt(epoch) + audioFrame, timestamp);
                    audioFrame += AudioPacketFrames;
                    return;
                }

                var surface = frames.GetSurface();
                try
                {
                    var sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, timestamp);
                    sample.Duration = SampleTime(frameCount + 1, FrameRate) - timestamp;
                    sample.Processed += ReleaseSample;
                    request.Sample = sample;
                    frameCount++;

                    void ReleaseSample(MediaStreamSample sender, object e)
                    {
                        sender.Processed -= ReleaseSample;
                        surface.Dispose();
                    }
                }
                catch
                {
                    surface.Dispose();
                    throw;
                }
            }
            catch (Exception exception)
            {
                captureError = exception;
            }
            finally
            {
                deferral.Complete();
            }
        };

        var mode = target.IsWindow ? "Window" : "Area";
        var destination = PendingClip.NewPath(folder, mode, ExportFormat.Mp4);
        var partial = destination + ".partial";
        await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var output = stream.AsRandomAccessStream();
            // Hardware transcoding can encode black frames from the Direct3D capture surfaces.
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
            var preparation = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
            if (!preparation.CanTranscode)
                throw new InvalidOperationException($"Windows could not prepare the MP4 encoder ({preparation.FailureReason}).");
            try
            {
                await preparation.TranscodeAsync();
            }
            catch when (captureError is not null)
            {
                throw new InvalidOperationException("Screen capture stopped unexpectedly.", captureError);
            }
        }

        if (captureError is not null)
            throw new InvalidOperationException("Screen capture stopped unexpectedly.", captureError);
        if (frameCount == 0)
        {
            File.Delete(partial);
            return null;
        }

        File.Move(partial, destination);
        return destination;

        void ReportAudioWarning(string? warning)
        {
            if (warning is not null && audioWarnings.Add(warning))
                audioStatus?.Report(warning);
        }
    }

    private static MediaStreamSample CreateAudioSample(AudioTimeline? mic, AudioTimeline? system, long frame, TimeSpan time)
    {
        Span<float> mix = stackalloc float[AudioPacketFrames * AudioEndpoint.Channels];
        mix.Clear();
        mic?.AddTo(frame, mix);
        system?.AddTo(frame, mix);
        var gain = mic is not null && system is not null ? 0.5f : 1f;
        var buffer = new byte[mix.Length * sizeof(short)];
        var pcm = MemoryMarshal.Cast<byte, short>(buffer.AsSpan());
        for (var i = 0; i < mix.Length; i++)
            pcm[i] = (short)Math.Clamp(mix[i] * gain * short.MaxValue, short.MinValue, short.MaxValue);
        var sample = MediaStreamSample.CreateFromBuffer(buffer.AsBuffer(), time);
        sample.Duration = SampleTime(AudioPacketFrames, AudioEndpoint.Rate);
        return sample;
    }

    private static TimeSpan SampleTime(long frame, int rate) => TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / rate);
}
