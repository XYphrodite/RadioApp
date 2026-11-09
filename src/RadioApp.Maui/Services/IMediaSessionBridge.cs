namespace RadioApp.Maui.Services;

public sealed record MediaPlaybackSnapshot(
    int TrackId,
    string TrackTitle,
    string TrackArtist,
    string StationLabel,
    string CoverUrl,
    bool IsPlaying,
    double ElapsedSec,
    double DurationSec);

public interface IMediaSessionBridge
{
    bool IsSupported { get; }

    event Action<bool>? PlayPauseRequested;
    event Action? TogglePlayPauseRequested;
    event Action<int>? StepStationRequested;

    void UpdatePlayback(MediaPlaybackSnapshot snapshot);
    void Stop();
}

public sealed class NoOpMediaSessionBridge : IMediaSessionBridge
{
    public bool IsSupported => false;

    public event Action<bool>? PlayPauseRequested
    {
        add { }
        remove { }
    }

    public event Action? TogglePlayPauseRequested
    {
        add { }
        remove { }
    }

    public event Action<int>? StepStationRequested
    {
        add { }
        remove { }
    }

    public void UpdatePlayback(MediaPlaybackSnapshot snapshot)
    {
    }

    public void Stop()
    {
    }
}
