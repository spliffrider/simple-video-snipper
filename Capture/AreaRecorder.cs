using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace SimpleVideoSnipper;

internal static class AreaRecorder
{
    private const int FrameRate = 30;

    public static async Task<string?> RecordAsync(SelectedArea area, string folder, Task stopRequested)
    {
        using var frames = new MonitorFrames(area);
        var stop = Task.WhenAny(stopRequested, frames.Closed);
        var firstFrame = frames.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        if (await Task.WhenAny(firstFrame, stop).ConfigureAwait(false) == stop)
            return null;
        await firstFrame.ConfigureAwait(false);

        var width = (uint)area.Region.Width;
        var height = (uint)area.Region.Height;
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, width, height);
        input.FrameRate.Numerator = FrameRate;
        input.FrameRate.Denominator = 1;
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Audio = null;
        profile.Video.Width = width;
        profile.Video.Height = height;
        profile.Video.FrameRate.Numerator = FrameRate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.Bitrate = Math.Clamp(width * height * 5, 2_000_000u, 35_000_000u);

        var clock = new Stopwatch();
        long frameCount = 0;
        Exception? captureError = null;
        source.Starting += (_, args) => args.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += async (_, args) =>
        {
            var request = args.Request;
            var deferral = request.GetDeferral();
            try
            {
                clock.Start();
                var timestamp = SampleTime(frameCount);
                var wait = timestamp - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                    await Task.WhenAny(Task.Delay(wait), stop).ConfigureAwait(false);
                if (stop.IsCompleted)
                    return;

                var surface = frames.GetSurface();
                try
                {
                    var sample = MediaStreamSample.CreateFromDirect3D11Surface(surface, timestamp);
                    sample.Duration = SampleTime(frameCount + 1) - timestamp;
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

        var name = $"Area {DateTime.Now:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid():N}.mp4";
        var destination = Path.Combine(folder, name);
        var partial = destination + ".partial";
        await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var output = stream.AsRandomAccessStream();
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var preparation = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, output, profile);
            if (!preparation.CanTranscode)
                throw new InvalidOperationException($"Windows could not prepare the MP4 encoder ({preparation.FailureReason}).");
            await preparation.TranscodeAsync();
        }

        if (captureError is not null)
            throw new InvalidOperationException("Area capture stopped unexpectedly.", captureError);
        if (frameCount == 0)
        {
            File.Delete(partial);
            return null;
        }

        File.Move(partial, destination);
        return destination;
    }

    private static TimeSpan SampleTime(long frame) => TimeSpan.FromTicks(frame * TimeSpan.TicksPerSecond / FrameRate);
}
