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
    private float _musicFadeGain = 1f;
    private MasterFadeState _musicFadeState = MasterFadeState.Full;
    private float _ambienceFadeGain = 1f;
    private MasterFadeState _ambienceFadeState = MasterFadeState.Full;
    private long _lastIssuedPlaybackId;
    private bool _stopAllMuted;
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

    public float MasterFadeGain => MasterGain;

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

    public float MusicFadeGain
    {
        get
        {
            lock (_stateLock)
            {
                return _musicFadeGain;
            }
        }
    }

    public MasterFadeState MusicFadeState
    {
        get
        {
            lock (_stateLock)
            {
                return _musicFadeState;
            }
        }
    }

    public float MusicGain => MusicFadeGain;

    public float AmbienceFadeGain
    {
        get
        {
            lock (_stateLock)
            {
                return _ambienceFadeGain;
            }
        }
    }

    public MasterFadeState AmbienceFadeState
    {
        get
        {
            lock (_stateLock)
            {
                return _ambienceFadeState;
            }
        }
    }

    public float AmbienceGain => AmbienceFadeGain;

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

    public Task ConfigureTimingAsync(
        TimeSpan mediumFadeDuration,
        TimeSpan crossfadeStaggerDuration)
    {
        ValidateTiming(mediumFadeDuration, crossfadeStaggerDuration);
        return ExecuteSerializedAsync(async errors =>
        {
            try
            {
                await _audioEngine.ConfigureTimingAsync(
                    mediumFadeDuration,
                    crossfadeStaggerDuration).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportError("Could not configure audio timing.", exception, errors);
            }

            return true;
        });
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
                    _masterVolume,
                    _musicFadeGain,
                    _musicFadeState,
                    _ambienceFadeGain,
                    _ambienceFadeState);
            }
        }
    }

    public Task PlayAmbienceAsync(
        LibraryTrack track,
        float sourceGain,
        TimeSpan? automaticFadeDuration = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidateGain(sourceGain, nameof(sourceGain));
        var fadeDuration = automaticFadeDuration ?? DefaultFastFadeDuration;
        ValidateFadeDuration(fadeDuration, nameof(automaticFadeDuration));
        return ExecuteSerializedAsync(async errors =>
        {
            var identity = GetPathIdentity(track.FilePath);
            bool wasGloballyStopped;
            lock (_stateLock)
            {
                wasGloballyStopped = _currentTrack is null && _ambience.Count == 0;
            }

            if (wasGloballyStopped &&
                !await PrepareAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false))
            {
                return true;
            }

            if (await TryPlayAmbienceCoreAsync(track, sourceGain, errors).ConfigureAwait(false) &&
                wasGloballyStopped)
            {
                await ArmAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false);
            }

            return true;
        });
    }

    /// <summary>
    /// Applies one complete ambience target set as one serialized controller
    /// operation. Duplicate paths use the last target supplied for that path.
    /// </summary>
    public Task<AmbiencePresetApplicationResult> ApplyAmbiencePresetAsync(
        IEnumerable<AmbiencePresetTarget> targets,
        TimeSpan? automaticFadeDuration = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var normalizedTargets = NormalizeAmbiencePresetTargets(targets);
        var fadeDuration = automaticFadeDuration ?? DefaultFastFadeDuration;
        ValidateFadeDuration(fadeDuration, nameof(automaticFadeDuration));

        return ExecuteSerializedResultAsync(async errors =>
        {
            var failures = new List<AmbiencePresetApplicationFailure>();
            var succeededCount = await ApplyAmbiencePresetCoreAsync(
                    normalizedTargets,
                    fadeDuration,
                    errors,
                    failures)
                .ConfigureAwait(false);

            return (true, new AmbiencePresetApplicationResult(
                succeededCount,
                Array.AsReadOnly(failures.ToArray())));
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

    private async Task<bool> TryPlayAmbienceCoreAsync(
        LibraryTrack track,
        float sourceGain,
        List<PlaybackErrorEventArgs> errors,
        Action<Exception>? onFailure = null)
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
                _stopAllMuted = false;
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
            onFailure?.Invoke(exception);
            return false;
        }
    }

    private async Task<int> ApplyAmbiencePresetCoreAsync(
        IReadOnlyList<NormalizedAmbiencePresetTarget> targets,
        TimeSpan automaticFadeDuration,
        List<PlaybackErrorEventArgs> errors,
        List<AmbiencePresetApplicationFailure> failures)
    {
        Dictionary<string, AmbiencePlaybackSnapshot> current;
        bool wasGloballyStopped;
        lock (_stateLock)
        {
            current = new Dictionary<string, AmbiencePlaybackSnapshot>(_ambience, PathComparer);
            wasGloballyStopped = _currentTrack is null && _ambience.Count == 0;
        }

        var desired = targets.ToDictionary(
            target => target.Identity,
            target => target,
            PathComparer);

        // Stop sources that are currently on before touching shared sources or
        // opening new sources. A source already fading out is already following
        // the desired off transition and does not need another stop command.
        foreach (var source in current.Values)
        {
            if (source.State is not (AmbiencePlaybackState.FadingIn or AmbiencePlaybackState.Playing) ||
                desired.ContainsKey(source.FilePath))
            {
                continue;
            }

            await TryStopAmbienceForPresetAsync(source.FilePath, errors, failures)
                .ConfigureAwait(false);
        }

        var newSources = targets
            .Where(target => !current.ContainsKey(target.Identity))
            .ToArray();
        var succeededCount = 0;
        foreach (var target in targets.Where(target => current.ContainsKey(target.Identity)))
        {
            var source = current[target.Identity];
            if (source.State == AmbiencePlaybackState.FadingOut)
            {
                var reversed = await TryPlayAmbienceForPresetAsync(
                        target,
                        errors,
                        failures)
                    .ConfigureAwait(false);
                succeededCount += reversed ? 1 : 0;
                continue;
            }

            if (source.State is (AmbiencePlaybackState.FadingIn or AmbiencePlaybackState.Playing) &&
                await TrySetAmbienceGainForPresetAsync(target, errors, failures)
                    .ConfigureAwait(false))
            {
                succeededCount++;
            }
        }

        var canStartNewSources = true;
        if (wasGloballyStopped && newSources.Length > 0)
        {
            canStartNewSources = await PrepareAutomaticFadeInAsync(
                    automaticFadeDuration,
                    errors)
                .ConfigureAwait(false);
        }

        var startedNewSource = false;
        foreach (var target in newSources)
        {
            if (!canStartNewSources)
            {
                AddPresetFailure(
                    failures,
                    target.Identity,
                    "Could not start ambience because playback fade-in preparation failed.",
                    null);
                continue;
            }

            if (await TryPlayAmbienceForPresetAsync(target, errors, failures)
                    .ConfigureAwait(false))
            {
                succeededCount++;
                startedNewSource = true;
            }
        }

        // A preset may include only reversals/retargets. Those sources are
        // already physical and must not cause an automatic master fade.
        if (wasGloballyStopped && startedNewSource)
        {
            await ArmAutomaticFadeInAsync(automaticFadeDuration, errors).ConfigureAwait(false);
        }

        return succeededCount;
    }

    private async Task<bool> TryStopAmbienceForPresetAsync(
        string identity,
        List<PlaybackErrorEventArgs> errors,
        List<AmbiencePresetApplicationFailure> failures)
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

            return true;
        }
        catch (Exception exception)
        {
            var message = $"Could not stop ambience '{identity}'.";
            ReportError(message, exception, errors);
            AddPresetFailure(failures, identity, message, exception);
            return false;
        }
    }

    private async Task<bool> TrySetAmbienceGainForPresetAsync(
        NormalizedAmbiencePresetTarget target,
        List<PlaybackErrorEventArgs> errors,
        List<AmbiencePresetApplicationFailure> failures)
    {
        try
        {
            await _audioEngine.SetAmbienceSourceGainAsync(target.Identity, target.SourceGain)
                .ConfigureAwait(false);
            lock (_stateLock)
            {
                if (_ambience.TryGetValue(target.Identity, out var current))
                {
                    _ambience[target.Identity] = current with { SourceGain = target.SourceGain };
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            var message = $"Could not set ambience gain for '{target.Identity}'.";
            ReportError(message, exception, errors);
            AddPresetFailure(failures, target.Identity, message, exception);
            return false;
        }
    }

    private async Task<bool> TryPlayAmbienceForPresetAsync(
        NormalizedAmbiencePresetTarget target,
        List<PlaybackErrorEventArgs> errors,
        List<AmbiencePresetApplicationFailure> failures)
    {
        var track = new LibraryTrack(GetTrackName(target.Identity), target.Identity);
        if (await TryPlayAmbienceCoreAsync(
                track,
                target.SourceGain,
                errors,
                exception => AddPresetFailure(
                    failures,
                    target.Identity,
                    $"Could not play ambience '{track.Name}'.",
                    exception))
            .ConfigureAwait(false))
        {
            return true;
        }

        return false;
    }

    private static void AddPresetFailure(
        List<AmbiencePresetApplicationFailure> failures,
        string identity,
        string message,
        Exception? exception) => failures.Add(new(identity, message, exception));

    private static string GetTrackName(string identity)
    {
        var name = Path.GetFileNameWithoutExtension(identity);
        return string.IsNullOrWhiteSpace(name) ? identity : name;
    }

    private static IReadOnlyList<NormalizedAmbiencePresetTarget> NormalizeAmbiencePresetTargets(
        IEnumerable<AmbiencePresetTarget> targets)
    {
        var normalized = new List<NormalizedAmbiencePresetTarget>();
        var indices = new Dictionary<string, int>(PathComparer);
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            var identity = GetPathIdentity(target.FilePath);
            ValidateGain(target.SourceGain, nameof(target.SourceGain));
            var normalizedTarget = new NormalizedAmbiencePresetTarget(
                identity,
                target.SourceGain);

            // Assignment replaces the target while retaining the first path's
            // stable position. Thus duplicates collapse case-insensitively and
            // the last requested gain wins deterministically.
            if (indices.TryGetValue(identity, out var index))
            {
                normalized[index] = normalizedTarget;
            }
            else
            {
                indices.Add(identity, normalized.Count);
                normalized.Add(normalizedTarget);
            }
        }

        return normalized.AsReadOnly();
    }

    private sealed record NormalizedAmbiencePresetTarget(string Identity, float SourceGain)
    {
        public string FilePath => Identity;
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
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        TimeSpan? automaticFadeDuration = null)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ValidateTransitionMode(transitionMode);
        var fadeDuration = automaticFadeDuration ?? DefaultFastFadeDuration;
        ValidateFadeDuration(fadeDuration, nameof(automaticFadeDuration));

        return ExecuteSerializedAsync(async errors =>
        {
            LibraryTrack? previousTrack;
            bool hadCurrentTrack;
            bool wasGloballyStopped;
            lock (_stateLock)
            {
                previousTrack = _currentTrack;
                hadCurrentTrack = _currentTrack is not null;
                wasGloballyStopped = _currentTrack is null && _ambience.Count == 0;
                _activePlaylist = playlist;
                _pendingPlaylist = null;
            }

            if (wasGloballyStopped &&
                !await PrepareAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false))
            {
                return true;
            }

            var started = await TryPlayFromPlaylistAsync(
                playlist,
                previousTrack,
                transitionMode,
                errors).ConfigureAwait(false);
            if (started && wasGloballyStopped)
            {
                await ArmAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false);
            }

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

    public Task PlayNowAsync(
        LibraryTrack track,
        LibraryPlaylist sourcePlaylist,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        TimeSpan? automaticFadeDuration = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(sourcePlaylist);
        ValidateTransitionMode(transitionMode);
        var fadeDuration = automaticFadeDuration ?? DefaultFastFadeDuration;
        ValidateFadeDuration(fadeDuration, nameof(automaticFadeDuration));

        return ExecuteSerializedAsync(async errors =>
        {
            LibraryTrack? previousTrack;
            bool hadCurrentTrack;
            bool wasGloballyStopped;
            lock (_stateLock)
            {
                previousTrack = _currentTrack;
                hadCurrentTrack = _currentTrack is not null;
                wasGloballyStopped = _currentTrack is null && _ambience.Count == 0;
                _activePlaylist = sourcePlaylist;
                _pendingPlaylist = null;
            }

            if (wasGloballyStopped &&
                !await PrepareAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false))
            {
                return true;
            }

            var started = await TryPlayTrackAsync(
                track,
                sourcePlaylist,
                transitionMode,
                errors).ConfigureAwait(false);
            if (started)
            {
                if (wasGloballyStopped)
                {
                    await ArmAutomaticFadeInAsync(fadeDuration, errors).ConfigureAwait(false);
                }

                return true;
            }

            if (hadCurrentTrack)
            {
                await TryStopMusicEngineAsync(errors).ConfigureAwait(false);
            }

            lock (_stateLock)
            {
                ClearCurrentNoLock();
            }

            return true;
        });
    }

    public Task PlayNowAsync(
        LibraryPlaylist sourcePlaylist,
        LibraryTrack track,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        TimeSpan? automaticFadeDuration = null) =>
        PlayNowAsync(track, sourcePlaylist, transitionMode, automaticFadeDuration);

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
        return await StopAllCoreAsync(DefaultFastFadeDuration, errors).ConfigureAwait(false);
    });

    public Task StopAllAsync(TimeSpan fullScaleDuration)
    {
        ValidateFadeDuration(fullScaleDuration, nameof(fullScaleDuration));
        return ExecuteSerializedAsync(errors => StopAllCoreAsync(fullScaleDuration, errors));
    }

    public Task FadeMusicAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        return ExecuteSerializedAsync(async errors =>
        {
            bool hasMusic;
            lock (_stateLock)
            {
                hasMusic = _currentTrack is not null;
            }

            if (!hasMusic)
            {
                return false;
            }

            try
            {
                await _audioEngine.FadeMusicAsync(direction, fullScaleDuration)
                    .ConfigureAwait(false);
                lock (_stateLock)
                {
                    UpdateFadeStateNoLock(
                        direction,
                        fullScaleDuration,
                        ref _musicFadeGain,
                        ref _musicFadeState);
                }
            }
            catch (Exception exception)
            {
                ReportError("Could not fade music playback level.", exception, errors);
            }

            return true;
        });
    }

    public Task FadeAmbienceAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        return ExecuteSerializedAsync(async errors =>
        {
            bool hasAmbience;
            lock (_stateLock)
            {
                hasAmbience = _ambience.Count > 0;
            }

            if (!hasAmbience)
            {
                return false;
            }

            try
            {
                await _audioEngine.FadeAmbienceAsync(direction, fullScaleDuration)
                    .ConfigureAwait(false);
                lock (_stateLock)
                {
                    UpdateFadeStateNoLock(
                        direction,
                        fullScaleDuration,
                        ref _ambienceFadeGain,
                        ref _ambienceFadeState);
                }
            }
            catch (Exception exception)
            {
                ReportError("Could not fade ambience playback level.", exception, errors);
            }

            return true;
        });
    }

    public Task ArmMusicFadeAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration) => FadeMusicAsync(direction, fullScaleDuration);

    public Task ArmAmbienceFadeAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration) => FadeAmbienceAsync(direction, fullScaleDuration);

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
                    _musicFadeGain = progress.MusicFadeGain;
                    _musicFadeState = progress.MusicFadeState;
                    _ambienceFadeGain = progress.AmbienceFadeGain;
                    _ambienceFadeState = progress.AmbienceFadeState;
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
                          progress.MasterVolume,
                          progress.MusicFadeGain,
                          progress.MusicFadeState,
                          progress.AmbienceFadeGain,
                          progress.AmbienceFadeState);
            }
            catch (Exception exception)
            {
                float masterGain;
                MasterFadeState masterFadeState;
                float musicVolume;
                float ambienceVolume;
                float masterVolume;
                float musicFadeGain;
                MasterFadeState musicFadeState;
                float ambienceFadeGain;
                MasterFadeState ambienceFadeState;
                IReadOnlyList<AmbiencePlaybackSnapshot> ambience;
                lock (_stateLock)
                {
                    masterGain = _masterGain;
                    masterFadeState = _masterFadeState;
                    musicVolume = _musicVolume;
                    ambienceVolume = _ambienceVolume;
                    masterVolume = _masterVolume;
                    musicFadeGain = _musicFadeGain;
                    musicFadeState = _musicFadeState;
                    ambienceFadeGain = _ambienceFadeGain;
                    ambienceFadeState = _ambienceFadeState;
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
                    masterVolume,
                    musicFadeGain,
                    musicFadeState,
                    ambienceFadeGain,
                    ambienceFadeState);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseNotifications(errors, errors.Count > 0);
        return result;
    }

    private async Task<bool> StopAllCoreAsync(
        TimeSpan fullScaleDuration,
        List<PlaybackErrorEventArgs> errors)
    {
        lock (_stateLock)
        {
            if (_currentTrack is null &&
                _pendingPlaylist is null &&
                _ambience.Count == 0 &&
                _masterGain <= 0f)
            {
                return false;
            }
        }

        // A fade may have advanced physically since the last UI progress poll.
        // Read the rendered master gain before invalidating logical state so
        // Stop All starts at the actual audible level.
        try
        {
            var progress = await _audioEngine.GetProgressAsync().ConfigureAwait(false);
            lock (_stateLock)
            {
                _masterGain = progress.MasterGain;
                _masterFadeState = progress.MasterFadeState;
            }
        }
        catch (Exception exception)
        {
            ReportError("Could not read playback level before stopping.", exception, errors);
        }

        bool hasAudibleSources;
        bool masterAlreadyMuted;
        lock (_stateLock)
        {
            hasAudibleSources = (_currentTrack is not null && _state == PlaybackState.Playing) ||
                _ambience.Count > 0;
            masterAlreadyMuted = _masterGain <= 0f;

            // Invalidate the logical run before touching the physical graph.
            // Completion and fault notifications that were already queued now
            // cannot advance a playlist or resurrect a source.
            ClearCurrentNoLock();
            _pendingPlaylist = null;
            _ambience.Clear();
            _stopAllMuted = true;
        }

        if (hasAudibleSources && !masterAlreadyMuted)
        {
            try
            {
                await _audioEngine.FadeMasterAndWaitAsync(
                    MasterFadeDirection.Out,
                    fullScaleDuration).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _masterGain = 0f;
                    _masterFadeState = MasterFadeState.Muted;
                }
            }
            catch (Exception exception)
            {
                ReportError("Could not fade playback before stopping.", exception, errors);
                await TryMuteMasterImmediatelyAsync(errors).ConfigureAwait(false);
            }
        }
        else if (!masterAlreadyMuted)
        {
            try
            {
                // This is also the paused-only path: never resume a paused
                // source merely to fade it out.
                await _audioEngine.FadeMasterAsync(
                    MasterFadeDirection.Out,
                    TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ReportError("Could not mute playback before stopping.", exception, errors);
            }

            lock (_stateLock)
            {
                _masterGain = 0f;
                _masterFadeState = MasterFadeState.Muted;
            }
        }

        try
        {
            await _audioEngine.StopSourcesPreservingMasterFadeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("Could not stop playback sources.", exception, errors);
        }

        lock (_stateLock)
        {
            _masterGain = 0f;
            _masterFadeState = MasterFadeState.Muted;
        }

        return true;
    }

    private async Task<bool> PrepareAutomaticFadeInAsync(
        TimeSpan fullScaleDuration,
        List<PlaybackErrorEventArgs> errors)
    {
        try
        {
            // Arm the zero endpoint, but do not wait for an audio callback.
            await _audioEngine.FadeMasterAsync(
                MasterFadeDirection.Out,
                TimeSpan.Zero).ConfigureAwait(false);
            lock (_stateLock)
            {
                _masterGain = 0f;
                _masterFadeState = MasterFadeState.Muted;
            }

            return true;
        }
        catch (Exception exception)
        {
            ReportError("Could not prepare playback fade-in.", exception, errors);
            lock (_stateLock)
            {
                _masterGain = 0f;
                _masterFadeState = MasterFadeState.Muted;
            }

            return false;
        }
    }

    private async Task ArmAutomaticFadeInAsync(
        TimeSpan fullScaleDuration,
        List<PlaybackErrorEventArgs> errors)
    {
        try
        {
            await _audioEngine.FadeMasterAsync(
                MasterFadeDirection.In,
                fullScaleDuration).ConfigureAwait(false);
            lock (_stateLock)
            {
                _masterFadeState = fullScaleDuration == TimeSpan.Zero
                    ? MasterFadeState.Full
                    : MasterFadeState.FadingIn;
                if (fullScaleDuration == TimeSpan.Zero)
                {
                    _masterGain = 1f;
                }
            }
        }
        catch (Exception exception)
        {
            ReportError("Could not arm playback fade-in.", exception, errors);
        }
    }

    private async Task TryMuteMasterImmediatelyAsync(List<PlaybackErrorEventArgs> errors)
    {
        try
        {
            await _audioEngine.FadeMasterAsync(
                MasterFadeDirection.Out,
                TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ReportError("Could not mute playback after fade failure.", exception, errors);
        }

        lock (_stateLock)
        {
            _masterGain = 0f;
            _masterFadeState = MasterFadeState.Muted;
        }
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

                if (_stopAllMuted && _currentPlaybackId is null)
                {
                    _ambience.Clear();
                    _masterGain = 0f;
                    _masterFadeState = MasterFadeState.Muted;
                    return true;
                }

                if (progress is not null)
                {
                    _masterGain = progress.MasterGain;
                    _masterFadeState = progress.MasterFadeState;
                    _musicVolume = progress.MusicVolume;
                    _ambienceVolume = progress.AmbienceVolume;
                    _masterVolume = progress.MasterVolume;
                    _musicFadeGain = progress.MusicFadeGain;
                    _musicFadeState = progress.MusicFadeState;
                    _ambienceFadeGain = progress.AmbienceFadeGain;
                    _ambienceFadeState = progress.AmbienceFadeState;
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
                    _stopAllMuted = false;
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
            if (_ambience.TryGetValue(pair.Key, out var logical))
            {
                _ambience[pair.Key] = pair.Value with
                {
                    // The engine reports the gain currently rendered by the
                    // sample envelope. Keep the requested value here so a
                    // source that is stopped mid-ramp remembers its target.
                    SourceGain = logical.SourceGain,
                    State = logical.State == AmbiencePlaybackState.FadingOut ||
                        pair.Value.State == AmbiencePlaybackState.FadingOut
                        ? AmbiencePlaybackState.FadingOut
                        : pair.Value.State,
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
        _musicFadeGain = progress.MusicFadeGain;
        _musicFadeState = progress.MusicFadeState;
        _ambienceFadeGain = progress.AmbienceFadeGain;
        _ambienceFadeState = progress.AmbienceFadeState;
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
        _musicFadeGain = 1f;
        _musicFadeState = MasterFadeState.Full;
        _ambienceFadeGain = 1f;
        _ambienceFadeState = MasterFadeState.Full;
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

    private static void ValidateTiming(TimeSpan mediumFadeDuration, TimeSpan crossfadeStaggerDuration)
    {
        if (mediumFadeDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mediumFadeDuration),
                "The medium fade duration must be greater than zero.");
        }

        if (crossfadeStaggerDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(crossfadeStaggerDuration),
                "The crossfade stagger cannot be negative.");
        }

        if (crossfadeStaggerDuration > mediumFadeDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(crossfadeStaggerDuration),
                "The crossfade stagger cannot exceed the medium fade duration.");
        }
    }

    private static readonly TimeSpan DefaultFastFadeDuration =
        TimeSpan.FromSeconds(AppSettings.DefaultFastFadeSeconds);

    private static void ValidateTransitionMode(ImmediateTransitionMode transitionMode)
    {
        if (transitionMode is not ImmediateTransitionMode.HardCut and
            not ImmediateTransitionMode.Crossfade)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionMode));
        }
    }

    private static void ValidateFadeDuration(TimeSpan duration, string parameterName)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The fade duration cannot be negative.");
        }
    }

    private static void ValidateFadeArguments(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration)
    {
        if (direction is not MasterFadeDirection.In and not MasterFadeDirection.Out)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        ValidateFadeDuration(fullScaleDuration, nameof(fullScaleDuration));
    }

    private static void UpdateFadeStateNoLock(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        ref float gain,
        ref MasterFadeState state)
    {
        if (fullScaleDuration == TimeSpan.Zero)
        {
            gain = direction == MasterFadeDirection.In ? 1f : 0f;
            state = direction == MasterFadeDirection.In
                ? MasterFadeState.Full
                : MasterFadeState.Muted;
            return;
        }

        state = direction == MasterFadeDirection.In
            ? MasterFadeState.FadingIn
            : MasterFadeState.FadingOut;
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

    private async Task<TResult> ExecuteSerializedResultAsync<TResult>(
        Func<List<PlaybackErrorEventArgs>, Task<(bool StateChanged, TResult Result)>> operation)
    {
        var errors = new List<PlaybackErrorEventArgs>();
        TResult result;
        var stateChanged = false;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var operationResult = await operation(errors).ConfigureAwait(false);
            stateChanged = operationResult.StateChanged;
            result = operationResult.Result;
        }
        finally
        {
            _gate.Release();
        }

        RaiseNotifications(errors, stateChanged);
        return result;
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
