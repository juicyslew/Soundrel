namespace Soundrel.Models;

public sealed record PlaybackSnapshot(
    LibraryPlaylist? ActivePlaylist,
    LibraryPlaylist? PendingPlaylist,
    LibraryTrack? CurrentTrack,
    LibraryPlaylist? CurrentPlaylist,
    IReadOnlyList<PlaybackQueueEntry> Queue,
    PlaybackState State,
    string? LastError,
    long? CurrentPlaybackId,
    TimeSpan? CurrentDuration,
    float MasterGain = 1f,
    MasterFadeState MasterFadeState = Soundrel.Models.MasterFadeState.Full,
    IReadOnlyList<AmbiencePlaybackSnapshot>? Ambience = null,
    float MusicVolume = 1f,
    float AmbienceVolume = 1f,
    float MasterVolume = 1f)
{
    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSnapshots =>
        Ambience ?? Array.Empty<AmbiencePlaybackSnapshot>();

    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSources => AmbienceSnapshots;
}
