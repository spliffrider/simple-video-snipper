using System.Windows.Forms.Integration;
using MediaElement = System.Windows.Controls.MediaElement;
using MediaState = System.Windows.Controls.MediaState;
using Stretch = System.Windows.Media.Stretch;

namespace SimpleVideoSnipper;

internal sealed class ClipPlayback : ElementHost
{
    private readonly MediaElement player = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Close,
        Stretch = Stretch.Uniform,
        Focusable = false,
    };
    private TimeSpan duration;
    private TimeSpan requestedPosition;

    public ClipPlayback()
    {
        Dock = DockStyle.Fill;
        Child = player;
        BackColor = Color.FromArgb(17, 23, 32);
        AccessibleName = "Video playback";
        TabStop = false;

        player.MediaOpened += (_, _) =>
        {
            if (player.Source is null)
                return;
            if (duration <= TimeSpan.Zero || player.NaturalVideoWidth <= 0)
            {
                Failed?.Invoke("The preview has no playable video or duration.");
                return;
            }
            IsOpen = true;
            Seek(requestedPosition);
            player.IsMuted = false;
            Opened?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        {
            if (IsOpen)
                Ended?.Invoke();
        };
        player.MediaFailed += (_, args) =>
        {
            if (player.Source is not null)
                Failed?.Invoke(args.ErrorException?.Message ?? "Windows could not open the preview.");
        };
        player.MouseLeftButtonDown += (_, _) => PauseRequested?.Invoke();
    }

    public bool IsOpen { get; private set; }
    public TimeSpan Position => IsOpen ? player.Position : requestedPosition;
    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? PauseRequested;

    public void Open(string path, TimeSpan mediaDuration, TimeSpan position)
    {
        duration = mediaDuration;
        requestedPosition = position;
        player.IsMuted = true;
        player.Source = new Uri(Path.GetFullPath(path));
        player.Play();
    }

    public void SetVolume(double volume) => player.Volume = volume;

    public void Seek(TimeSpan position)
    {
        requestedPosition = position;
        if (!IsOpen)
            return;
        var lastPosition = duration - TimeSpan.FromMilliseconds(1);
        player.Position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Math.Max(0, lastPosition.Ticks)));
    }

    public void CloseMedia()
    {
        IsOpen = false;
        if (player.Source is not null)
        {
            player.Close();
            player.Source = null;
        }
        duration = requestedPosition = TimeSpan.Zero;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseMedia();
        base.Dispose(disposing);
    }
}
