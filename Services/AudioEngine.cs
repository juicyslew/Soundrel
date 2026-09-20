using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Soundrel.Models;

[assembly: InternalsVisibleTo("Soundrel.Tests")]

namespace Soundrel.Services;

public sealed class AudioEngine : IAudioEngine
{
    private static readonly WaveFormat MixerFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    private static readonly TimeSpan DefaultMediumFadeDuration =
        TimeSpan.FromSeconds(AppSettings.DefaultMediumFadeSeconds);
    private static readonly TimeSpan DefaultCrossfadeStagger =
        TimeSpan.FromSeconds(AppSettings.DefaultCrossfadeStaggerSeconds);

    private readonly Func<IWavePlayer> _outputFactory;
    private readonly Func<string, long, Action<AudioTrackSource>, AudioTrackSource> _sourceFactory;
    private readonly MixingSampleProvider _mixer;
    private readonly MixingSampleProvider _musicBus;
    private readonly MixingSampleProvider _ambienceBus;
    private readonly PauseGateSampleProvider _musicPauseGate;
    private readonly GainEnvelopeSampleProvider _musicVolume;
    private readonly GainEnvelopeSampleProvider _musicFade;
    private readonly GainEnvelopeSampleProvider _ambienceVolume;
    private readonly GainEnvelopeSampleProvider _ambienceFade;
    private readonly GainEnvelopeSampleProvider _masterVolume;
    private readonly GainEnvelopeSampleProvider _masterEnvelope;
    private readonly IWaveProvider _waveProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _outputCommandGate = new();
    private readonly object _faultedOutputGate = new();
    private readonly List<WeakReference<IWavePlayer>> _faultedOutputs = [];
    private IWavePlayer? _output;
    private MusicSlot? _currentSlot;
    private MusicSlot? _outgoingSlot;
    private readonly Dictionary<string, AmbienceSlot> _ambienceSlots =
        new(StringComparer.OrdinalIgnoreCase);
    private MasterFadeState _masterFadeState = MasterFadeState.Full;
    private MasterFadeState _musicFadeState = MasterFadeState.Full;
    private MasterFadeState _ambienceFadeState = MasterFadeState.Full;
    private long _masterFadeVersion;
    private long _musicFadeVersion;
    private long _ambienceFadeVersion;
    private TaskCompletionSource? _masterFadeCompletion;
    private long _playbackRunGeneration;
    private long _faultNotificationGeneration;
    private PlaybackFaultIdentity _playbackFaultIdentity = new(null, null, 0);
    private OutputCommandContext? _activeOutputCommand;
    private bool _paused;
    private bool _disposed;
    private long _nextAmbiencePlaybackId;
    private TimeSpan _mediumFadeDuration = DefaultMediumFadeDuration;
    private TimeSpan _crossfadeStagger = DefaultCrossfadeStagger;
    private float _musicVolumeTarget = 1f;
    private float _ambienceVolumeTarget = 1f;
    private float _masterVolumeTarget = 1f;

    public AudioEngine()
#pragma warning disable CS0618 // Milestone 3 explicitly requires one shared-mode WasapiOut.
        : this(static () => new WasapiOut(
            AudioClientShareMode.Shared,
            useEventSync: true,
            latency: 100))
#pragma warning restore CS0618
    {
    }

    internal AudioEngine(Func<IWavePlayer> outputFactory)
        : this(outputFactory, AudioTrackSource.Open)
    {
    }

    internal AudioEngine(
        Func<IWavePlayer> outputFactory,
        Func<string, long, Action<AudioTrackSource>, AudioTrackSource> sourceFactory)
    {
        ArgumentNullException.ThrowIfNull(outputFactory);
        ArgumentNullException.ThrowIfNull(sourceFactory);

        _outputFactory = outputFactory;
        _sourceFactory = sourceFactory;
        _musicBus = new MixingSampleProvider(MixerFormat)
        {
            ReadFully = true,
        };
        _ambienceBus = new MixingSampleProvider(MixerFormat)
        {
            ReadFully = true,
        };
        _mixer = new MixingSampleProvider(MixerFormat)
        {
            ReadFully = true,
        };
        _musicPauseGate = new PauseGateSampleProvider(_musicBus);
        _musicVolume = new GainEnvelopeSampleProvider(_musicPauseGate);
        _musicFade = new GainEnvelopeSampleProvider(_musicVolume);
        _ambienceVolume = new GainEnvelopeSampleProvider(_ambienceBus);
        _ambienceFade = new GainEnvelopeSampleProvider(_ambienceVolume);
        _masterVolume = new GainEnvelopeSampleProvider(_mixer);
        _mixer.AddMixerInput(_musicFade);
        _mixer.AddMixerInput(_ambienceFade);
        _masterEnvelope = new GainEnvelopeSampleProvider(_masterVolume);
        _waveProvider = new SampleSafetySampleProvider(_masterEnvelope).ToWaveProvider();
    }

    public event AudioTrackEndedHandler? TrackEnded;

    public event AudioOutputFaultedHandler? OutputFaulted;

    public async Task ConfigureTimingAsync(
        TimeSpan mediumFadeDuration,
        TimeSpan crossfadeStaggerDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateTiming(mediumFadeDuration, crossfadeStaggerDuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _mediumFadeDuration = mediumFadeDuration;
            _crossfadeStagger = crossfadeStaggerDuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal int MusicInputCount => _musicBus.MixerInputs.Count();

    internal int AmbienceInputCount => _ambienceBus.MixerInputs.Count();

    internal long? CurrentPlaybackId => Volatile.Read(ref _currentSlot)?.Source.PlaybackId;

    internal long? OutgoingPlaybackId => Volatile.Read(ref _outgoingSlot)?.Source.PlaybackId;

    internal float CurrentMusicGain => Volatile.Read(ref _currentSlot)?.Envelope.CurrentGain ?? 0f;

    internal float OutgoingMusicGain => Volatile.Read(ref _outgoingSlot)?.Envelope.CurrentGain ?? 0f;

    internal float CurrentMusicVolumeGain => _musicVolume.CurrentGain;

    internal float CurrentAmbienceVolumeGain => _ambienceVolume.CurrentGain;

    internal float CurrentMasterVolumeGain => _masterVolume.CurrentGain;

    internal long GetAmbienceTransitionVersionForTests(string filePath)
    {
        var identity = GetPathIdentity(filePath);
        return _ambienceSlots[identity].TransitionVersion;
    }

    internal Task CompleteAmbienceTransitionForTests(
        string filePath,
        long transitionVersion,
        bool desiredPlaying)
    {
        var identity = GetPathIdentity(filePath);
        return _ambienceSlots.TryGetValue(identity, out var slot)
            ? ProcessAmbienceFadeCompletedAsync(slot, transitionVersion, desiredPlaying)
            : Task.CompletedTask;
    }

    public Task<AudioPlaybackInfo> PlayAsync(
        LibraryTrack track,
        long playbackId,
        ImmediateTransitionMode transitionMode = ImmediateTransitionMode.HardCut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (transitionMode is not ImmediateTransitionMode.HardCut and
            not ImmediateTransitionMode.Crossfade)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionMode));
        }

        var completion = new TaskCompletionSource<AudioPlaybackInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = PlayAndCompleteAsync(
            track,
            playbackId,
            transitionMode,
            cancellationToken,
            completion);
        return completion.Task;
    }

    public async Task PlayAmbienceAsync(
        LibraryTrack track,
        float sourceGain,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidatePublicGain(sourceGain, nameof(sourceGain));
        var identity = GetPathIdentity(track.FilePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AudioTrackSource? openedSource = null;
        try
        {
            ThrowIfDisposed();
            if (_ambienceSlots.TryGetValue(identity, out var existing))
            {
                existing.Source.Unsuppress();
                SetAmbienceSourceGainNoLock(existing, sourceGain);
                if (existing.State == AmbiencePlaybackState.FadingOut)
                {
                    ArmAmbienceTransitionNoLock(existing, targetGain: 1f, desiredPlaying: true);
                }
                return;
            }

            openedSource = await Task.Run(
                () => _sourceFactory(
                    track.FilePath,
                    Interlocked.Increment(ref _nextAmbiencePlaybackId),
                    OnSourceEnded),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            openedSource.ConfigureLooping();

            IWavePlayer output;
            try
            {
                output = EnsureOutput();
            }
            catch (Exception exception)
            {
                QueueOutputFault("initializing audio output", exception, null);
                throw;
            }

            var sourceGainStage = new GainEnvelopeSampleProvider(openedSource, sourceGain);
            var lifecycle = new GainEnvelopeSampleProvider(sourceGainStage, 0f);
            var slot = new AmbienceSlot(identity, openedSource, sourceGainStage, lifecycle);
            _ambienceSlots.Add(identity, slot);
            _ambienceBus.AddMixerInput(lifecycle);
            PublishPlaybackFaultIdentityNoLock();
            openedSource = null;
            slot.Source.Activate();
            ArmAmbienceTransitionNoLock(slot, targetGain: 1f, desiredPlaying: true);

            try
            {
                if (output.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                {
                    RunOutputCommand(output, output.Play);
                }
            }
            catch (Exception exception)
            {
                var attributedPlaybackId = _currentSlot?.Source.PlaybackId;
                ClearGraphForOutputFailureNoLock(output, attributedPlaybackId);
                QueueOutputFault("starting playback", exception, attributedPlaybackId);
                throw;
            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            openedSource?.Suppress();
            openedSource?.Dispose();
            throw;
        }
        catch
        {
            openedSource?.Suppress();
            openedSource?.Dispose();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAmbienceAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var identity = GetPathIdentity(filePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_ambienceSlots.TryGetValue(identity, out var slot))
            {
                return;
            }

            if (slot.State == AmbiencePlaybackState.FadingOut)
            {
                return;
            }

            slot.Source.Suppress();
            ArmAmbienceTransitionNoLock(slot, targetGain: 0f, desiredPlaying: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAmbienceSourceGainAsync(
        string filePath,
        float sourceGain,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicGain(sourceGain, nameof(sourceGain));
        var identity = GetPathIdentity(filePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_ambienceSlots.TryGetValue(identity, out var slot))
            {
                SetAmbienceSourceGainNoLock(slot, sourceGain);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetMusicVolumeAsync(float volume, CancellationToken cancellationToken = default) =>
        SetPersistentVolumeAsync(PersistentVolumeStage.Music, volume, cancellationToken);

    public Task SetAmbienceVolumeAsync(float volume, CancellationToken cancellationToken = default) =>
        SetPersistentVolumeAsync(PersistentVolumeStage.Ambience, volume, cancellationToken);

    public Task SetMasterVolumeAsync(float volume, CancellationToken cancellationToken = default) =>
        SetPersistentVolumeAsync(PersistentVolumeStage.Master, volume, cancellationToken);

    public Task FadeMasterAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateFadeArguments(direction, fullScaleDuration);

        return FadeMasterCoreAsync(direction, fullScaleDuration, cancellationToken);
    }

    public Task FadeMusicAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        return FadeBusCoreAsync(
            _musicFade,
            direction,
            fullScaleDuration,
            isMusic: true,
            cancellationToken);
    }

    public Task FadeAmbienceAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        return FadeBusCoreAsync(
            _ambienceFade,
            direction,
            fullScaleDuration,
            isMusic: false,
            cancellationToken);
    }

    public Task FadeMasterAndWaitAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ArmMasterFadeWithCompletionAsync(
            direction,
            fullScaleDuration,
            cancellationToken,
            completion);
        return completion.Task;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ClearCurrentFaultedOutputNoLock())
            {
                return;
            }

            if (_currentSlot is null || _paused)
            {
                return;
            }

            if (_ambienceSlots.Count == 0 && _output is not null)
            {
                var playbackId = _currentSlot.Source.PlaybackId;
                try
                {
                    RunOutputCommand(_output, _output.Pause);
                    SetMusicPausedNoLock(true);
                    _paused = true;
                }
                catch (Exception exception)
                {
                    var slots = DetachAllSlots();
                    ResetMasterNoLock();
                    RetireOutputNoLock(_output, playbackId);
                    DisposeSlots(slots);
                    QueueOutputFault("pausing playback", exception, playbackId);
                    throw;
                }
            }
            else
            {
                SetMusicPausedNoLock(true);
                _paused = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ClearCurrentFaultedOutputNoLock())
            {
                return;
            }

            if (_currentSlot is null || !_paused)
            {
                return;
            }

            if (_ambienceSlots.Count == 0 && _output is not null)
            {
                var playbackId = _currentSlot.Source.PlaybackId;
                try
                {
                    RunOutputCommand(_output, _output.Play);
                    SetMusicPausedNoLock(false);
                    _paused = false;
                }
                catch (Exception exception)
                {
                    var slots = DetachAllSlots();
                    ResetMasterNoLock();
                    RetireOutputNoLock(_output, playbackId);
                    DisposeSlots(slots);
                    QueueOutputFault("resuming playback", exception, playbackId);
                    throw;
                }
            }
            else
            {
                SetMusicPausedNoLock(false);
                _paused = false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopMusicAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ClearCurrentFaultedOutputNoLock())
            {
                return;
            }

            var slots = DetachMusicSlots();
            DisposeSlots(slots);

            if (_ambienceSlots.Count == 0 && _output is not null)
            {
                CancelMasterCompletionNoLock();
                try
                {
                    RunOutputCommand(_output, _output.Stop);
                }
                catch (Exception exception)
                {
                    RetireOutputNoLock(_output, null);
                    QueueOutputFault("stopping playback", exception, null);
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopSourcesPreservingMasterFadeAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ClearCurrentFaultedOutputNoLock())
            {
                return;
            }

            var playbackId = _currentSlot?.Source.PlaybackId;
            var slots = DetachAllSlots(publishIdentity: false);
            var ambience = DetachAllAmbienceNoLock(publishIdentity: false);
            PublishPlaybackFaultIdentityNoLock();
            CancelMasterCompletionNoLock();
            Exception? stopException = null;

            if (_output is not null)
            {
                try
                {
                    RunOutputCommand(_output, _output.Stop);
                }
                catch (Exception exception)
                {
                    stopException = exception;
                    RetireOutputNoLock(_output, playbackId);
                }
            }

            DisposeSlots(slots);
            DisposeAmbience(ambience);

            if (stopException is not null)
            {
                QueueOutputFault("stopping playback", stopException, playbackId);
                throw stopException;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ClearCurrentFaultedOutputNoLock())
            {
                return;
            }

            var playbackId = _currentSlot?.Source.PlaybackId;
            var slots = DetachAllSlots(publishIdentity: false);
            var ambience = DetachAllAmbienceNoLock(publishIdentity: false);
            PublishPlaybackFaultIdentityNoLock();
            ResetMasterNoLock();
            Exception? stopException = null;

            if (_output is not null)
            {
                try
                {
                    RunOutputCommand(_output, _output.Stop);
                }
                catch (Exception exception)
                {
                    stopException = exception;
                    RetireOutputNoLock(_output, playbackId);
                }
            }

            DisposeSlots(slots);
            DisposeAmbience(ambience);

            if (stopException is not null)
            {
                QueueOutputFault("stopping playback", stopException, playbackId);
                throw stopException;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudioProgressSnapshot> GetProgressAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            RefreshMasterFadeStateNoLock();
            RefreshBusFadeStatesNoLock();
            var slot = _currentSlot;
            if (slot is null)
            {
                return new AudioProgressSnapshot(
                    null,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    _masterEnvelope.CurrentGain,
                    _masterFadeState,
                    GetAmbienceSnapshotsNoLock(),
                    _musicVolumeTarget,
                    _ambienceVolumeTarget,
                    _masterVolumeTarget,
                    _musicFade.CurrentGain,
                    _musicFadeState,
                    _ambienceFade.CurrentGain,
                    _ambienceFadeState);
            }

            return new AudioProgressSnapshot(
                slot.Source.PlaybackId,
                slot.Source.GetPosition(),
                slot.Source.Duration,
                _masterEnvelope.CurrentGain,
                _masterFadeState,
                GetAmbienceSnapshotsNoLock(),
                _musicVolumeTarget,
                _ambienceVolumeTarget,
                _masterVolumeTarget,
                _musicFade.CurrentGain,
                _musicFadeState,
                _ambienceFade.CurrentGain,
                _ambienceFadeState);
        }
        finally
        {
            _gate.Release();
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
            Interlocked.Increment(ref _faultNotificationGeneration);
            var playbackId = _currentSlot?.Source.PlaybackId;
            var slots = DetachAllSlots(publishIdentity: false);
            var ambience = DetachAllAmbienceNoLock(publishIdentity: false);
            ResetMasterNoLock();
            var output = _output;
            _output = null;
            PublishPlaybackFaultIdentityNoLock();

            if (output is not null)
            {
                output.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    RunOutputCommand(output, output.Stop);
                }
                catch (Exception exception)
                {
                    QueueOutputFault("disposing audio output", exception, playbackId);
                }
            }

            DisposeSlots(slots);
            DisposeAmbience(ambience);
            _mixer.RemoveAllMixerInputs();

            if (output is not null)
            {
                try
                {
                    output.Dispose();
                }
                catch (Exception exception)
                {
                    QueueOutputFault("disposing audio output", exception, playbackId);
                }
            }

            TrackEnded = null;
            OutputFaulted = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PlayAndCompleteAsync(
        LibraryTrack track,
        long playbackId,
        ImmediateTransitionMode transitionMode,
        CancellationToken cancellationToken,
        TaskCompletionSource<AudioPlaybackInfo> completion)
    {
        AudioTrackSource? openedSource = null;
        MusicSlot? activatedSlot = null;

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                // Decode before touching either live slot so a bad candidate cannot
                // interrupt valid playback.
                openedSource = await Task.Run(
                    () => _sourceFactory(track.FilePath, playbackId, OnSourceEnded),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                IWavePlayer output;
                try
                {
                    output = EnsureOutput();
                }
                catch (Exception exception)
                {
                    QueueOutputFault("initializing audio output", exception, playbackId);
                    throw;
                }

                var initialGain = transitionMode == ImmediateTransitionMode.Crossfade &&
                    _currentSlot is not null
                    ? 0f
                    : 1f;
                var envelope = new GainEnvelopeSampleProvider(openedSource, initialGain);
                ISampleProvider input = transitionMode == ImmediateTransitionMode.Crossfade &&
                    _currentSlot is not null &&
                    _crossfadeStagger > TimeSpan.Zero
                    ? new SampleDelayGateSampleProvider(
                        envelope,
                        DurationToSamples(_crossfadeStagger, envelope.WaveFormat))
                    : envelope;
                var newSlot = new MusicSlot(openedSource, envelope, input);

                if (transitionMode == ImmediateTransitionMode.HardCut)
                {
                    DisposeSlots(DetachAllSlots());
                    SetCurrentSlotNoLock(newSlot);
                    _musicBus.AddMixerInput(newSlot.Input);
                }
                else
                {
                    ReplaceWithCrossfade(newSlot);
                }

                _paused = false;
                SetMusicPausedNoLock(false);
                if (output.PlaybackState != NAudio.Wave.PlaybackState.Playing)
                {
                    try
                    {
                        RunOutputCommand(output, output.Play);
                    }
                    catch (Exception exception)
                    {
                        ClearGraphForOutputFailureNoLock(output, playbackId);
                        QueueOutputFault("starting playback", exception, playbackId);
                        throw;
                    }
                }

                activatedSlot = newSlot;
                openedSource = null;
            }
            finally
            {
                _gate.Release();
            }

            completion.TrySetResult(new AudioPlaybackInfo(activatedSlot.Source.Duration));
            activatedSlot.Source.Activate();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            openedSource?.Suppress();
            openedSource?.Dispose();
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            openedSource?.Suppress();
            openedSource?.Dispose();
            completion.TrySetException(exception);
        }
    }

    private async Task FadeMasterCoreAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken,
        TaskCompletionSource? completion = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var targetGain = direction == MasterFadeDirection.In ? 1f : 0f;
            var currentGain = _masterEnvelope.CurrentGain;
            var remainingDistance = Math.Abs(targetGain - currentGain);
            var fadeVersion = ++_masterFadeVersion;

            CancelMasterCompletionNoLock();
            _masterFadeCompletion = completion;

            if (remainingDistance == 0f || fullScaleDuration == TimeSpan.Zero)
            {
                _masterEnvelope.SetGain(targetGain);
                _masterFadeState = direction == MasterFadeDirection.In
                    ? MasterFadeState.Full
                    : MasterFadeState.Muted;
                completion?.TrySetResult();
                _masterFadeCompletion = null;
                return;
            }

            var scaledTicks = decimal.ToInt64(decimal.Round(
                fullScaleDuration.Ticks * (decimal)remainingDistance,
                0,
                MidpointRounding.AwayFromZero));
            if (scaledTicks == 0)
            {
                _masterEnvelope.SetGain(targetGain);
                _masterFadeState = direction == MasterFadeDirection.In
                    ? MasterFadeState.Full
                    : MasterFadeState.Muted;
                completion?.TrySetResult();
                _masterFadeCompletion = null;
                return;
            }

            _masterFadeState = direction == MasterFadeDirection.In
                ? MasterFadeState.FadingIn
                : MasterFadeState.FadingOut;
            _masterEnvelope.RampTo(
                targetGain,
                TimeSpan.FromTicks(scaledTicks),
                GainRampCurve.Linear,
                () => OnMasterFadeCompleted(fadeVersion, direction));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ArmMasterFadeWithCompletionAsync(
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        CancellationToken cancellationToken,
        TaskCompletionSource completion)
    {
        try
        {
            await FadeMasterCoreAsync(
                direction,
                fullScaleDuration,
                cancellationToken,
                completion).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task FadeBusCoreAsync(
        GainEnvelopeSampleProvider envelope,
        MasterFadeDirection direction,
        TimeSpan fullScaleDuration,
        bool isMusic,
        CancellationToken cancellationToken)
    {
        ValidateFadeArguments(direction, fullScaleDuration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var targetGain = direction == MasterFadeDirection.In ? 1f : 0f;
            var remainingDistance = Math.Abs(targetGain - envelope.CurrentGain);
            var fadeVersion = isMusic ? ++_musicFadeVersion : ++_ambienceFadeVersion;
            SetBusFadeStateNoLock(isMusic, direction, remainingDistance, fullScaleDuration);

            if (remainingDistance == 0f || fullScaleDuration == TimeSpan.Zero)
            {
                envelope.SetGain(targetGain);
                SetBusFadeTerminalStateNoLock(isMusic, direction);
                return;
            }

            var duration = ScaleFadeDuration(fullScaleDuration, remainingDistance);
            if (duration == TimeSpan.Zero)
            {
                envelope.SetGain(targetGain);
                SetBusFadeTerminalStateNoLock(isMusic, direction);
                return;
            }

            envelope.RampTo(
                targetGain,
                duration,
                GainRampCurve.Linear,
                () => OnBusFadeCompleted(isMusic, fadeVersion, direction));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnBusFadeCompleted(
        bool isMusic,
        long fadeVersion,
        MasterFadeDirection direction)
    {
        _ = ProcessBusFadeCompletedAsync(isMusic, fadeVersion, direction);
    }

    private async Task ProcessBusFadeCompletedAsync(
        bool isMusic,
        long fadeVersion,
        MasterFadeDirection direction)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed ||
                (isMusic ? fadeVersion != _musicFadeVersion : fadeVersion != _ambienceFadeVersion))
            {
                return;
            }

            SetBusFadeTerminalStateNoLock(isMusic, direction);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetBusFadeStateNoLock(
        bool isMusic,
        MasterFadeDirection direction,
        float remainingDistance,
        TimeSpan fullScaleDuration)
    {
        if (remainingDistance == 0f || fullScaleDuration == TimeSpan.Zero)
        {
            return;
        }

        var state = direction == MasterFadeDirection.In
            ? MasterFadeState.FadingIn
            : MasterFadeState.FadingOut;
        if (isMusic)
        {
            _musicFadeState = state;
        }
        else
        {
            _ambienceFadeState = state;
        }
    }

    private void SetBusFadeTerminalStateNoLock(bool isMusic, MasterFadeDirection direction)
    {
        var state = direction == MasterFadeDirection.In
            ? MasterFadeState.Full
            : MasterFadeState.Muted;
        if (isMusic)
        {
            _musicFadeState = state;
        }
        else
        {
            _ambienceFadeState = state;
        }
    }

    private void ReplaceWithCrossfade(MusicSlot newSlot)
    {
        var previousCurrent = _currentSlot;
        if (previousCurrent is null)
        {
            newSlot.Envelope.SetGain(1f);
            SetCurrentSlotNoLock(newSlot);
            _musicBus.AddMixerInput(newSlot.Input);
            return;
        }

        var previousOutgoing = _outgoingSlot;
        MusicSlot retained = previousCurrent;
        MusicSlot? retired = null;

        if (previousOutgoing is not null &&
            previousOutgoing.Envelope.CurrentGain > previousCurrent.Envelope.CurrentGain)
        {
            retained = previousOutgoing;
            retired = DetachCurrentSlot();
        }
        else
        {
            retired = DetachOutgoingSlot();
            Volatile.Write(ref _outgoingSlot, previousCurrent);
        }

        retired?.Source.Dispose();
        SetCurrentSlotNoLock(newSlot);
        _musicBus.AddMixerInput(newSlot.Input);
        retained.Envelope.RampTo(
            0f,
            ScaleFadeDuration(_mediumFadeDuration, retained.Envelope.CurrentGain),
            GainRampCurve.EqualPowerOutgoing,
            () => OnOutgoingFadeCompleted(retained));
        newSlot.Envelope.RampTo(
            1f,
            _mediumFadeDuration,
            GainRampCurve.EqualPowerIncoming);
    }

    private IWavePlayer EnsureOutput()
    {
        if (_output is not null)
        {
            if (IsOutputFaulted(_output))
            {
                ClearGraphForOutputFailureNoLock(_output, _currentSlot?.Source.PlaybackId);
            }
            else
            {
                return _output;
            }
        }

        if (_output is not null)
        {
            return _output;
        }

        var output = _outputFactory() ??
            throw new InvalidOperationException("The audio output factory returned null.");
        if (IsOutputFaulted(output))
        {
            try
            {
                output.Dispose();
            }
            catch
            {
                // Preserve the faulted-output error below.
            }

            throw new InvalidOperationException(
                "The audio output factory returned a previously faulted output instance.");
        }

        output.PlaybackStopped += OnPlaybackStopped;

        try
        {
            RunOutputCommand(output, () => output.Init(_waveProvider));
        }
        catch
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                output.Dispose();
            }
            catch
            {
                // Preserve the initialization exception reported by PlayAsync.
            }

            throw;
        }

        _output = output;
        PublishPlaybackFaultIdentityNoLock();
        return output;
    }

    private bool IsOutputFaulted(IWavePlayer output)
    {
        lock (_faultedOutputGate)
        {
            var isFaulted = false;
            for (var index = _faultedOutputs.Count - 1; index >= 0; index--)
            {
                if (!_faultedOutputs[index].TryGetTarget(out var candidate))
                {
                    _faultedOutputs.RemoveAt(index);
                }
                else if (ReferenceEquals(candidate, output))
                {
                    isFaulted = true;
                }
            }

            return isFaulted;
        }
    }

    private bool TryMarkOutputFaulted(IWavePlayer output)
    {
        lock (_faultedOutputGate)
        {
            for (var index = _faultedOutputs.Count - 1; index >= 0; index--)
            {
                if (!_faultedOutputs[index].TryGetTarget(out var candidate))
                {
                    _faultedOutputs.RemoveAt(index);
                }
                else if (ReferenceEquals(candidate, output))
                {
                    return false;
                }
            }

            _faultedOutputs.Add(new WeakReference<IWavePlayer>(output));
            return true;
        }
    }

    private bool ClearCurrentFaultedOutputNoLock()
    {
        if (_output is null || !IsOutputFaulted(_output))
        {
            return false;
        }

        ClearGraphForOutputFailureNoLock(_output, _currentSlot?.Source.PlaybackId);
        return true;
    }

    private void ClearGraphForOutputFailureNoLock(
        IWavePlayer output,
        long? playbackId)
    {
        TryMarkOutputFaulted(output);
        var attributedPlaybackId = _currentSlot?.Source.PlaybackId ?? playbackId;
        var slots = DetachAllSlots(publishIdentity: false);
        var ambience = DetachAllAmbienceNoLock(publishIdentity: false);
        ResetMasterNoLock();
        RetireOutputNoLock(output, attributedPlaybackId);
        DisposeSlots(slots);
        DisposeAmbience(ambience);
    }

    private MusicSlot? DetachCurrentSlot()
    {
        var slot = _currentSlot;
        if (slot is null)
        {
            return null;
        }

        SetCurrentSlotNoLock(null);
        slot.Source.Suppress();
        slot.Envelope.SetGain(0f);
        _musicBus.RemoveMixerInput(slot.Input);
        return slot;
    }

    private MusicSlot? DetachOutgoingSlot()
    {
        var slot = _outgoingSlot;
        if (slot is null)
        {
            return null;
        }

        Volatile.Write(ref _outgoingSlot, null);
        slot.Source.Suppress();
        slot.Envelope.SetGain(0f);
        _musicBus.RemoveMixerInput(slot.Input);
        return slot;
    }

    private (MusicSlot? Current, MusicSlot? Outgoing) DetachAllSlots(
        bool publishIdentity = true)
    {
        var current = _currentSlot;
        var outgoing = _outgoingSlot;
        Volatile.Write(ref _currentSlot, null);
        Volatile.Write(ref _outgoingSlot, null);
        _paused = false;
        _masterEnvelope.SetPaused(false);

        if (current is not null)
        {
            current.Source.Suppress();
            current.Envelope.SetGain(0f);
            _musicBus.RemoveMixerInput(current.Input);
        }

        if (outgoing is not null)
        {
            outgoing.Source.Suppress();
            outgoing.Envelope.SetGain(0f);
            _musicBus.RemoveMixerInput(outgoing.Input);
        }

        if (publishIdentity)
        {
            PublishPlaybackFaultIdentityNoLock();
        }

        return (current, outgoing);
    }

    private (MusicSlot? Current, MusicSlot? Outgoing) DetachMusicSlots() =>
        DetachAllSlots();

    private List<AmbienceSlot> DetachAllAmbienceNoLock(bool publishIdentity = true)
    {
        var slots = _ambienceSlots.Values.ToList();
        _ambienceSlots.Clear();
        foreach (var slot in slots)
        {
            slot.Source.Suppress();
            slot.Lifecycle.SetGain(0f);
            _ambienceBus.RemoveMixerInput(slot.Lifecycle);
        }

        if (publishIdentity)
        {
            PublishPlaybackFaultIdentityNoLock();
        }

        return slots;
    }

    private void DetachAmbienceNoLock(AmbienceSlot slot)
    {
        if (!_ambienceSlots.Remove(slot.Identity))
        {
            return;
        }

        slot.Source.Suppress();
        slot.Lifecycle.SetGain(0f);
        _ambienceBus.RemoveMixerInput(slot.Lifecycle);
        PublishPlaybackFaultIdentityNoLock();
    }

    private static void DisposeAmbience(IEnumerable<AmbienceSlot> slots)
    {
        foreach (var slot in slots)
        {
            slot.Source.Dispose();
        }
    }

    private void SetCurrentSlotNoLock(
        MusicSlot? slot,
        bool forceGenerationAdvance = false)
    {
        if (!forceGenerationAdvance && ReferenceEquals(_currentSlot, slot))
        {
            return;
        }

        Volatile.Write(ref _currentSlot, slot);
        PublishPlaybackFaultIdentityNoLock();
    }

    private void PublishPlaybackFaultIdentityNoLock()
    {
        var generation = Interlocked.Increment(ref _playbackRunGeneration);
        Volatile.Write(
            ref _playbackFaultIdentity,
            new PlaybackFaultIdentity(
                _output,
                _currentSlot?.Source ??
                _outgoingSlot?.Source ??
                _ambienceSlots.Values.FirstOrDefault()?.Source,
                generation));
    }

    private static void DisposeSlots((MusicSlot? Current, MusicSlot? Outgoing) slots)
    {
        slots.Current?.Source.Dispose();
        if (!ReferenceEquals(slots.Outgoing, slots.Current))
        {
            slots.Outgoing?.Source.Dispose();
        }
    }

    private void ResetMasterNoLock()
    {
        _masterFadeVersion++;
        CancelMasterCompletionNoLock();
        _masterEnvelope.SetGain(1f);
        _masterEnvelope.SetPaused(false);
        _masterFadeState = MasterFadeState.Full;
        _musicFadeVersion++;
        _musicFade.SetGain(1f);
        _musicFadeState = MasterFadeState.Full;
        _ambienceFadeVersion++;
        _ambienceFade.SetGain(1f);
        _ambienceFadeState = MasterFadeState.Full;
    }

    private void CancelMasterCompletionNoLock()
    {
        var completion = _masterFadeCompletion;
        _masterFadeCompletion = null;
        completion?.TrySetCanceled();
    }

    private void ArmAmbienceTransitionNoLock(
        AmbienceSlot slot,
        float targetGain,
        bool desiredPlaying)
    {
        var transitionVersion = ++slot.TransitionVersion;
        slot.DesiredPlaying = desiredPlaying;
        slot.State = desiredPlaying
            ? AmbiencePlaybackState.FadingIn
            : AmbiencePlaybackState.FadingOut;
        var remainingDistance = Math.Abs(targetGain - slot.Lifecycle.CurrentGain);
        slot.Lifecycle.RampTo(
            targetGain,
            ScaleFadeDuration(_mediumFadeDuration, remainingDistance),
            GainRampCurve.Linear,
            () => OnAmbienceFadeCompleted(slot, transitionVersion, desiredPlaying));
    }

    private static TimeSpan ScaleFadeDuration(TimeSpan fullScaleDuration, float remainingDistance)
    {
        remainingDistance = Math.Clamp(remainingDistance, 0f, 1f);
        var ticks = decimal.ToInt64(decimal.Round(
            fullScaleDuration.Ticks * (decimal)remainingDistance,
            0,
            MidpointRounding.AwayFromZero));
        return TimeSpan.FromTicks(ticks);
    }

    private void SetMusicPausedNoLock(bool paused)
    {
        _musicPauseGate.SetPaused(paused);
    }

    private void SetAmbienceSourceGainNoLock(AmbienceSlot slot, float targetGain)
    {
        var remainingDistance = Math.Abs(targetGain - slot.SourceGain.CurrentGain);
        slot.SourceGain.RampTo(
            targetGain,
            ScaleFadeDuration(_mediumFadeDuration, remainingDistance));
    }

    private IReadOnlyList<AmbiencePlaybackSnapshot> GetAmbienceSnapshotsNoLock()
    {
        return _ambienceSlots.Values
            .Select(slot => new AmbiencePlaybackSnapshot(
                slot.Identity,
                slot.Source.GetPosition(),
                slot.Source.Duration,
                slot.SourceGain.CurrentGain,
                slot.Lifecycle.CurrentGain,
                slot.State))
            .ToArray();
    }

    private void RetireOutputNoLock(IWavePlayer? output, long? playbackId)
    {
        if (output is null || !ReferenceEquals(_output, output))
        {
            return;
        }

        TryMarkOutputFaulted(output);
        output.PlaybackStopped -= OnPlaybackStopped;
        _output = null;
        PublishPlaybackFaultIdentityNoLock();
        try
        {
            output.Dispose();
        }
        catch (Exception exception)
        {
            QueueOutputFault("retiring audio output", exception, playbackId);
        }
    }

    private void RefreshMasterFadeStateNoLock()
    {
        if (_masterEnvelope.IsRampActive)
        {
            return;
        }

        _masterFadeState = _masterEnvelope.CurrentGain <= 0f
            ? MasterFadeState.Muted
            : MasterFadeState.Full;
    }

    private void RefreshBusFadeStatesNoLock()
    {
        if (!_musicFade.IsRampActive)
        {
            _musicFadeState = _musicFade.CurrentGain <= 0f
                ? MasterFadeState.Muted
                : MasterFadeState.Full;
        }

        if (!_ambienceFade.IsRampActive)
        {
            _ambienceFadeState = _ambienceFade.CurrentGain <= 0f
                ? MasterFadeState.Muted
                : MasterFadeState.Full;
        }
    }

    private void OnSourceEnded(AudioTrackSource source)
    {
        _ = ProcessSourceEndedAsync(source);
    }

    private async Task ProcessSourceEndedAsync(AudioTrackSource source)
    {
        var shouldNotify = false;
        (MusicSlot? Current, MusicSlot? Outgoing) retired = default;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_ambienceSlots.Values.Any(slot => ReferenceEquals(slot.Source, source)))
            {
                if (source.IsFaulted)
                {
                    var output = _output;
                    var playbackId = _currentSlot?.Source.PlaybackId;
                    if (output is not null)
                    {
                        ClearGraphForOutputFailureNoLock(output, playbackId);
                    }
                    else
                    {
                        var slots = DetachAllSlots(publishIdentity: false);
                        var ambience = DetachAllAmbienceNoLock(publishIdentity: false);
                        ResetMasterNoLock();
                        PublishPlaybackFaultIdentityNoLock();
                        DisposeSlots(slots);
                        DisposeAmbience(ambience);
                    }

                    QueueOutputFault(
                        "handling an ambience source that stopped unexpectedly",
                        new InvalidDataException("A looping ambience source produced no more samples."),
                        playbackId);
                }

                return;
            }

            if (ReferenceEquals(_currentSlot?.Source, source) &&
                !source.IsSuppressed &&
                !source.IsFaulted)
            {
                retired = DetachAllSlots();
                shouldNotify = true;
            }
            else if (ReferenceEquals(_outgoingSlot?.Source, source))
            {
                retired = (null, DetachOutgoingSlot());
            }

            DisposeSlots(retired);
        }
        finally
        {
            _gate.Release();
        }

        if (shouldNotify)
        {
            await InvokeTrackEndedAsync(new AudioTrackEndedNotification(source.PlaybackId))
                .ConfigureAwait(false);
        }
    }

    private void OnOutgoingFadeCompleted(MusicSlot slot)
    {
        _ = ProcessOutgoingFadeCompletedAsync(slot);
    }

    private async Task ProcessOutgoingFadeCompletedAsync(MusicSlot slot)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(_outgoingSlot, slot))
            {
                return;
            }

            var retired = DetachOutgoingSlot();
            retired?.Source.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnAmbienceFadeCompleted(
        AmbienceSlot slot,
        long transitionVersion,
        bool desiredPlaying)
    {
        _ = ProcessAmbienceFadeCompletedAsync(slot, transitionVersion, desiredPlaying);
    }

    private async Task ProcessAmbienceFadeCompletedAsync(
        AmbienceSlot slot,
        long transitionVersion,
        bool desiredPlaying)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed ||
                !_ambienceSlots.TryGetValue(slot.Identity, out var current) ||
                !ReferenceEquals(current, slot) ||
                slot.TransitionVersion != transitionVersion ||
                slot.DesiredPlaying != desiredPlaying)
            {
                return;
            }

            if (desiredPlaying)
            {
                slot.State = AmbiencePlaybackState.Playing;
                return;
            }

            DetachAmbienceNoLock(slot);
            slot.Source.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnMasterFadeCompleted(long version, MasterFadeDirection direction)
    {
        _ = ProcessMasterFadeCompletedAsync(version, direction);
    }

    private async Task ProcessMasterFadeCompletedAsync(
        long version,
        MasterFadeDirection direction)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || version != _masterFadeVersion)
            {
                return;
            }

            _masterFadeState = direction == MasterFadeDirection.In
                ? MasterFadeState.Full
                : MasterFadeState.Muted;
            var completion = _masterFadeCompletion;
            _masterFadeCompletion = null;
            completion?.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null || sender is not IWavePlayer output)
        {
            return;
        }

        if (!TryMarkOutputFaulted(output))
        {
            return;
        }

        lock (_outputCommandGate)
        {
            if (_activeOutputCommand is not null &&
                ReferenceEquals(_activeOutputCommand.Output, output))
            {
                _activeOutputCommand.CallbackException ??= eventArgs.Exception;
                return;
            }
        }

        var identity = Volatile.Read(ref _playbackFaultIdentity);
        if (!ReferenceEquals(identity.Output, output))
        {
            return;
        }

        var source = identity.Source;

        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Engine.ProcessPlaybackFault(
                state.Output,
                state.Source,
                state.Identity,
                state.Exception),
            (
                Engine: this,
                Output: output,
                Source: source,
                Identity: identity,
                Exception: eventArgs.Exception),
            preferLocal: false);
    }

    private void ProcessPlaybackFault(
        IWavePlayer output,
        AudioTrackSource? source,
        PlaybackFaultIdentity identity,
        Exception exception)
    {
        _ = ProcessPlaybackFaultAsync(output, source, identity, exception);
    }

    private async Task ProcessPlaybackFaultAsync(
        IWavePlayer output,
        AudioTrackSource? source,
        PlaybackFaultIdentity identity,
        Exception exception)
    {
        var shouldNotify = false;
        AudioOutputFaultedHandler? handlers = null;
        long notificationGeneration = 0;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var faultStillCurrent = ReferenceEquals(_output, output) &&
                ReferenceEquals(Volatile.Read(ref _playbackFaultIdentity).Output, output);

            if (faultStillCurrent)
            {
                ClearGraphForOutputFailureNoLock(output, source?.PlaybackId);
                shouldNotify = true;
            }
            else if (source is not null)
            {
                // A newer logical run restarted this reusable output. Report the
                // captured run's fault without disturbing the newer source.
                shouldNotify = true;
            }

            if (shouldNotify)
            {
                handlers = OutputFaulted;
                notificationGeneration = Volatile.Read(ref _faultNotificationGeneration);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (shouldNotify)
        {
            await InvokeOutputFaultedAsync(new AudioOutputFault(
                $"Audio output stopped unexpectedly: {exception.Message}",
                exception,
                source?.PlaybackId),
                handlers,
                notificationGeneration).ConfigureAwait(false);
        }
    }

    private async Task SetPersistentVolumeAsync(
        PersistentVolumeStage persistentStage,
        float volume,
        CancellationToken cancellationToken)
    {
        ValidatePublicGain(volume, nameof(volume));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var stage = persistentStage switch
            {
                PersistentVolumeStage.Music => _musicVolume,
                PersistentVolumeStage.Ambience => _ambienceVolume,
                PersistentVolumeStage.Master => _masterVolume,
                _ => throw new ArgumentOutOfRangeException(nameof(persistentStage)),
            };
            var currentGain = stage.CurrentGain;
            if (ShouldRampPersistentVolumeNoLock(persistentStage))
            {
                stage.RampTo(
                    volume,
                    ScaleFadeDuration(_mediumFadeDuration, Math.Abs(volume - currentGain)));
            }
            else
            {
                stage.SetGain(volume);
            }

            SetPersistentVolumeTargetNoLock(persistentStage, volume);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldRampPersistentVolumeNoLock(PersistentVolumeStage persistentStage) =>
        persistentStage switch
        {
            PersistentVolumeStage.Music => HasUnpausedPhysicalMusicNoLock(),
            PersistentVolumeStage.Ambience => _ambienceSlots.Count > 0,
            PersistentVolumeStage.Master =>
                HasUnpausedPhysicalMusicNoLock() || _ambienceSlots.Count > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(persistentStage)),
        };

    private bool HasUnpausedPhysicalMusicNoLock() =>
        !_paused && (_currentSlot is not null || _outgoingSlot is not null);

    private void SetPersistentVolumeTargetNoLock(
        PersistentVolumeStage persistentStage,
        float volume)
    {
        switch (persistentStage)
        {
            case PersistentVolumeStage.Music:
                _musicVolumeTarget = volume;
                break;
            case PersistentVolumeStage.Ambience:
                _ambienceVolumeTarget = volume;
                break;
            case PersistentVolumeStage.Master:
                _masterVolumeTarget = volume;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(persistentStage));
        }
    }

    private static string GetPathIdentity(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    private static void ValidatePublicGain(float gain, string parameterName)
    {
        if (!float.IsFinite(gain) || gain is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Gain must be finite and between zero and one.");
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

        if (fullScaleDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fullScaleDuration),
                "The full-scale fade duration cannot be negative.");
        }
    }

    private void QueueOutputFault(
        string operation,
        Exception exception,
        long? playbackId)
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        var handlers = OutputFaulted;
        if (handlers is null)
        {
            return;
        }

        var notificationGeneration = Volatile.Read(ref _faultNotificationGeneration);

        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Engine.StartOutputFaultNotification(
                state.Fault,
                state.Handlers,
                state.NotificationGeneration),
            (
                Engine: this,
                Fault: new AudioOutputFault(
                    $"Audio output failed while {operation}: {exception.Message}",
                    exception,
                    playbackId),
                Handlers: handlers,
                NotificationGeneration: notificationGeneration),
            preferLocal: false);
    }

    private void StartOutputFaultNotification(
        AudioOutputFault fault,
        AudioOutputFaultedHandler handlers,
        long notificationGeneration)
    {
        _ = InvokeOutputFaultedAsync(fault, handlers, notificationGeneration);
    }

    private async Task InvokeTrackEndedAsync(AudioTrackEndedNotification notification)
    {
        var handlers = TrackEnded;
        if (handlers is null)
        {
            return;
        }

        foreach (AudioTrackEndedHandler handler in handlers.GetInvocationList())
        {
            try
            {
                await handler(notification).ConfigureAwait(false);
            }
            catch
            {
                // Event consumers must not terminate the audio worker.
            }
        }
    }

    private async Task InvokeOutputFaultedAsync(
        AudioOutputFault fault,
        AudioOutputFaultedHandler? handlers = null,
        long? notificationGeneration = null)
    {
        var expectedGeneration = notificationGeneration ??
            Volatile.Read(ref _faultNotificationGeneration);
        handlers ??= OutputFaulted;
        if (handlers is null)
        {
            return;
        }

        foreach (AudioOutputFaultedHandler handler in handlers.GetInvocationList())
        {
            if (Volatile.Read(ref _disposed) ||
                expectedGeneration != Volatile.Read(ref _faultNotificationGeneration))
            {
                return;
            }

            try
            {
                await handler(fault).ConfigureAwait(false);
            }
            catch
            {
                // Event consumers must not terminate the audio worker.
            }
        }
    }

    private void RunOutputCommand(IWavePlayer output, Action command)
    {
        var context = new OutputCommandContext(output);
        lock (_outputCommandGate)
        {
            if (_activeOutputCommand is not null)
            {
                throw new InvalidOperationException("Audio output commands cannot overlap.");
            }

            _activeOutputCommand = context;
        }

        Exception? commandException = null;
        try
        {
            command();
        }
        catch (Exception exception)
        {
            commandException = exception;
        }
        finally
        {
            lock (_outputCommandGate)
            {
                if (ReferenceEquals(_activeOutputCommand, context))
                {
                    _activeOutputCommand = null;
                }
            }
        }

        if (commandException is not null)
        {
            if (context.CallbackException is not null &&
                !ReferenceEquals(commandException, context.CallbackException))
            {
                throw new AggregateException(commandException, context.CallbackException);
            }

            ExceptionDispatchInfo.Capture(commandException).Throw();
        }

        if (context.CallbackException is not null)
        {
            ExceptionDispatchInfo.Capture(context.CallbackException).Throw();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static long DurationToSamples(TimeSpan duration, WaveFormat format)
    {
        var exactFrames = (decimal)duration.Ticks * format.SampleRate / TimeSpan.TicksPerSecond;
        var wholeFrames = decimal.Round(exactFrames, 0, MidpointRounding.AwayFromZero);
        var interleavedSamples = wholeFrames * format.Channels;
        if (interleavedSamples > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return decimal.ToInt64(interleavedSamples);
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

    private sealed record MusicSlot(
        AudioTrackSource Source,
        GainEnvelopeSampleProvider Envelope,
        ISampleProvider Input);

    private enum PersistentVolumeStage
    {
        Music,
        Ambience,
        Master,
    }

    private sealed class AmbienceSlot(
        string identity,
        AudioTrackSource source,
        GainEnvelopeSampleProvider sourceGain,
        GainEnvelopeSampleProvider lifecycle)
    {
        internal string Identity { get; } = identity;

        internal AudioTrackSource Source { get; } = source;

        internal GainEnvelopeSampleProvider SourceGain { get; } = sourceGain;

        internal GainEnvelopeSampleProvider Lifecycle { get; } = lifecycle;

        internal AmbiencePlaybackState State { get; set; } = AmbiencePlaybackState.FadingIn;

        internal long TransitionVersion { get; set; }

        internal bool DesiredPlaying { get; set; }
    }

    private sealed record PlaybackFaultIdentity(
        IWavePlayer? Output,
        AudioTrackSource? Source,
        long Generation);

    private sealed class OutputCommandContext(IWavePlayer output)
    {
        internal IWavePlayer Output { get; } = output;

        internal Exception? CallbackException { get; set; }
    }
}
