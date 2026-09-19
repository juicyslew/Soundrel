using Soundrel.Models;

namespace Soundrel.Services;

public sealed record AudioPlaybackInfo(TimeSpan Duration);

public sealed record AudioProgressSnapshot(
    long? PlaybackId,
    TimeSpan Position,
    TimeSpan Duration,
    float MasterGain = 1f,
    MasterFadeState MasterFadeState = Soundrel.Models.MasterFadeState.Full,
    IReadOnlyList<AmbiencePlaybackSnapshot>? Ambience = null,
    float MusicVolume = 1f,
    float AmbienceVolume = 1f,
    float MasterVolume = 1f)
{
    public static AudioProgressSnapshot Stopped { get; } = new(
        null,
        TimeSpan.Zero,
        TimeSpan.Zero,
        1f,
        Soundrel.Models.MasterFadeState.Full,
        Array.Empty<AmbiencePlaybackSnapshot>(),
        1f,
        1f,
        1f);

    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSnapshots =>
        Ambience ?? Array.Empty<AmbiencePlaybackSnapshot>();

    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSources => AmbienceSnapshots;
}

public sealed record AudioTrackEndedNotification(long PlaybackId);

public sealed record AudioOutputFault(string Message, Exception? Exception = null, long? PlaybackId = null);

public delegate Task AudioTrackEndedHandler(AudioTrackEndedNotification notification);

public delegate Task AudioOutputFaultedHandler(AudioOutputFault fault);

public interface IAudioEngine : IAsyncDisposable
{
    // Raised at most once for natural end-of-stream while the playback is still
    // logical current, and only after PlayAsync has returned. Replacement,
    // retirement, stop, fault, and disposal never raise this notification.
    event AudioTrackEndedHandler? TrackEnded;

    event AudioOutputFaultedHandler? OutputFaulted;

    Task<AudioPlaybackInfo> PlayAsync(
        LibraryTrack track,
        long playbackId,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        CancellationToken cancellationToken = default);

    Task PlayAmbienceAsync(
        LibraryTrack track,
        float sourceGain,
        CancellationToken cancellationToken = default);

    Task StopAmbienceAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    Task SetAmbienceSourceGainAsync(
        string filePath,
        float sourceGain,
        CancellationToken cancellationToken = default);

    Task SetMusicVolumeAsync(float volume, CancellationToken cancellationToken = default);

    Task SetAmbienceVolumeAsync(float volume, CancellationToken cancellationToken = default);

    Task SetMasterVolumeAsync(float volume, CancellationToken cancellationToken = default);

    // Returns after the sample-counted envelope has been armed.
    Task FadeMasterAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    Task StopMusicAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<AudioProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken = default);
}
