using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

internal sealed record PlayRequest(
    LibraryTrack Track,
    long PlaybackId,
    ImmediateTransitionMode TransitionMode);

internal sealed record FadeRequest(
    MasterFadeDirection Direction,
    TimeSpan FullScaleDuration);

internal sealed record AmbiencePlayRequest(LibraryTrack Track, float SourceGain);

internal sealed record AmbienceGainRequest(string FilePath, float SourceGain);

internal sealed record VolumeRequest(string Kind, float Volume);

internal sealed class FakeAudioEngine : IAudioEngine
{
    private readonly object _sync = new();
    private readonly List<PlayRequest> _playRequests = [];
    private readonly List<FadeRequest> _fadeRequests = [];
    private readonly List<AmbiencePlayRequest> _ambiencePlayRequests = [];
    private readonly List<string> _ambienceStopRequests = [];
    private readonly List<AmbienceGainRequest> _ambienceGainRequests = [];
    private readonly List<VolumeRequest> _volumeRequests = [];
    private readonly Dictionary<string, AmbiencePlaybackSnapshot> _ambience =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, Task> _trackEndAfterPlayTasks = [];
    private long? _currentPlaybackId;
    private TimeSpan _currentDuration;

    public event AudioTrackEndedHandler? TrackEnded;

    public event AudioOutputFaultedHandler? OutputFaulted;

    public IReadOnlyList<PlayRequest> PlayRequests
    {
        get
        {
            lock (_sync)
            {
                return _playRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<FadeRequest> FadeRequests
    {
        get
        {
            lock (_sync)
            {
                return _fadeRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<AmbiencePlayRequest> AmbiencePlayRequests
    {
        get { lock (_sync) { return _ambiencePlayRequests.ToArray(); } }
    }

    public IReadOnlyList<AmbiencePlayRequest> PlayAmbienceRequests => AmbiencePlayRequests;

    public IReadOnlyList<string> AmbienceStopRequests
    {
        get { lock (_sync) { return _ambienceStopRequests.ToArray(); } }
    }

    public IReadOnlyList<string> StopAmbienceRequests => AmbienceStopRequests;

    public IReadOnlyList<AmbienceGainRequest> AmbienceGainRequests
    {
        get { lock (_sync) { return _ambienceGainRequests.ToArray(); } }
    }

    public IReadOnlyList<AmbienceGainRequest> SetAmbienceSourceGainRequests =>
        AmbienceGainRequests;

    public IReadOnlyList<VolumeRequest> VolumeRequests
    {
        get { lock (_sync) { return _volumeRequests.ToArray(); } }
    }

    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSnapshots
    {
        get { lock (_sync) { return _ambience.Values.ToArray(); } }
    }

    public IReadOnlyList<AmbiencePlaybackSnapshot> AmbienceSources => AmbienceSnapshots;

    public HashSet<string> UnreadablePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<long> PlaybackIdsEndingOnPlayCompletion { get; } = [];

    public Func<LibraryTrack, long, Task<AudioPlaybackInfo>>? PlayHandler { get; set; }

    public Func<MasterFadeDirection, TimeSpan, Task>? FadeHandler { get; set; }

    public Action? PauseHandler { get; set; }

    public Action? DisposeHandler { get; set; }

    public Exception? PauseException { get; set; }

    public Exception? ResumeException { get; set; }

    public Exception? StopException { get; set; }

    public Exception? StopMusicException { get; set; }

    public Exception? PlayAmbienceException { get; set; }

    public bool ClearPhysicalGraphOnPlayFailure { get; set; }

    public bool ClearPhysicalGraphOnAmbienceFailure { get; set; }

    public bool PlayFailureClearsPhysicalGraph
    {
        get => ClearPhysicalGraphOnPlayFailure;
        set => ClearPhysicalGraphOnPlayFailure = value;
    }

    public bool AmbienceFailureClearsPhysicalGraph
    {
        get => ClearPhysicalGraphOnAmbienceFailure;
        set => ClearPhysicalGraphOnAmbienceFailure = value;
    }

    public Exception? StopAmbienceException { get; set; }

    public Exception? AmbienceGainException { get; set; }

    public Exception? SetAmbienceSourceGainException
    {
        get => AmbienceGainException;
        set => AmbienceGainException = value;
    }

    public Exception? MusicVolumeException { get; set; }

    public Exception? SetMusicVolumeException
    {
        get => MusicVolumeException;
        set => MusicVolumeException = value;
    }

    public Exception? AmbienceVolumeException { get; set; }

    public Exception? SetAmbienceVolumeException
    {
        get => AmbienceVolumeException;
        set => AmbienceVolumeException = value;
    }

    public Exception? MasterVolumeException { get; set; }

    public Exception? SetMasterVolumeException
    {
        get => MasterVolumeException;
        set => MasterVolumeException = value;
    }

    public Exception? ProgressException { get; set; }

    public Exception? FadeException { get; set; }

    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan AmbienceDuration { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan Position { get; set; }

    public float MasterGain { get; set; } = 1f;

    public float MusicVolume { get; set; } = 1f;

    public float AmbienceVolume { get; set; } = 1f;

    public float MasterVolume { get; set; } = 1f;

    public MasterFadeState MasterFadeState { get; set; } = MasterFadeState.Full;

    public int PauseCount { get; private set; }

    public int ResumeCount { get; private set; }

    public int StopCount { get; private set; }

    public int StopMusicCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public Task<AudioPlaybackInfo> PlayAsync(
        LibraryTrack track,
        long playbackId,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _playRequests.Add(new PlayRequest(track, playbackId, transitionMode));
        }

        var playTask = CompletePlayAsync(track, playbackId, cancellationToken);
        if (PlaybackIdsEndingOnPlayCompletion.Contains(playbackId))
        {
            var trackEndTask = playTask.ContinueWith(
                async completedPlay =>
                {
                    await completedPlay.ConfigureAwait(false);
                    await RaiseTrackEndedAsync(playbackId).ConfigureAwait(false);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Unwrap();

            lock (_sync)
            {
                _trackEndAfterPlayTasks.Add(playbackId, trackEndTask);
            }
        }

        return playTask;
    }

    public async Task FadeMasterAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (direction is not MasterFadeDirection.In and not MasterFadeDirection.Out)
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        if (fullScaleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fullScaleDuration));
        }

        lock (_sync)
        {
            _fadeRequests.Add(new FadeRequest(direction, fullScaleDuration));
        }

        if (FadeException is not null)
        {
            throw FadeException;
        }

        if (FadeHandler is not null)
        {
            await FadeHandler(direction, fullScaleDuration).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (fullScaleDuration == TimeSpan.Zero)
            {
                MasterGain = direction == MasterFadeDirection.In ? 1f : 0f;
            }

            MasterFadeState = direction switch
            {
                MasterFadeDirection.In when MasterGain >= 1f => MasterFadeState.Full,
                MasterFadeDirection.In => MasterFadeState.FadingIn,
                MasterFadeDirection.Out when MasterGain <= 0f => MasterFadeState.Muted,
                _ => MasterFadeState.FadingOut,
            };
        }
    }

    public Task GetTrackEndAfterPlayTask(long playbackId)
    {
        lock (_sync)
        {
            return _trackEndAfterPlayTasks[playbackId];
        }
    }

    private void ClearPhysicalGraph()
    {
        lock (_sync)
        {
            _currentPlaybackId = null;
            _currentDuration = TimeSpan.Zero;
            _ambience.Clear();
            MasterGain = 1f;
            MasterFadeState = MasterFadeState.Full;
        }
    }

    private async Task<AudioPlaybackInfo> CompletePlayAsync(
        LibraryTrack track,
        long playbackId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UnreadablePaths.Contains(track.FilePath))
            {
                throw new IOException($"Cannot open {track.FilePath}.");
            }

            var info = PlayHandler is null
                ? new AudioPlaybackInfo(Duration)
                : await PlayHandler(track, playbackId).ConfigureAwait(false);

            lock (_sync)
            {
                _currentPlaybackId = playbackId;
                _currentDuration = info.Duration;
            }

            return info;
        }
        catch
        {
            if (ClearPhysicalGraphOnPlayFailure)
            {
                ClearPhysicalGraph();
            }

            throw;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        PauseCount++;
        PauseHandler?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return PauseException is null ? Task.CompletedTask : Task.FromException(PauseException);
    }

    public Task PlayAmbienceAsync(
        LibraryTrack track,
        float sourceGain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _ambiencePlayRequests.Add(new AmbiencePlayRequest(track, sourceGain));
        }

        if (PlayAmbienceException is not null)
        {
            if (ClearPhysicalGraphOnAmbienceFailure)
            {
                ClearPhysicalGraph();
            }

            return Task.FromException(PlayAmbienceException);
        }

        var identity = Path.GetFullPath(track.FilePath);
        if (UnreadablePaths.Contains(track.FilePath) || UnreadablePaths.Contains(identity))
        {
            if (ClearPhysicalGraphOnAmbienceFailure)
            {
                ClearPhysicalGraph();
            }

            return Task.FromException(new IOException($"Cannot open {track.FilePath}."));
        }

        lock (_sync)
        {
            _ambience[identity] = _ambience.TryGetValue(identity, out var existing)
                ? existing with
                {
                    SourceGain = sourceGain,
                    State = AmbiencePlaybackState.FadingIn,
                }
                : new AmbiencePlaybackSnapshot(
                    identity,
                    TimeSpan.Zero,
                    AmbienceDuration,
                    sourceGain,
                    0f,
                    AmbiencePlaybackState.FadingIn);
        }

        return Task.CompletedTask;
    }

    public Task StopAmbienceAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Path.GetFullPath(filePath);
        lock (_sync)
        {
            _ambienceStopRequests.Add(identity);
        }

        if (StopAmbienceException is not null)
        {
            return Task.FromException(StopAmbienceException);
        }

        lock (_sync)
        {
            if (_ambience.TryGetValue(identity, out var current))
            {
                _ambience[identity] = current with
                {
                    State = AmbiencePlaybackState.FadingOut,
                };
            }
        }

        return Task.CompletedTask;
    }

    public Task SetAmbienceSourceGainAsync(
        string filePath,
        float sourceGain,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = Path.GetFullPath(filePath);
        lock (_sync)
        {
            _ambienceGainRequests.Add(new AmbienceGainRequest(identity, sourceGain));
        }

        if (AmbienceGainException is not null)
        {
            return Task.FromException(AmbienceGainException);
        }

        lock (_sync)
        {
            if (_ambience.TryGetValue(identity, out var current))
            {
                _ambience[identity] = current with { SourceGain = sourceGain };
            }
        }

        return Task.CompletedTask;
    }

    public Task SetMusicVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { _volumeRequests.Add(new VolumeRequest("music", volume)); }
        if (MusicVolumeException is not null)
        {
            return Task.FromException(MusicVolumeException);
        }

        MusicVolume = volume;
        return Task.CompletedTask;
    }

    public Task SetAmbienceVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { _volumeRequests.Add(new VolumeRequest("ambience", volume)); }
        if (AmbienceVolumeException is not null)
        {
            return Task.FromException(AmbienceVolumeException);
        }

        AmbienceVolume = volume;
        return Task.CompletedTask;
    }

    public Task SetMasterVolumeAsync(float volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync) { _volumeRequests.Add(new VolumeRequest("master", volume)); }
        if (MasterVolumeException is not null)
        {
            return Task.FromException(MasterVolumeException);
        }

        MasterVolume = volume;
        return Task.CompletedTask;
    }

    public Task StopMusicAsync(CancellationToken cancellationToken = default)
    {
        StopMusicCount++;
        // Keep the legacy aggregate counter useful to the pre-Milestone-4
        // view-model tests; StopMusicCount remains the operation-specific
        // counter used by the new controller tests.
        StopCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (StopMusicException is not null)
        {
            return Task.FromException(StopMusicException);
        }

        lock (_sync)
        {
            _currentPlaybackId = null;
            _currentDuration = TimeSpan.Zero;
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ResumeCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return ResumeException is null ? Task.CompletedTask : Task.FromException(ResumeException);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (StopException is not null)
        {
            return Task.FromException(StopException);
        }

        lock (_sync)
        {
            _currentPlaybackId = null;
            _currentDuration = TimeSpan.Zero;
            _ambience.Clear();
            MasterGain = 1f;
            MasterFadeState = MasterFadeState.Full;
        }

        return Task.CompletedTask;
    }

    public Task<AudioProgressSnapshot> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ProgressException is not null)
        {
            return Task.FromException<AudioProgressSnapshot>(ProgressException);
        }

        lock (_sync)
        {
            var ambience = _ambience.Values.ToArray();
            return Task.FromResult(new AudioProgressSnapshot(
                _currentPlaybackId,
                Position,
                _currentDuration,
                MasterGain,
                MasterFadeState,
                ambience,
                MusicVolume,
                AmbienceVolume,
                MasterVolume));
        }
    }

    public async Task RaiseTrackEndedAsync(long playbackId)
    {
        var handlers = TrackEnded;
        if (handlers is null)
        {
            return;
        }

        foreach (AudioTrackEndedHandler handler in handlers.GetInvocationList())
        {
            await handler(new AudioTrackEndedNotification(playbackId)).ConfigureAwait(false);
        }
    }

    public async Task RaiseOutputFaultAsync(AudioOutputFault fault)
    {
        if (fault.PlaybackId is long playbackId)
        {
            lock (_sync)
            {
                if (_currentPlaybackId is null || playbackId == _currentPlaybackId)
                {
                    if (playbackId == _currentPlaybackId)
                    {
                        _currentPlaybackId = null;
                        _currentDuration = TimeSpan.Zero;
                    }

                    _ambience.Clear();
                    MasterGain = 1f;
                    MasterFadeState = MasterFadeState.Full;
                }
            }
        }
        else
        {
            lock (_sync)
            {
                if (_currentPlaybackId is null)
                {
                    _ambience.Clear();
                    MasterGain = 1f;
                    MasterFadeState = MasterFadeState.Full;
                }
            }
        }

        var handlers = OutputFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (AudioOutputFaultedHandler handler in handlers.GetInvocationList())
        {
            await handler(fault).ConfigureAwait(false);
        }
    }

    public void CompleteAmbienceTransition(string filePath)
    {
        var identity = Path.GetFullPath(filePath);
        lock (_sync)
        {
            if (!_ambience.TryGetValue(identity, out var current))
            {
                return;
            }

            if (current.State == AmbiencePlaybackState.FadingOut)
            {
                _ambience.Remove(identity);
            }
            else
            {
                _ambience[identity] = current with
                {
                    LifecycleGain = 1f,
                    State = AmbiencePlaybackState.Playing,
                };
            }
        }
    }

    public void CompleteAmbienceFade(string filePath) => CompleteAmbienceTransition(filePath);

    public void RemoveAmbiencePhysically(string filePath)
    {
        var identity = Path.GetFullPath(filePath);
        lock (_sync)
        {
            _ambience.Remove(identity);
        }
    }

    public void ClearAmbiencePhysically()
    {
        lock (_sync)
        {
            _ambience.Clear();
        }
    }

    public void SetAmbienceProgress(string filePath, TimeSpan position, float lifecycleGain)
    {
        var identity = Path.GetFullPath(filePath);
        lock (_sync)
        {
            if (_ambience.TryGetValue(identity, out var current))
            {
                _ambience[identity] = current with { Position = position, LifecycleGain = lifecycleGain };
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeHandler?.Invoke();
        lock (_sync)
        {
            _ambience.Clear();
            _currentPlaybackId = null;
            _currentDuration = TimeSpan.Zero;
        }
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}
