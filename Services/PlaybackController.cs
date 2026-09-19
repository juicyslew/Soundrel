using System.IO;
using Soundrel.Models;

namespace Soundrel.Services;

public sealed class PlaybackErrorEventArgs(string message, Exception? exception = null) : EventArgs
{
    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}

public sealed class PlaybackController : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IAudioEngine _audioEngine;
    private readonly Random _random;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<PlaybackQueueEntry> _queue = [];
    private readonly Dictionary<string, ShuffleBag> _shuffleBags = new(PathComparer);
    private readonly Dictionary<string, AmbiencePlaybackSnapshot> _ambience = new(PathComparer);

    private LibraryPlaylist? _activePlaylist;
    private LibraryPlaylist? _pendingPlaylist;
    private LibraryTrack? _currentTrack;
    private LibraryPlaylist? _currentPlaylist;
    private PlaybackState _state;
    private string? _lastError;
    private long? _currentPlaybackId;
    private TimeSpan? _currentDuration;
    private float _masterGain = 1f;
    private MasterFadeState _masterFadeState = MasterFadeState.Full;
    private float _musicVolume = 1f;
    private float _ambienceVolume = 1f;
    private float _masterVolume = 1f;
    private long _lastIssuedPlaybackId;
    private bool _disposed;

    public PlaybackController(IAudioEngine audioEngine, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(audioEngine);

        _audioEngine = audioEngine;
        _random = random ?? Random.Shared;
        _audioEngine.TrackEnded += OnTrackEndedAsync;
        _audioEngine.OutputFaulted += OnOutputFaultedAsync;
    }

    public event EventHandler? StateChanged;

    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    public LibraryPlaylist? ActivePlaylist
    {
        get
        {
            lock (_stateLock)
            {
                return _activePlaylist;
            }
        }
    }

    public LibraryPlaylist? PendingPlaylist
    {
        get
        {
            lock (_stateLock)
            {
                return _pendingPlaylist;
            }
        }
    }

    public LibraryTrack? CurrentTrack
    {
        get
        {
            lock (_stateLock)
            {
                return _currentTrack;
            }
        }
    }

    public LibraryPlaylist? CurrentPlaylist
    {
        get
        {
            lock (_stateLock)
            {
                return _currentPlaylist;
            }
        }
    }

    public IReadOnlyList<PlaybackQueueEntry> Queue
    {
        get
        {
            lock (_stateLock)
            {
                return Array.AsReadOnly(_queue.ToArray());
            }
        }
    }

    public PlaybackState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError;
            }
        }
    }

    public long? CurrentPlaybackId
    {
        get
        {
            lock (_stateLock)
            {
                return _currentPlaybackId;
            }
        }
    }

    public TimeSpan? CurrentDuration
    {
        get
        {
            lock (_stateLock)
            {
                return _currentDuration;
            }
        }
    }

    public float MasterGain
    {
        get
        {
            lock (_stateLock)
            {
                return _masterGain;
            }
        }
    }

    public MasterFadeState MasterFadeState
    {
        get
        {
            lock (_stateLock)
            {
                return _masterFadeState;
            }
        }
    }

    public float MusicVolume
    {
        get
        {
            lock (_stateLock)
            {
                return _musicVolume;
            }
        }
    }

    public float AmbienceVolume
    {
        get
        {
            lock (_stateLock)
            {
                return _ambienceVolume;
            }
        }
    }

    public float MasterVolume
    {
        get
        {
            lock (_stateLock)
            {
                return _masterVolume;
            }
        }
    }

    public IReadOnlyList<AmbiencePlaybackSnapshot> Ambience
    {
        get
        {
            lock (_stateLock)
            {
                return Array.AsReadOnly(_ambience.Values.ToArray());
            }
        }
    }

    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return new PlaybackSnapshot(
                    _activePlaylist,
                    _pendingPlaylist,
                    _currentTrack,
                    _currentPlaylist,
                    Array.AsReadOnly(_queue.ToArray()),
                    _state,
                    _lastError,
                    _currentPlaybackId,
                    _currentDuration,
                    _masterGain,
                    _masterFadeState,
                    Array.AsReadOnly(_ambience.Values.ToArray()),
                    _musicVolume,
                    _ambienceVolume,
                    _masterVolume);
            }
        }
    }

    public Task PlayAmbienceAsync(LibraryTrack track, float sourceGain)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidateGain(sourceGain, nameof(sourceGain));
        return ExecuteSerializedAsync(async errors =>
        {
            var identity = GetPathIdentity(track.FilePath);
            AmbiencePlaybackSnapshot? previous;
            lock (_stateLock)
            {
                _ambience.TryGetValue(identity, out previous);
            }

            try
            {
                await _audioEngine.PlayAmbienceAsync(track, sourceGain).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _ambience[identity] = new AmbiencePlaybackSnapshot(
                        identity,
                        previous?.Position ?? TimeSpan.Zero,
                        previous?.Duration ?? TimeSpan.Zero,
                        sourceGain,
                        previous?.LifecycleGain ?? 0f,
                        AmbiencePlaybackState.FadingIn);
                }
            }
            catch (Exception exception)
            {
                AudioProgressSnapshot? progress = null;
                try
                {
                    progress = await _audioEngine.GetProgressAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A source-open failure normally leaves the existing graph
                    // intact. Without progress there is nothing safe to
                    // reconcile here.
                }

                if (progress is not null)
                {
                    lock (_stateLock)
                    {
                        ReconcileEngineProgressNoLock(progress);
                        if (_currentPlaybackId is long currentPlaybackId &&
                            progress.PlaybackId != currentPlaybackId)
                        {
                            ClearCurrentNoLock();
                        }
                    }
                }

                ReportError($"Could not play ambience '{track.Name}'.", exception, errors);
            }

            return true;
        });
    }

    public Task StopAmbienceAsync(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return StopAmbienceAsync(track.FilePath);
    }

    public Task StopAmbienceAsync(string filePath)
    {
        var identity = GetPathIdentity(filePath);
        return ExecuteSerializedAsync(async errors =>
        {
            try
            {
                await _audioEngine.StopAmbienceAsync(identity).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (_ambience.TryGetValue(identity, out var current))
                    {
                        _ambience[identity] = current with
                        {
                            State = AmbiencePlaybackState.FadingOut,
                        };
                    }
                }
            }
            catch (Exception exception)
            {
                ReportError($"Could not stop ambience '{identity}'.", exception, errors);
            }

            return true;
        });
    }

    public Task SetAmbienceSourceGainAsync(LibraryTrack track, float sourceGain)
    {
        ArgumentNullException.ThrowIfNull(track);
        return SetAmbienceSourceGainAsync(track.FilePath, sourceGain);
    }

    public Task SetAmbienceSourceGainAsync(string filePath, float sourceGain)
    {
        ValidateGain(sourceGain, nameof(sourceGain));
        var identity = GetPathIdentity(filePath);
        return ExecuteSerializedAsync(async errors =>
        {
            try
            {
                await _audioEngine.SetAmbienceSourceGainAsync(identity, sourceGain)
                    .ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (_ambience.TryGetValue(identity, out var current))
                    {
                        _ambience[identity] = current with { SourceGain = sourceGain };
                    }
                }
            }
            catch (Exception exception)
            {
                ReportError($"Could not set ambience gain for '{identity}'.", exception, errors);
            }

            return true;
        });
    }

    public Task SetMusicVolumeAsync(float volume) => SetVolumeAsync(
        volume,
        nameof(volume),
        "music",
        (level, cancellationToken) => _audioEngine.SetMusicVolumeAsync(level, cancellationToken),
        value => _musicVolume = value);

    public Task SetAmbienceVolumeAsync(float volume) => SetVolumeAsync(
        volume,
        nameof(volume),
        "ambience",
        (level, cancellationToken) => _audioEngine.SetAmbienceVolumeAsync(level, cancellationToken),
        value => _ambienceVolume = value);

    public Task SetMasterVolumeAsync(float volume) => SetVolumeAsync(
        volume,
        nameof(volume),
        "master",
        (level, cancellationToken) => _audioEngine.SetMasterVolumeAsync(level, cancellationToken),
        value => _masterVolume = value);

    public Task PlayNowAsync(
        LibraryPlaylist playlist,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (transitionMode is not ImmediateTransitionMode.HardCut and
            not ImmediateTransitionMode.Crossfade)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionMode));
        }

        return ExecuteSerializedAsync(async errors =>
        {
            LibraryTrack? previousTrack;
            bool hadCurrentTrack;
            lock (_stateLock)
            {
                previousTrack = _currentTrack;
                hadCurrentTrack = _currentTrack is not null;
                _activePlaylist = playlist;
                _pendingPlaylist = null;
            }

            var started = await TryPlayFromPlaylistAsync(
                playlist,
                previousTrack,
                transitionMode,
                errors).ConfigureAwait(false);
            if (!started)
            {
                if (hadCurrentTrack)
                {
                    await TryStopMusicEngineAsync(errors).ConfigureAwait(false);
                }

                lock (_stateLock)
                {
                    ClearCurrentNoLock();
                }
            }

            return true;
        });
    }

    public Task AfterCurrentAsync(LibraryPlaylist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        return ExecuteSerializedAsync(errors =>
        {
            lock (_stateLock)
            {
                _pendingPlaylist = playlist;
            }

            return Task.FromResult(true);
        });
    }

    public Task QueueTrackAsync(LibraryTrack track, LibraryPlaylist sourcePlaylist)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(sourcePlaylist);

        return ExecuteSerializedAsync(errors =>
        {
            lock (_stateLock)
            {
                _queue.Add(new PlaybackQueueEntry(track, sourcePlaylist));
            }

            return Task.FromResult(true);
        });
    }

    public Task ClearQueueAsync() => ExecuteSerializedAsync(errors =>
    {
        lock (_stateLock)
        {
            if (_queue.Count == 0)
            {
                return Task.FromResult(false);
            }

            _queue.Clear();
        }

        return Task.FromResult(true);
    });

    public Task SkipAsync() => ExecuteSerializedAsync(async errors =>
    {
        LibraryTrack? previousTrack;
        lock (_stateLock)
        {
            if (_currentTrack is null)
            {
                return false;
            }

            previousTrack = _currentTrack;
            ClearCurrentNoLock();
        }

        await TryStopMusicEngineAsync(errors).ConfigureAwait(false);
        PromotePendingPlaylist();
        await TryPlayNextAsync(previousTrack, errors).ConfigureAwait(false);
        return true;
    });

    public Task PauseAsync() => ExecuteSerializedAsync(async errors =>
    {
        lock (_stateLock)
        {
            if (_currentTrack is null || _state != PlaybackState.Playing)
            {
                return false;
            }
        }

        try
        {
            await _audioEngine.PauseAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("Could not pause playback.", exception, errors);
            return true;
        }

        lock (_stateLock)
        {
            _state = PlaybackState.Paused;
        }

        return true;
    });

    public Task ResumeAsync() => ExecuteSerializedAsync(async errors =>
    {
        lock (_stateLock)
        {
            if (_currentTrack is null || _state != PlaybackState.Paused)
            {
                return false;
            }
        }

        try
        {
            await _audioEngine.ResumeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("Could not resume playback.", exception, errors);
            return true;
        }

        lock (_stateLock)
        {
            _state = PlaybackState.Playing;
        }

        return true;
    });

    public Task StopAllAsync() => ExecuteSerializedAsync(async errors =>
    {
        lock (_stateLock)
        {
            ClearCurrentNoLock();
            _pendingPlaylist = null;
            _ambience.Clear();
            ResetMasterNoLock();
        }

        await TryStopEngineAsync(errors).ConfigureAwait(false);

        return true;
    });

    public Task FadeMasterAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration)
    {
        if (direction is not MasterFadeDirection.In and not MasterFadeDirection.Out)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (fullScaleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullScaleDuration),
                "The full-scale fade duration cannot be negative.");
        }

        return ExecuteSerializedAsync(async errors =>
        {
            lock (_stateLock)
            {
                if (_currentTrack is null && _ambience.Count == 0)
                {
                    return false;
                }
            }

            try
            {
                await _audioEngine
                    .FadeMasterAsync(direction, fullScaleDuration)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportError("Could not fade master playback level.", exception, errors);
                return true;
            }

            lock (_stateLock)
            {
                if (direction == MasterFadeDirection.In)
                {
                    if (fullScaleDuration == TimeSpan.Zero)
                    {
                        _masterGain = 1f;
                    }

                    _masterFadeState = _masterGain >= 1f
                        ? MasterFadeState.Full
                        : MasterFadeState.FadingIn;
                }
                else
                {
                    if (fullScaleDuration == TimeSpan.Zero)
                    {
                        _masterGain = 0f;
                    }

                    _masterFadeState = _masterGain <= 0f
                        ? MasterFadeState.Muted
                        : MasterFadeState.FadingOut;
                }
            }

            return true;
        });
    }

    public async Task<AudioProgressSnapshot> GetProgressAsync()
    {
        var errors = new List<PlaybackErrorEventArgs>();
        AudioProgressSnapshot result;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            long? playbackId;
            TimeSpan duration;
            lock (_stateLock)
            {
                playbackId = _currentPlaybackId;
                duration = _currentDuration ?? TimeSpan.Zero;
            }

            try
            {
                var progress = await _audioEngine.GetProgressAsync().ConfigureAwait(false);
                lock (_stateLock)
                {
                    _masterGain = progress.MasterGain;
                    _masterFadeState = progress.MasterFadeState;
                    _musicVolume = progress.MusicVolume;
                    _ambienceVolume = progress.AmbienceVolume;
                    _masterVolume = progress.MasterVolume;
                    ReconcileAmbienceNoLock(progress.AmbienceSnapshots);
                }

                result = progress.PlaybackId == playbackId
                    ? progress
                    : new AudioProgressSnapshot(
                        playbackId,
                        TimeSpan.Zero,
                         duration,
                         progress.MasterGain,
                         progress.MasterFadeState,
                         progress.AmbienceSnapshots,
                         progress.MusicVolume,
                         progress.AmbienceVolume,
                         progress.MasterVolume);
            }
            catch (Exception exception)
            {
                float masterGain;
                MasterFadeState masterFadeState;
                float musicVolume;
                float ambienceVolume;
                float masterVolume;
                IReadOnlyList<AmbiencePlaybackSnapshot> ambience;
                lock (_stateLock)
                {
                    masterGain = _masterGain;
                    masterFadeState = _masterFadeState;
                    musicVolume = _musicVolume;
                    ambienceVolume = _ambienceVolume;
                    masterVolume = _masterVolume;
                    ambience = _ambience.Values.ToArray();
                }

                ReportError("Could not read playback progress.", exception, errors);
                result = new AudioProgressSnapshot(
                    playbackId,
                    TimeSpan.Zero,
                    duration,
                    masterGain,
                    masterFadeState,
                    ambience,
                    musicVolume,
                    ambienceVolume,
                    masterVolume);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseNotifications(errors, errors.Count > 0);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _audioEngine.TrackEnded -= OnTrackEndedAsync;
            _audioEngine.OutputFaulted -= OnOutputFaultedAsync;

            lock (_stateLock)
            {
                ClearCurrentNoLock();
                _pendingPlaylist = null;
                _ambience.Clear();
                ResetMasterNoLock();
            }

            try
            {
                await _audioEngine.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await _audioEngine.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task OnTrackEndedAsync(AudioTrackEndedNotification notification) =>
        ExecuteSerializedAsync(async errors =>
        {
            LibraryTrack? previousTrack;
            lock (_stateLock)
            {
                if (notification.PlaybackId != _currentPlaybackId)
                {
                    return false;
                }

                previousTrack = _currentTrack;
                ClearCurrentNoLock();
            }

            await TryStopMusicEngineAsync(errors).ConfigureAwait(false);
            PromotePendingPlaylist();
            await TryPlayNextAsync(previousTrack, errors).ConfigureAwait(false);
            return true;
        }, ignoreIfDisposed: true);

    private Task OnOutputFaultedAsync(AudioOutputFault fault) =>
        ExecuteSerializedAsync(async errors =>
        {
            var faultMessage = $"Audio output fault: {fault.Message}";
            ReportError(faultMessage, fault.Exception, errors);

            AudioProgressSnapshot? progress = null;
            try
            {
                progress = await _audioEngine.GetProgressAsync().ConfigureAwait(false);
            }
            catch
            {
                // Keep the visible fault and use the event identity as the
                // fallback. A progress read can itself fail during device loss.
            }

            lock (_stateLock)
            {
                if (_currentPlaybackId is long currentPlaybackId &&
                    fault.PlaybackId != currentPlaybackId)
                {
                    // This is a fault from an older physical run. Do not let
                    // its progress (including its transient master gain)
                    // overwrite the newer logical run. Null-id faults retain
                    // the historical behavior of not clearing current music.
                    return true;
                }

                if (progress is not null)
                {
                    _masterGain = progress.MasterGain;
                    _masterFadeState = progress.MasterFadeState;
                    _musicVolume = progress.MusicVolume;
                    _ambienceVolume = progress.AmbienceVolume;
                    _masterVolume = progress.MasterVolume;
                    ReconcileAmbienceNoLock(progress.AmbienceSnapshots);

                    if (_currentPlaybackId is null)
                    {
                        // An ambience-only output run has no music identity.
                        // The engine's empty physical snapshot is therefore the
                        // authoritative indication that the run was lost.
                        if (progress.PlaybackId is null &&
                            progress.AmbienceSnapshots.Count == 0)
                        {
                            _ambience.Clear();
                            ResetMasterNoLock();
                        }
                    }
                    else if (progress.PlaybackId != _currentPlaybackId &&
                             fault.PlaybackId == _currentPlaybackId)
                    {
                        ClearCurrentNoLock();
                        if (progress.PlaybackId is null &&
                            progress.AmbienceSnapshots.Count == 0)
                        {
                            ResetMasterNoLock();
                        }
                    }
                    // A null-id fault is deliberately not allowed to clear a
                    // current music run. This preserves the old delayed-fault
                    // behavior while still reconciling ambience-only faults.
                }
                else if (fault.PlaybackId is long playbackId &&
                         playbackId == _currentPlaybackId)
                {
                    ClearCurrentNoLock();
                    _ambience.Clear();
                    ResetMasterNoLock();
                }
            }

            return true;
        }, ignoreIfDisposed: true);

    private async Task<bool> TryPlayNextAsync(
        LibraryTrack? previousTrack,
        List<PlaybackErrorEventArgs> errors)
    {
        while (true)
        {
            PlaybackQueueEntry? queuedEntry;
            lock (_stateLock)
            {
                if (_queue.Count == 0)
                {
                    queuedEntry = null;
                }
                else
                {
                    queuedEntry = _queue[0];
                    _queue.RemoveAt(0);
                }
            }

            if (queuedEntry is null)
            {
                break;
            }

            if (await TryPlayTrackAsync(
                    queuedEntry.Track,
                    queuedEntry.SourcePlaylist,
                    ImmediateTransitionMode.HardCut,
                    errors).ConfigureAwait(false))
            {
                return true;
            }
        }

        LibraryPlaylist? activePlaylist;
        lock (_stateLock)
        {
            activePlaylist = _activePlaylist;
        }

        return activePlaylist is not null &&
            await TryPlayFromPlaylistAsync(
                activePlaylist,
                previousTrack,
                ImmediateTransitionMode.HardCut,
                errors).ConfigureAwait(false);
    }

    private async Task<bool> TryPlayFromPlaylistAsync(
        LibraryPlaylist playlist,
        LibraryTrack? previousTrack,
        ImmediateTransitionMode transitionMode,
        List<PlaybackErrorEventArgs> errors)
    {
        var candidateCount = playlist.Tracks
            .Select(track => track.FilePath)
            .Distinct(PathComparer)
            .Count();

        if (candidateCount == 0)
        {
            ReportError($"Playlist '{playlist.Name}' contains no playable tracks.", null, errors);
            return false;
        }

        if (!_shuffleBags.TryGetValue(playlist.DirectoryPath, out var shuffleBag))
        {
            shuffleBag = new ShuffleBag(_random);
            _shuffleBags.Add(playlist.DirectoryPath, shuffleBag);
        }

        var attemptedPaths = new HashSet<string>(PathComparer);
        var maximumSelections = candidateCount * 2;
        for (var selection = 0;
             selection < maximumSelections && attemptedPaths.Count < candidateCount;
             selection++)
        {
            var track = shuffleBag.TakeNext(playlist.Tracks, previousTrack);
            if (track is null || !attemptedPaths.Add(track.FilePath))
            {
                continue;
            }

            if (await TryPlayTrackAsync(
                    track,
                    playlist,
                    transitionMode,
                    errors).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> TryPlayTrackAsync(
        LibraryTrack track,
        LibraryPlaylist sourcePlaylist,
        ImmediateTransitionMode transitionMode,
        List<PlaybackErrorEventArgs> errors)
    {
        var playbackId = ++_lastIssuedPlaybackId;
        LibraryTrack? priorTrack;
        LibraryPlaylist? priorPlaylist;
        long? priorPlaybackId;
        TimeSpan? priorDuration;
        PlaybackState priorState;
        bool isReplacement;

        lock (_stateLock)
        {
            priorTrack = _currentTrack;
            priorPlaylist = _currentPlaylist;
            priorPlaybackId = _currentPlaybackId;
            priorDuration = _currentDuration;
            priorState = _state;
            isReplacement = priorTrack is not null;
            _currentPlaybackId = playbackId;
            if (!isReplacement)
            {
                _currentTrack = track;
                _currentPlaylist = sourcePlaylist;
                _currentDuration = null;
                _state = PlaybackState.Playing;
            }
        }

        try
        {
            var playbackInfo = await _audioEngine
                .PlayAsync(track, playbackId, transitionMode)
                .ConfigureAwait(false);
            lock (_stateLock)
            {
                if (_currentPlaybackId == playbackId)
                {
                    _currentTrack = track;
                    _currentPlaylist = sourcePlaylist;
                    _currentDuration = playbackInfo.Duration;
                    _state = PlaybackState.Playing;
                }
            }

            return true;
        }
            catch (Exception exception)
            {
                AudioProgressSnapshot? progress = null;
                try
                {
                    progress = await _audioEngine.GetProgressAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A failed progress read is treated as loss of the prior
                    // physical music source below.
                }

                lock (_stateLock)
                {
                    if (progress is not null)
                    {
                        ReconcileEngineProgressNoLock(progress);
                    }

                    if (_currentPlaybackId == playbackId)
                    {
                        if (progress?.PlaybackId == priorPlaybackId &&
                            priorPlaybackId is not null)
                        {
                            _currentTrack = priorTrack;
                            _currentPlaylist = priorPlaylist;
                            _currentPlaybackId = priorPlaybackId;
                            _currentDuration = priorDuration;
                            _state = priorState;
                        }
                        else
                        {
                            ClearCurrentNoLock();
                        }
                    }
                }

                ReportError(
                $"Could not play '{track.Name}' from '{track.FilePath}'.",
                exception,
                errors);
            return false;
        }
    }

    private async Task TryStopEngineAsync(List<PlaybackErrorEventArgs> errors)
    {
        try
        {
            await _audioEngine.StopAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                ResetMasterNoLock();
            }
        }
        catch (Exception exception)
        {
            ReportError("Could not stop playback.", exception, errors);
        }
    }

    private async Task TryStopMusicEngineAsync(List<PlaybackErrorEventArgs> errors)
    {
        try
        {
            await _audioEngine.StopMusicAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("Could not stop music playback.", exception, errors);
        }
    }

    private Task SetVolumeAsync(
        float volume,
        string parameterName,
        string displayName,
        Func<float, CancellationToken, Task> setEngineVolume,
        Action<float> setControllerVolume)
    {
        ValidateGain(volume, parameterName);
        return ExecuteSerializedAsync(async errors =>
        {
            try
            {
                await setEngineVolume(volume, CancellationToken.None).ConfigureAwait(false);
                lock (_stateLock)
                {
                    setControllerVolume(volume);
                }
            }
            catch (Exception exception)
            {
                ReportError($"Could not set {displayName} volume.", exception, errors);
            }

            return true;
        });
    }

    private void ReconcileAmbienceNoLock(
        IReadOnlyList<AmbiencePlaybackSnapshot> physicalSources)
    {
        var physicalByPath = new Dictionary<string, AmbiencePlaybackSnapshot>(PathComparer);
        foreach (var source in physicalSources)
        {
            var identity = GetPathIdentity(source.FilePath);
            physicalByPath[identity] = source with { FilePath = identity };
        }

        foreach (var identity in _ambience.Keys.ToArray())
        {
            if (!physicalByPath.ContainsKey(identity))
            {
                _ambience.Remove(identity);
            }
        }

        foreach (var pair in physicalByPath)
        {
            if (_ambience.TryGetValue(pair.Key, out var logical) &&
                logical.State == AmbiencePlaybackState.FadingOut &&
                pair.Value.State != AmbiencePlaybackState.FadingOut)
            {
                _ambience[pair.Key] = pair.Value with
                {
                    State = AmbiencePlaybackState.FadingOut,
                };
            }
            else
            {
                _ambience[pair.Key] = pair.Value;
            }
        }
    }

    private void ReconcileEngineProgressNoLock(AudioProgressSnapshot progress)
    {
        _masterGain = progress.MasterGain;
        _masterFadeState = progress.MasterFadeState;
        _musicVolume = progress.MusicVolume;
        _ambienceVolume = progress.AmbienceVolume;
        _masterVolume = progress.MasterVolume;
        ReconcileAmbienceNoLock(progress.AmbienceSnapshots);
    }

    private void PromotePendingPlaylist()
    {
        lock (_stateLock)
        {
            if (_pendingPlaylist is null)
            {
                return;
            }

            _activePlaylist = _pendingPlaylist;
            _pendingPlaylist = null;
        }
    }

    private void ClearCurrentNoLock()
    {
        _currentTrack = null;
        _currentPlaylist = null;
        _currentPlaybackId = null;
        _currentDuration = null;
        _state = PlaybackState.Stopped;
    }

    private void ResetMasterNoLock()
    {
        _masterGain = 1f;
        _masterFadeState = MasterFadeState.Full;
    }

    private static string GetPathIdentity(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    private static void ValidateGain(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Gain must be finite and between zero and one.");
        }
    }

    private void ReportError(
        string message,
        Exception? exception,
        List<PlaybackErrorEventArgs> errors)
    {
        var error = new PlaybackErrorEventArgs(message, exception);
        lock (_stateLock)
        {
            _lastError = message;
        }

        errors.Add(error);
    }

    private async Task ExecuteSerializedAsync(
        Func<List<PlaybackErrorEventArgs>, Task<bool>> operation,
        bool ignoreIfDisposed = false)
    {
        var errors = new List<PlaybackErrorEventArgs>();
        var stateChanged = false;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed && ignoreIfDisposed)
            {
                return;
            }

            ThrowIfDisposed();
            stateChanged = await operation(errors).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        RaiseNotifications(errors, stateChanged);
    }

    private void RaiseNotifications(
        IReadOnlyList<PlaybackErrorEventArgs> errors,
        bool stateChanged)
    {
        foreach (var error in errors)
        {
            ErrorOccurred?.Invoke(this, error);
        }

        if (stateChanged)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
