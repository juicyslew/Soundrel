using System.IO;
using System.Threading.Channels;
using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.ViewModels;

public sealed class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const string SelectLibraryPrompt = "Select a library folder to begin.";

    private readonly LibraryScanner libraryScanner;
    private readonly SettingsService settingsService;
    private readonly PlaybackController playbackController;
    private readonly SynchronizationContext? uiContext;
    private readonly int uiThreadId;
    private readonly object playbackOperationLock = new();
    private readonly Dictionary<PlaybackOperationKey, int> playbackOperationCounts = [];
    private readonly object volumeRequestLock = new();
    private readonly Dictionary<VolumeKind, float> pendingVolumeRequests = [];
    private bool volumeWorkerScheduled;
    private Task? volumeWorkerTask;
    private readonly AsyncFifoDispatcher playbackDispatcher = new();
    private readonly SemaphoreSlim settingsSaveGate = new(1, 1);
    private IReadOnlyList<LibraryTreeNode> libraryNodes = Array.Empty<LibraryTreeNode>();
    private IReadOnlyList<LibraryTrack> selectedTracks = Array.Empty<LibraryTrack>();
    private IReadOnlyList<AmbienceTrackViewModel> ambienceTracks = Array.Empty<AmbienceTrackViewModel>();
    private IReadOnlyList<AmbienceTrackViewModel> filteredAmbienceTracks = Array.Empty<AmbienceTrackViewModel>();
    private IReadOnlyList<LibraryScanIssue> scanIssues = Array.Empty<LibraryScanIssue>();
    private IReadOnlyList<PlaybackQueueEntry> explicitQueue = Array.Empty<PlaybackQueueEntry>();
    private IReadOnlyList<LibraryAmbiencePresets> ambiencePresets = Array.Empty<LibraryAmbiencePresets>();
    private IReadOnlyList<AmbiencePreset> currentLibraryPresets = Array.Empty<AmbiencePreset>();
    private AmbiencePreset? selectedAmbiencePreset;
    private string ambiencePresetName = string.Empty;
    private string? selectedLibraryPath;
    private LibraryPlaylist? selectedPlaylist;
    private string selectedPlaylistName = "No playlist selected";
    private string statusText = SelectLibraryPrompt;
    private LibraryPlaylist? activePlaylist;
    private LibraryPlaylist? pendingPlaylist;
    private LibraryTrack? currentTrack;
    private LibraryPlaylist? currentPlaylist;
    private PlaybackState playbackState;
    private TimeSpan elapsed;
    private TimeSpan duration;
    private long? currentPlaybackId;
    private string? latestPlaybackError;
    private ImmediateTransitionMode preferredImmediateTransitionMode = ImmediateTransitionMode.HardCut;
    private float masterGain = 1f;
    private float musicVolume = 1f;
    private float ambienceVolume = 1f;
    private float masterVolume = 1f;
    private float fastFadeSeconds = AppSettings.DefaultFastFadeSeconds;
    private float mediumFadeSeconds = AppSettings.DefaultMediumFadeSeconds;
    private float slowFadeSeconds = AppSettings.DefaultSlowFadeSeconds;
    private float crossfadeStaggerSeconds = AppSettings.DefaultCrossfadeStaggerSeconds;
    private MasterFadeState masterFadeState = MasterFadeState.Full;
    private float musicFadeGain = 1f;
    private MasterFadeState musicFadeState = MasterFadeState.Full;
    private float ambienceFadeGain = 1f;
    private MasterFadeState ambienceFadeState = MasterFadeState.Full;
    private bool isFastFadeSelected = true;
    private string ambienceSearchText = string.Empty;
    private bool hasPhysicalAmbience;
    private bool isBusy;
    private long playbackErrorNotificationVersion;
    private int disposeStarted;
    private Task? disposeTask;

    public MainViewModel()
        : this(
            new LibraryScanner(),
            new SettingsService(),
            new PlaybackController(new AudioEngine()),
            SynchronizationContext.Current)
    {
    }

    public MainViewModel(LibraryScanner libraryScanner, SettingsService settingsService)
        : this(
            libraryScanner,
            settingsService,
            new PlaybackController(new AudioEngine()),
            SynchronizationContext.Current)
    {
    }

    public MainViewModel(
        LibraryScanner libraryScanner,
        SettingsService settingsService,
        PlaybackController playbackController,
        SynchronizationContext? synchronizationContext = null)
    {
        ArgumentNullException.ThrowIfNull(libraryScanner);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(playbackController);

        this.libraryScanner = libraryScanner;
        this.settingsService = settingsService;
        this.playbackController = playbackController;
        uiContext = synchronizationContext ?? SynchronizationContext.Current;
        uiThreadId = Environment.CurrentManagedThreadId;

        playbackController.StateChanged += PlaybackController_StateChanged;
        playbackController.ErrorOccurred += PlaybackController_ErrorOccurred;
        ApplyPlaybackSnapshot(playbackController.Snapshot);
    }

    public IReadOnlyList<LibraryTreeNode> LibraryNodes
    {
        get => libraryNodes;
        private set => SetProperty(ref libraryNodes, value);
    }

    public IReadOnlyList<LibraryTrack> SelectedTracks
    {
        get => selectedTracks;
        private set => SetProperty(ref selectedTracks, value);
    }

    public IReadOnlyList<AmbienceTrackViewModel> AmbienceTracks
    {
        get => ambienceTracks;
        private set
        {
            if (SetProperty(ref ambienceTracks, value))
            {
                UpdateFilteredAmbienceTracks();
                OnPropertyChanged(nameof(CanStopAll));
                NotifyFadeCommandAvailability();
            }
        }
    }

    public IReadOnlyList<AmbienceTrackViewModel> FilteredAmbienceTracks => filteredAmbienceTracks;

    // Alias retained for the later compact-list binding.
    public IReadOnlyList<AmbienceTrackViewModel> VisibleAmbienceTracks => FilteredAmbienceTracks;

    public string AmbienceSearchText
    {
        get => ambienceSearchText;
        set
        {
            if (SetProperty(ref ambienceSearchText, value ?? string.Empty))
            {
                UpdateFilteredAmbienceTracks();
            }
        }
    }

    public IReadOnlyList<LibraryScanIssue> ScanIssues
    {
        get => scanIssues;
        private set
        {
            if (SetProperty(ref scanIssues, value))
            {
                OnPropertyChanged(nameof(HasScanIssues));
            }
        }
    }

    public bool HasScanIssues => ScanIssues.Count > 0;

    public string? SelectedLibraryPath
    {
        get => selectedLibraryPath;
        private set
        {
            if (SetProperty(ref selectedLibraryPath, value))
            {
                OnPropertyChanged(nameof(LibraryPathDisplay));
                OnPropertyChanged(nameof(CanRescan));
                RefreshCurrentLibraryPresets(clearSelection: true);
            }
        }
    }

    public string LibraryPathDisplay => SelectedLibraryPath ?? "No library selected";

    public IReadOnlyList<AmbiencePreset> CurrentLibraryPresets => currentLibraryPresets;

    public AmbiencePreset? SelectedAmbiencePreset
    {
        get => selectedAmbiencePreset;
        set
        {
            if (!SetProperty(ref selectedAmbiencePreset, value))
            {
                return;
            }

            if (value is not null && !string.Equals(ambiencePresetName, value.Name, StringComparison.Ordinal))
            {
                ambiencePresetName = value.Name;
                OnPropertyChanged(nameof(AmbiencePresetName));
            }

            OnPropertyChanged(nameof(CanSaveAmbiencePreset));
            OnPropertyChanged(nameof(CanDeleteAmbiencePreset));
            OnPropertyChanged(nameof(CanApplyAmbiencePreset));
        }
    }

    public string AmbiencePresetName
    {
        get => ambiencePresetName;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref ambiencePresetName, value))
            {
                OnPropertyChanged(nameof(CanSaveAmbiencePreset));
            }
        }
    }

    public bool CanSaveAmbiencePreset =>
        !IsDisposed &&
        !string.IsNullOrWhiteSpace(AmbiencePresetName) &&
        TryGetCurrentLibraryRoot(out _);

    public bool CanDeleteAmbiencePreset =>
        !IsDisposed &&
        SelectedAmbiencePreset is not null &&
        TryGetCurrentLibraryRoot(out _);

    public bool CanApplyAmbiencePreset =>
        !IsDisposed &&
        SelectedAmbiencePreset is not null &&
        TryGetCurrentLibraryRoot(out _);

    public string SelectedPlaylistName
    {
        get => selectedPlaylistName;
        private set => SetProperty(ref selectedPlaylistName, value);
    }

    public LibraryPlaylist? SelectedPlaylist
    {
        get => selectedPlaylist;
        private set
        {
            if (SetProperty(ref selectedPlaylist, value))
            {
                OnPropertyChanged(nameof(CanPlaySelectedPlaylist));
                NotifyPlaybackCommandAvailability();
            }
        }
    }

    public LibraryPlaylist? ActivePlaylist
    {
        get => activePlaylist;
        private set
        {
            if (SetProperty(ref activePlaylist, value))
            {
                OnPropertyChanged(nameof(ActivePlaylistDisplay));
            }
        }
    }

    public string ActivePlaylistDisplay => ActivePlaylist?.QualifiedDisplayName ?? "None";

    public LibraryPlaylist? PendingPlaylist
    {
        get => pendingPlaylist;
        private set
        {
            if (SetProperty(ref pendingPlaylist, value))
            {
                OnPropertyChanged(nameof(PendingPlaylistDisplay));
                OnPropertyChanged(nameof(CanStopAll));
            }
        }
    }

    public string PendingPlaylistDisplay => PendingPlaylist?.QualifiedDisplayName ?? "None";

    public LibraryTrack? CurrentTrack
    {
        get => currentTrack;
        private set
        {
            if (SetProperty(ref currentTrack, value))
            {
                OnPropertyChanged(nameof(CurrentTrackDisplayName));
                OnPropertyChanged(nameof(CurrentFileName));
                OnPropertyChanged(nameof(CanPauseResume));
                OnPropertyChanged(nameof(CanSkip));
                OnPropertyChanged(nameof(CanStopAll));
                NotifyFadeCommandAvailability();
            }
        }
    }

    public string CurrentTrackDisplayName => CurrentTrack?.Name ?? "Nothing playing";

    public string CurrentFileName => CurrentTrack is null
        ? "None"
        : Path.GetFileName(CurrentTrack.FilePath);

    public LibraryPlaylist? CurrentPlaylist
    {
        get => currentPlaylist;
        private set
        {
            if (SetProperty(ref currentPlaylist, value))
            {
                OnPropertyChanged(nameof(CurrentPlaylistDisplay));
            }
        }
    }

    public string CurrentPlaylistDisplay => CurrentPlaylist?.QualifiedDisplayName ?? "None";

    public PlaybackState PlaybackState
    {
        get => playbackState;
        private set
        {
            if (SetProperty(ref playbackState, value))
            {
                OnPropertyChanged(nameof(PlaybackStateDisplay));
                OnPropertyChanged(nameof(PauseResumeLabel));
                OnPropertyChanged(nameof(CanPauseResume));
                OnPropertyChanged(nameof(CanStopAll));
            }
        }
    }

    public string PlaybackStateDisplay => PlaybackState.ToString();

    public TimeSpan Elapsed
    {
        get => elapsed;
        private set
        {
            if (SetProperty(ref elapsed, value))
            {
                OnPropertyChanged(nameof(ElapsedDisplay));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public string ElapsedDisplay => FormatTime(Elapsed);

    public TimeSpan Duration
    {
        get => duration;
        private set
        {
            if (SetProperty(ref duration, value))
            {
                OnPropertyChanged(nameof(DurationDisplay));
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public string DurationDisplay => FormatTime(Duration);

    public string ProgressDisplay => $"{ElapsedDisplay} / {DurationDisplay}";

    public IReadOnlyList<PlaybackQueueEntry> ExplicitQueue
    {
        get => explicitQueue;
        private set
        {
            if (SetProperty(ref explicitQueue, value))
            {
                OnPropertyChanged(nameof(HasExplicitQueue));
                OnPropertyChanged(nameof(CanClearQueue));
            }
        }
    }

    public bool HasExplicitQueue => ExplicitQueue.Count > 0;

    public string? LatestPlaybackError
    {
        get => latestPlaybackError;
        private set
        {
            if (SetProperty(ref latestPlaybackError, value))
            {
                OnPropertyChanged(nameof(HasPlaybackError));
                OnPropertyChanged(nameof(LatestPlaybackErrorDisplay));
            }
        }
    }

    public bool HasPlaybackError => !string.IsNullOrWhiteSpace(LatestPlaybackError);

    public string LatestPlaybackErrorDisplay => LatestPlaybackError ?? "No playback errors.";

    public ImmediateTransitionMode PreferredImmediateTransitionMode
    {
        get => preferredImmediateTransitionMode;
        private set
        {
            if (SetProperty(ref preferredImmediateTransitionMode, value))
            {
                OnPropertyChanged(nameof(IsHardCutSelected));
                OnPropertyChanged(nameof(IsCrossfadeSelected));
                OnPropertyChanged(nameof(TransitionModeLabel));
                OnPropertyChanged(nameof(TransitionLabel));
                OnPropertyChanged(nameof(ImmediateTransitionModeLabel));
            }
        }
    }

    public bool IsHardCutSelected =>
        PreferredImmediateTransitionMode == ImmediateTransitionMode.HardCut;

    public bool IsCrossfadeSelected =>
        PreferredImmediateTransitionMode == ImmediateTransitionMode.Crossfade;

    public string PlayNowLabel => "Play Now";

    public string TransitionModeLabel => PreferredImmediateTransitionMode switch
    {
        ImmediateTransitionMode.Crossfade => "Transition: Crossfade",
        _ => "Transition: Hard Cut",
    };

    public string TransitionLabel => TransitionModeLabel;

    public string ImmediateTransitionModeLabel => TransitionModeLabel;

    public float MasterGain
    {
        get => masterGain;
        private set
        {
            if (SetProperty(ref masterGain, value))
            {
                OnPropertyChanged(nameof(MasterGainDisplay));
                OnPropertyChanged(nameof(MasterFadeGainDisplay));
                OnPropertyChanged(nameof(MasterFadeGain));
            }
        }
    }

    public string MasterGainDisplay =>
        $"{Math.Clamp((int)Math.Round(MasterGain * 100), 0, 100)}%";

    public string MasterFadeGainDisplay => MasterGainDisplay;

    public float MasterFadeGain => MasterGain;

    public float MusicFadeGain
    {
        get => musicFadeGain;
        private set
        {
            if (SetProperty(ref musicFadeGain, NormalizeVolume(value, musicFadeGain)))
            {
                OnPropertyChanged(nameof(MusicFadeGainDisplay));
            }
        }
    }

    public string MusicFadeGainDisplay => FormatPercent(MusicFadeGain);

    public MasterFadeState MusicFadeState
    {
        get => musicFadeState;
        private set
        {
            if (SetProperty(ref musicFadeState, value))
            {
                OnPropertyChanged(nameof(MusicFadeStateDisplay));
                OnPropertyChanged(nameof(MusicFadeDirectionLabel));
                OnPropertyChanged(nameof(MusicFadeLabel));
                NotifyFadeCommandAvailability();
            }
        }
    }

    public string MusicFadeStateDisplay => FormatFadeState(MusicFadeState);

    public float AmbienceFadeGain
    {
        get => ambienceFadeGain;
        private set
        {
            if (SetProperty(ref ambienceFadeGain, NormalizeVolume(value, ambienceFadeGain)))
            {
                OnPropertyChanged(nameof(AmbienceFadeGainDisplay));
            }
        }
    }

    public string AmbienceFadeGainDisplay => FormatPercent(AmbienceFadeGain);

    public MasterFadeState AmbienceFadeState
    {
        get => ambienceFadeState;
        private set
        {
            if (SetProperty(ref ambienceFadeState, value))
            {
                OnPropertyChanged(nameof(AmbienceFadeStateDisplay));
                OnPropertyChanged(nameof(AmbienceFadeDirectionLabel));
                OnPropertyChanged(nameof(AmbienceFadeLabel));
                NotifyFadeCommandAvailability();
            }
        }
    }

    public string AmbienceFadeStateDisplay => FormatFadeState(AmbienceFadeState);

    public float MusicVolume
    {
        get => musicVolume;
        private set
        {
            value = NormalizeVolume(value, musicVolume);
            if (SetProperty(ref musicVolume, value))
            {
                OnPropertyChanged(nameof(MusicVolumeDisplay));
            }
        }
    }

    public string MusicVolumeDisplay => FormatPercent(MusicVolume);

    public float AmbienceVolume
    {
        get => ambienceVolume;
        private set
        {
            value = NormalizeVolume(value, ambienceVolume);
            if (SetProperty(ref ambienceVolume, value))
            {
                OnPropertyChanged(nameof(AmbienceVolumeDisplay));
            }
        }
    }

    public string AmbienceVolumeDisplay => FormatPercent(AmbienceVolume);

    public float MasterVolume
    {
        get => masterVolume;
        private set
        {
            value = NormalizeVolume(value, masterVolume);
            if (SetProperty(ref masterVolume, value))
            {
                OnPropertyChanged(nameof(MasterVolumeDisplay));
            }
        }
    }

    public string MasterVolumeDisplay => FormatPercent(MasterVolume);

    public TimeSpan ConfiguredFastFadeDuration => TimeSpan.FromSeconds(fastFadeSeconds);

    public TimeSpan ConfiguredMediumFadeDuration => TimeSpan.FromSeconds(mediumFadeSeconds);

    public TimeSpan ConfiguredSlowFadeDuration => TimeSpan.FromSeconds(slowFadeSeconds);

    public TimeSpan ConfiguredCrossfadeStaggerDuration => TimeSpan.FromSeconds(crossfadeStaggerSeconds);

    public MasterFadeState MasterFadeState
    {
        get => masterFadeState;
        private set
        {
            if (SetProperty(ref masterFadeState, value))
            {
                OnPropertyChanged(nameof(MasterFadeStateDisplay));
                OnPropertyChanged(nameof(MasterFadeDirectionLabel));
                OnPropertyChanged(nameof(MasterFadeLabel));
                NotifyFadeCommandAvailability();
            }
        }
    }

    public string MasterFadeStateDisplay => MasterFadeState switch
    {
        Soundrel.Models.MasterFadeState.FadingOut => "Fading Out",
        Soundrel.Models.MasterFadeState.Muted => "Muted",
        Soundrel.Models.MasterFadeState.FadingIn => "Fading In",
        _ => "Full",
    };

    public string MasterFadeDirectionLabel => GetFadeDirectionLabel(MasterFadeState);

    public string MusicFadeDirectionLabel => GetFadeDirectionLabel(MusicFadeState);

    public string AmbienceFadeDirectionLabel => GetFadeDirectionLabel(AmbienceFadeState);

    public bool IsFastFadeSelected
    {
        get => isFastFadeSelected;
        private set
        {
            if (SetProperty(ref isFastFadeSelected, value))
            {
                OnPropertyChanged(nameof(IsSlowFadeSelected));
                OnPropertyChanged(nameof(IsFastFade));
                OnPropertyChanged(nameof(IsSlowFade));
                OnPropertyChanged(nameof(FadeSpeedLabel));
                OnPropertyChanged(nameof(FadeSpeedDisplay));
                OnPropertyChanged(nameof(MasterFadeSpeedLabel));
            }
        }
    }

    public bool IsSlowFadeSelected => !IsFastFadeSelected;

    public bool IsFastFade => IsFastFadeSelected;

    public bool IsSlowFade => IsSlowFadeSelected;

    public string FadeSpeedLabel => IsFastFadeSelected
        ? $"Fast ({FormatDuration(ConfiguredFastFadeDuration)})"
        : $"Slow ({FormatDuration(ConfiguredSlowFadeDuration)})";

    public string FadeSpeedDisplay => FadeSpeedLabel;

    public string MasterFadeSpeedLabel => FadeSpeedLabel;

    public bool CanPlaySelectedPlaylist => SelectedPlaylist is not null && !IsDisposed;

    public bool CanPlayNow =>
        CanPlaySelectedPlaylist && !IsPlaybackOperationInFlight(PlaybackOperation.PlayNow);

    public bool CanPlayAfterCurrent =>
        CanPlaySelectedPlaylist && !IsPlaybackOperationInFlight(PlaybackOperation.AfterCurrent);

    public bool CanQueueTrack =>
        SelectedPlaylist is not null &&
        !IsDisposed &&
            !IsPlaybackOperationInFlight(PlaybackOperation.QueueTrack);

    public bool CanPlayTrackNow => CanPlaySelectedPlaylist && !IsDisposed &&
        !IsPlaybackOperationInFlight(PlaybackOperation.PlayNowTrack);

    public bool CanPlayTrackNowFor(LibraryTrack track) =>
        track is not null && CanPlayTrackNow && SelectedTracks.Contains(track);

    public string PauseResumeLabel => PlaybackState switch
    {
        PlaybackState.Playing => "Pause",
        PlaybackState.Paused => "Resume",
        _ => "Pause / Resume",
    };

    public bool CanPauseResume =>
        CurrentTrack is not null &&
        PlaybackState is PlaybackState.Playing or PlaybackState.Paused &&
        !IsDisposed &&
        !IsPlaybackOperationInFlight(PlaybackOperation.PauseResume);

    public bool CanSkip =>
        CurrentTrack is not null &&
        !IsDisposed &&
        !IsPlaybackOperationInFlight(PlaybackOperation.Skip);

    public bool CanClearQueue =>
        ExplicitQueue.Count > 0 &&
        !IsDisposed &&
        !IsPlaybackOperationInFlight(PlaybackOperation.ClearQueue);

    public bool CanStopAll =>
        (CurrentTrack is not null ||
         PendingPlaylist is not null ||
         PlaybackState != PlaybackState.Stopped ||
         HasActiveAmbience) &&
        !IsDisposed &&
        !IsPlaybackOperationInFlight(PlaybackOperation.StopAll);

    public bool CanQuickFadeIn => CanFadeIn &&
        !IsPlaybackOperationInFlight(PlaybackOperation.QuickFadeIn);

    public bool CanQuickFadeOut => CanFadeOut &&
        !IsPlaybackOperationInFlight(PlaybackOperation.QuickFadeOut);

    public bool CanSlowFadeIn => CanFadeIn &&
        !IsPlaybackOperationInFlight(PlaybackOperation.SlowFadeIn);

    public bool CanSlowFadeOut => CanFadeOut &&
        !IsPlaybackOperationInFlight(PlaybackOperation.SlowFadeOut);

    public bool CanFadeMusic => HasMusicPlayback &&
        !IsDisposed && !IsPlaybackOperationInFlight(PlaybackOperation.FadeMusic);

    public bool CanFadeAmbience => HasActiveAmbience &&
        !IsDisposed && !IsPlaybackOperationInFlight(PlaybackOperation.FadeAmbience);

    public bool CanFadeMaster => HasActivePlayback &&
        !IsDisposed && !IsPlaybackOperationInFlight(PlaybackOperation.FadeMaster);

    public bool CanToggleMusicFade => CanFadeMusic;

    public bool CanToggleAmbienceFade => CanFadeAmbience;

    public bool CanToggleMasterFade => CanFadeMaster;

    public string MusicFadeLabel => MusicFadeDirectionLabel;

    public string AmbienceFadeLabel => AmbienceFadeDirectionLabel;

    public string MasterFadeLabel => MasterFadeDirectionLabel;

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(CanRescan));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool CanRescan => !IsBusy && !string.IsNullOrWhiteSpace(SelectedLibraryPath);

    public async Task InitializeAsync()
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            SettingsLoadResult loadResult;
            try
            {
                loadResult = await Task.Run(settingsService.Load);
            }
            catch (Exception exception)
            {
                ClearLibrary();
                StatusText = $"Settings could not be loaded: {exception.Message} {SelectLibraryPrompt}";
                return;
            }

            PreferredImmediateTransitionMode = loadResult.Settings.PreferredImmediateTransitionMode;
            MusicVolume = loadResult.Settings.MusicVolume;
            AmbienceVolume = loadResult.Settings.AmbienceVolume;
            MasterVolume = loadResult.Settings.MasterVolume;
            ApplyTimingSettings(loadResult.Settings);
            SetPersistedAmbiencePresets(loadResult.Settings.AmbiencePresets);
            await ApplyInitialPlaybackSettingsAsync();

            string? initialStatus = loadResult.Warning;
            if (loadResult.RequiresPersistenceMigration)
            {
                try
                {
                    await SaveSettingsAsync(loadResult.Settings);
                }
                catch (Exception exception)
                {
                    initialStatus = JoinMessages(
                        initialStatus,
                        $"Legacy settings were migrated in memory but could not be saved: {exception.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(loadResult.Settings.SelectedLibraryPath))
            {
                ClearLibrary();
                StatusText = JoinMessages(initialStatus, SelectLibraryPrompt);
                return;
            }

            SelectedLibraryPath = NormalizeLibraryPathForSelection(loadResult.Settings.SelectedLibraryPath);
            await ScanCurrentLibraryAsync(initialStatus);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectLibraryAsync(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);

        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            SelectedLibraryPath = NormalizeLibraryPathForSelection(libraryPath);
            Task settingsSaveTask = SaveSettingsAsync(
                SelectedLibraryPath,
                PreferredImmediateTransitionMode);
            await ScanCurrentLibraryAsync();

            try
            {
                await settingsSaveTask;
            }
            catch (Exception exception)
            {
                StatusText = JoinMessages(
                    StatusText,
                    $"The selected library could not be saved to settings: {exception.Message}");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RescanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedLibraryPath) || !TryBeginOperation())
        {
            return;
        }

        try
        {
            await ScanCurrentLibraryAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectNode(LibraryTreeNode? node)
    {
        if (node?.Playlist is null)
        {
            SelectedPlaylist = null;
            SelectedPlaylistName = "No playlist selected";
            SelectedTracks = Array.Empty<LibraryTrack>();
            return;
        }

        SelectedPlaylist = node.Playlist;
        SelectedPlaylistName = node.Playlist.QualifiedDisplayName;
        SelectedTracks = node.Playlist.Tracks;
    }

    public Task PlayNowAsync()
    {
        LibraryPlaylist? playlist = SelectedPlaylist;
        ImmediateTransitionMode transitionMode = PreferredImmediateTransitionMode;
        TimeSpan fadeDuration = SelectedAutomaticFadeDuration;
        return playlist is null
            ? Task.CompletedTask
            : RunPlaybackOperationAsync(
                PlaybackOperation.PlayNow,
                () => playbackController.PlayNowAsync(playlist, transitionMode, fadeDuration));
    }

    public Task PlayNowAsync(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        LibraryPlaylist? sourcePlaylist = SelectedPlaylist;
        if (sourcePlaylist is null || !SelectedTracks.Contains(track))
        {
            return Task.CompletedTask;
        }

        ImmediateTransitionMode transitionMode = PreferredImmediateTransitionMode;
        TimeSpan fadeDuration = SelectedAutomaticFadeDuration;
        return RunPlaybackOperationAsync(
            PlaybackOperation.PlayNowTrack,
            () => playbackController.PlayNowAsync(
                track,
                sourcePlaylist,
                transitionMode,
                fadeDuration));
    }

    public Task PlayTrackNowAsync(LibraryTrack track) => PlayNowAsync(track);

    public Task ToggleImmediateTransitionModeAsync() =>
        SetImmediateTransitionModeAsync(
            PreferredImmediateTransitionMode == ImmediateTransitionMode.HardCut
                ? ImmediateTransitionMode.Crossfade
                : ImmediateTransitionMode.HardCut);

    public Task ToggleTransitionModeAsync() => ToggleImmediateTransitionModeAsync();

    public Task ToggleTransitionMode() => ToggleImmediateTransitionModeAsync();

    public Task SetImmediateTransitionModeAsync(ImmediateTransitionMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The transition mode is not supported.");
        }

        return SetImmediateTransitionModeCoreAsync(mode);
    }

    public Task AfterCurrentAsync()
    {
        LibraryPlaylist? playlist = SelectedPlaylist;
        return playlist is null
            ? Task.CompletedTask
            : RunPlaybackOperationAsync(
                PlaybackOperation.AfterCurrent,
                () => playbackController.AfterCurrentAsync(playlist));
    }

    public Task QueueTrackAsync(LibraryTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        LibraryPlaylist? sourcePlaylist = SelectedPlaylist;
        if (sourcePlaylist is null || !SelectedTracks.Contains(track))
        {
            return Task.CompletedTask;
        }

        return RunPlaybackOperationAsync(
            PlaybackOperation.QueueTrack,
            () => playbackController.QueueTrackAsync(track, sourcePlaylist));
    }

    public Task PauseResumeAsync()
        => RunPlaybackOperationAsync(
            PlaybackOperation.PauseResume,
            ResolvePauseResumeAsync);

    public Task SkipAsync() =>
        RunPlaybackOperationAsync(PlaybackOperation.Skip, playbackController.SkipAsync);

    public Task ClearQueueAsync() =>
        RunPlaybackOperationAsync(PlaybackOperation.ClearQueue, playbackController.ClearQueueAsync);

    public Task StopAllAsync() =>
        RunStopAllWithDuration(SelectedAutomaticFadeDuration);

    public Task PlayAmbienceAsync(AmbienceTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        float sourceGain = track.Gain;
        TimeSpan fadeDuration = SelectedAutomaticFadeDuration;
        return RunPlaybackOperationAsync(
            PlaybackOperation.PlayAmbience,
            GetPathIdentity(track.FilePath),
            () => playbackController.PlayAmbienceAsync(
                track.Track,
                sourceGain,
                fadeDuration));
    }

    public Task StopAmbienceAsync(AmbienceTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return RunPlaybackOperationAsync(
            PlaybackOperation.StopAmbience,
            GetPathIdentity(track.FilePath),
            () => playbackController.StopAmbienceAsync(track.Track));
    }

    public Task ToggleAmbienceAsync(AmbienceTrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.CanStop
            ? StopAmbienceAsync(track)
            : PlayAmbienceAsync(track);
    }

    public Task ToggleAmbiencePlayAsync(AmbienceTrackViewModel track) => ToggleAmbienceAsync(track);

    public Task ToggleAmbience(AmbienceTrackViewModel track) => ToggleAmbienceAsync(track);

    public Task SaveAmbiencePresetAsync(string? name = null)
    {
        string presetName = (name ?? AmbiencePresetName).Trim();
        if (presetName.Length == 0 || !TryGetCurrentLibraryRoot(out string libraryRoot))
        {
            return Task.CompletedTask;
        }

        return RunPlaybackOperationAsync(
            PlaybackOperation.SaveAmbiencePreset,
            async () =>
            {
                PlaybackSnapshot snapshot = playbackController.Snapshot;
                var tracks = new List<AmbiencePresetTrack>();
                string ambienceRoot = GetAmbienceRoot(libraryRoot);
                foreach (AmbiencePlaybackSnapshot source in snapshot.AmbienceSnapshots)
                {
                    if (source.State is not (AmbiencePlaybackState.FadingIn or AmbiencePlaybackState.Playing) ||
                        !float.IsFinite(source.SourceGain) ||
                        source.SourceGain is < 0f or > 1f)
                    {
                        continue;
                    }

                    if (!TryGetRelativeAmbiencePath(
                            source.FilePath,
                            libraryRoot,
                            ambienceRoot,
                            out string relativePath))
                    {
                        continue;
                    }

                    tracks.Add(new AmbiencePresetTrack(relativePath, source.SourceGain));
                }

                AmbiencePreset preset = new(presetName, tracks.ToArray());
                IReadOnlyList<LibraryAmbiencePresets> updatedPresets = UpsertAmbiencePreset(
                    libraryRoot,
                    preset);
                AppSettings? settings = null;
                await RunOnUiContextAsync(() =>
                {
                    ambiencePresets = updatedPresets;
                    if (IsCurrentLibrary(libraryRoot))
                    {
                        RefreshCurrentLibraryPresets(clearSelection: false);
                        SelectedAmbiencePreset = CurrentLibraryPresets.FirstOrDefault(
                            candidate => string.Equals(candidate.Name, presetName, StringComparison.OrdinalIgnoreCase));
                        AmbiencePresetName = presetName;
                    }
                    settings = CreateSettings(SelectedLibraryPath, PreferredImmediateTransitionMode);
                }).ConfigureAwait(false);

                try
                {
                    await SaveSettingsAsync(settings!).ConfigureAwait(false);
                    await RunOnUiContextAsync(() => StatusText =
                        $"Ambience preset '{presetName}' saved with {tracks.Count} source(s).").ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await RunOnUiContextAsync(() => StatusText = JoinMessages(
                        StatusText,
                        $"The ambience preset could not be saved to settings: {exception.Message}")).ConfigureAwait(false);
                }
            });
    }

    public Task ApplyAmbiencePresetAsync()
    {
        AmbiencePreset? preset = SelectedAmbiencePreset;
        if (preset is null || !TryGetCurrentLibraryRoot(out string libraryRoot))
        {
            return Task.CompletedTask;
        }

        // Capture the selection and the complete (unfiltered) catalogue at
        // invocation time. The controller snapshot is read inside the FIFO
        // item so preceding ambience commands are observed in order.
        AmbiencePreset selectedPreset = preset;
        HashSet<string> catalogPaths = AmbienceTracks
            .Select(track => GetPathIdentity(track.FilePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TimeSpan automaticFadeDuration = SelectedAutomaticFadeDuration;
        return RunPlaybackOperationAsync(
            PlaybackOperation.ApplyAmbiencePreset,
            async () =>
            {
                PlaybackSnapshot snapshot = playbackController.Snapshot;
                string ambienceRoot = GetAmbienceRoot(libraryRoot);
                var targets = new List<AmbiencePresetTarget>();
                var skippedPaths = new List<string>();
                foreach (AmbiencePresetTrack track in selectedPreset.Tracks)
                {
                    if (!AmbiencePresetPath.TryNormalizeRelativeTrackPath(
                            track.RelativePath,
                            libraryRoot,
                            out string relativePath))
                    {
                        skippedPaths.Add(track.RelativePath);
                        continue;
                    }

                    string filePath = GetPathIdentity(Path.Combine(
                        ambienceRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    bool representedByController = snapshot.AmbienceSnapshots.Any(source =>
                        string.Equals(GetPathIdentity(source.FilePath), filePath, StringComparison.OrdinalIgnoreCase));
                    if (!catalogPaths.Contains(filePath) && !representedByController)
                    {
                        skippedPaths.Add(relativePath);
                        continue;
                    }

                    targets.Add(new AmbiencePresetTarget(filePath, track.SourceVolume));
                }

                AmbiencePresetApplicationResult result =
                    await playbackController.ApplyAmbiencePresetAsync(
                        targets,
                        automaticFadeDuration).ConfigureAwait(false);
                string status =
                    $"Ambience preset '{selectedPreset.Name}' applied: {result.SucceededCount} source(s).";
                if (skippedPaths.Count > 0)
                {
                    status += $" Skipped missing source(s): {string.Join(", ", skippedPaths)}.";
                }

                if (result.HasFailures)
                {
                    status += $" {result.FailureCount} source(s) failed: " +
                        string.Join(" ", result.Failures.Select(failure => failure.Message));
                }

                await RunOnUiContextAsync(() => StatusText = status).ConfigureAwait(false);
            });
    }

    public Task DeleteAmbiencePresetAsync()
    {
        AmbiencePreset? preset = SelectedAmbiencePreset;
        if (preset is null || !TryGetCurrentLibraryRoot(out string libraryRoot))
        {
            return Task.CompletedTask;
        }

        return RunPlaybackOperationAsync(
            PlaybackOperation.DeleteAmbiencePreset,
            () => DeleteAmbiencePresetCoreAsync(preset, libraryRoot));
    }

    public async Task SetAmbienceSourceGainAsync(AmbienceTrackViewModel track, float sourceGain)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidateVolume(sourceGain, nameof(sourceGain));
        await RunOnUiContextAsync(() => track.Gain = sourceGain).ConfigureAwait(false);

        await RunPlaybackOperationAsync(
            PlaybackOperation.SetAmbienceGain,
            GetPathIdentity(track.FilePath),
            () => playbackController.SetAmbienceSourceGainAsync(track.Track, sourceGain))
            .ConfigureAwait(false);
    }

    public Task SetMusicVolumeAsync(float volume) => SetVolumeAsync(VolumeKind.Music, volume);

    public Task SetAmbienceVolumeAsync(float volume) => SetVolumeAsync(VolumeKind.Ambience, volume);

    public Task SetMasterVolumeAsync(float volume) => SetVolumeAsync(VolumeKind.Master, volume);

    public Task QuickFadeInAsync() => RunFadeAsync(
        PlaybackOperation.QuickFadeIn,
        MasterFadeDirection.In,
        ConfiguredFastFadeDuration);

    public Task QuickFadeOutAsync() => RunFadeAsync(
        PlaybackOperation.QuickFadeOut,
        MasterFadeDirection.Out,
        ConfiguredFastFadeDuration);

    public Task SlowFadeInAsync() => RunFadeAsync(
        PlaybackOperation.SlowFadeIn,
        MasterFadeDirection.In,
        ConfiguredSlowFadeDuration);

    public Task SlowFadeOutAsync() => RunFadeAsync(
        PlaybackOperation.SlowFadeOut,
        MasterFadeDirection.Out,
        ConfiguredSlowFadeDuration);

    public void ToggleFadeSpeed() => IsFastFadeSelected = !IsFastFadeSelected;

    public Task ToggleFadeSpeedAsync()
    {
        ToggleFadeSpeed();
        return Task.CompletedTask;
    }

    public Task ToggleMasterFadeAsync()
    {
        TimeSpan fadeDuration = SelectedFadeDuration;
        return RunStateAwareFadeAsync(
            PlaybackOperation.FadeMaster,
            snapshot => snapshot.MasterFadeState,
            fadeDuration,
            direction => playbackController.FadeMasterAsync(direction, fadeDuration));
    }

    public Task ToggleMusicFadeAsync()
    {
        TimeSpan fadeDuration = SelectedFadeDuration;
        return RunStateAwareFadeAsync(
            PlaybackOperation.FadeMusic,
            snapshot => snapshot.MusicFadeState,
            fadeDuration,
            direction => playbackController.FadeMusicAsync(direction, fadeDuration));
    }

    public Task ToggleAmbienceFadeAsync()
    {
        TimeSpan fadeDuration = SelectedFadeDuration;
        return RunStateAwareFadeAsync(
            PlaybackOperation.FadeAmbience,
            snapshot => snapshot.AmbienceFadeState,
            fadeDuration,
            direction => playbackController.FadeAmbienceAsync(direction, fadeDuration));
    }

    public Task ToggleMusicFade() => ToggleMusicFadeAsync();

    public Task ToggleAmbienceFade() => ToggleAmbienceFadeAsync();

    public Task ToggleMasterFade() => ToggleMasterFadeAsync();

    public Task FadeMusicAsync() => ToggleMusicFadeAsync();

    public Task FadeAmbienceAsync() => ToggleAmbienceFadeAsync();

    public Task FadeMasterAsync() => ToggleMasterFadeAsync();

    public async Task UpdatePlaybackProgressAsync()
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            AudioProgressSnapshot progress = await playbackController.GetProgressAsync();
            PlaybackSnapshot snapshot = playbackController.Snapshot;
            await RunOnUiContextAsync(() =>
            {
                ApplyPlaybackSnapshot(snapshot);
                ApplyFadeProgress(progress);
                if (progress.PlaybackId is null || progress.PlaybackId != snapshot.CurrentPlaybackId)
                {
                    return;
                }

                TimeSpan progressDuration = progress.Duration > TimeSpan.Zero
                    ? progress.Duration
                    : snapshot.CurrentDuration ?? TimeSpan.Zero;
                Duration = progressDuration;
                Elapsed = progressDuration > TimeSpan.Zero && progress.Position > progressDuration
                    ? progressDuration
                    : Max(progress.Position, TimeSpan.Zero);
            });
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            await SetPlaybackErrorAsync("Could not update playback progress.", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (playbackOperationLock)
        {
            if (disposeTask is not null)
            {
                return new ValueTask(disposeTask);
            }

            Volatile.Write(ref disposeStarted, 1);
            Task shutdownTask = playbackDispatcher.Enqueue(DisposePlaybackAsync);
            playbackDispatcher.Complete();
            disposeTask = AwaitShutdownAsync(shutdownTask);
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposePlaybackAsync()
    {
        playbackController.StateChanged -= PlaybackController_StateChanged;
        playbackController.ErrorOccurred -= PlaybackController_ErrorOccurred;
        await RunOnUiContextAsync(NotifyPlaybackCommandAvailability).ConfigureAwait(false);

        try
        {
            await playbackController.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SetPlaybackErrorAsync("Could not shut down audio playback cleanly.", exception)
                .ConfigureAwait(false);
        }

        PlaybackSnapshot snapshot = playbackController.Snapshot;
        await RunOnUiContextAsync(() => ApplyPlaybackSnapshot(snapshot)).ConfigureAwait(false);
    }

    private async Task AwaitShutdownAsync(Task shutdownTask)
    {
        try
        {
            await shutdownTask.ConfigureAwait(false);
        }
        finally
        {
            await playbackDispatcher.Completion.ConfigureAwait(false);
        }
    }

    public void ReportError(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            StatusText = message;
        }
    }

    private bool TryBeginOperation()
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        return true;
    }

    private async Task ScanCurrentLibraryAsync(string? initialStatus = null)
    {
        string libraryPath = SelectedLibraryPath!;
        StatusText = JoinMessages(initialStatus, $"Scanning {libraryPath}...");

        LibraryCatalog? catalog = null;
        string? scanFailure = null;

        try
        {
            catalog = await Task.Run(() => libraryScanner.Scan(libraryPath));
        }
        catch (Exception exception)
        {
            scanFailure = $"Library scan failed: {exception.Message}";
        }

        if (catalog is null)
        {
            ClearCatalogContents();
            StatusText = JoinMessages(initialStatus, scanFailure);
            return;
        }

        ApplyCatalog(catalog);
        StatusText = JoinMessages(initialStatus, DescribeCatalog(catalog));
    }

    private void ApplyCatalog(LibraryCatalog catalog)
    {
        LibraryNodes = catalog.Groups
            .Select(LibraryTreeNode.FromGroup)
            .Concat(catalog.Playlists.Select(LibraryTreeNode.FromPlaylist))
            .ToArray();
        var priorRows = AmbienceTracks.ToDictionary(
            track => GetPathIdentity(track.FilePath),
            StringComparer.OrdinalIgnoreCase);
        AmbienceTracks = catalog.AmbienceTracks
            .Select(track =>
            {
                if (priorRows.TryGetValue(GetPathIdentity(track.FilePath), out var prior))
                {
                    prior.UpdateTrack(track);
                    return prior;
                }

                return new AmbienceTrackViewModel(track);
            })
            .ToArray();
        ScanIssues = catalog.Issues;
        SelectedPlaylist = null;
        SelectedPlaylistName = "No playlist selected";
        SelectedTracks = Array.Empty<LibraryTrack>();
    }

    private async Task DeleteAmbiencePresetCoreAsync(
        AmbiencePreset preset,
        string libraryRoot)
    {
        IReadOnlyList<LibraryAmbiencePresets> updatedPresets = RemoveAmbiencePreset(
            libraryRoot,
            preset.Name,
            ambiencePresets);
        AppSettings? settings = null;
        await RunOnUiContextAsync(() =>
        {
            ambiencePresets = updatedPresets;
            if (IsCurrentLibrary(libraryRoot))
            {
                RefreshCurrentLibraryPresets(clearSelection: true);
            }
            settings = CreateSettings(SelectedLibraryPath, PreferredImmediateTransitionMode);
        }).ConfigureAwait(false);

        try
        {
            await SaveSettingsAsync(settings!).ConfigureAwait(false);
            await RunOnUiContextAsync(() => StatusText =
                $"Ambience preset '{preset.Name}' deleted.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiContextAsync(() => StatusText = JoinMessages(
                StatusText,
                $"The ambience preset could not be deleted from settings: {exception.Message}")).ConfigureAwait(false);
        }
    }

    private void SetPersistedAmbiencePresets(IReadOnlyList<LibraryAmbiencePresets>? presets)
    {
        ambiencePresets = presets ?? Array.Empty<LibraryAmbiencePresets>();
        RefreshCurrentLibraryPresets(clearSelection: true);
    }

    private void RefreshCurrentLibraryPresets(bool clearSelection)
    {
        string? libraryRoot = null;
        if (AmbiencePresetPath.TryNormalizeLibraryRootPath(SelectedLibraryPath, out string normalizedRoot))
        {
            libraryRoot = normalizedRoot;
        }

        currentLibraryPresets = libraryRoot is null
            ? Array.Empty<AmbiencePreset>()
            : ambiencePresets
                .FirstOrDefault(group => string.Equals(
                    group.LibraryRootPath,
                    libraryRoot,
                    StringComparison.OrdinalIgnoreCase))?.Presets
                .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.Name, StringComparer.Ordinal)
                .ToArray()
                ?? Array.Empty<AmbiencePreset>();
        OnPropertyChanged(nameof(CurrentLibraryPresets));

        if (clearSelection ||
            selectedAmbiencePreset is not null && !currentLibraryPresets.Contains(selectedAmbiencePreset))
        {
            SelectedAmbiencePreset = null;
            AmbiencePresetName = string.Empty;
        }

        OnPropertyChanged(nameof(CanSaveAmbiencePreset));
        OnPropertyChanged(nameof(CanDeleteAmbiencePreset));
        OnPropertyChanged(nameof(CanApplyAmbiencePreset));
    }

    private bool IsCurrentLibrary(string libraryRoot) =>
        TryGetCurrentLibraryRoot(out string currentRoot) &&
        string.Equals(currentRoot, libraryRoot, StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<LibraryAmbiencePresets> UpsertAmbiencePreset(
        string libraryRoot,
        AmbiencePreset preset)
    {
        List<LibraryAmbiencePresets> groups = ambiencePresets.ToList();
        int libraryIndex = groups.FindIndex(group => string.Equals(
            group.LibraryRootPath,
            libraryRoot,
            StringComparison.OrdinalIgnoreCase));
        if (libraryIndex < 0)
        {
            groups.Add(new LibraryAmbiencePresets(libraryRoot, [preset]));
            return groups.ToArray();
        }

        LibraryAmbiencePresets group = groups[libraryIndex];
        List<AmbiencePreset> presets = group.Presets.ToList();
        int presetIndex = presets.FindIndex(candidate => string.Equals(
            candidate.Name,
            preset.Name,
            StringComparison.OrdinalIgnoreCase));
        if (presetIndex >= 0)
        {
            presets[presetIndex] = preset;
        }
        else
        {
            presets.Add(preset);
        }

        groups[libraryIndex] = new LibraryAmbiencePresets(libraryRoot, presets.ToArray());
        return groups.ToArray();
    }

    private static IReadOnlyList<LibraryAmbiencePresets> RemoveAmbiencePreset(
        string libraryRoot,
        string presetName,
        IReadOnlyList<LibraryAmbiencePresets> source)
    {
        List<LibraryAmbiencePresets> groups = source.ToList();
        int libraryIndex = groups.FindIndex(group => string.Equals(
            group.LibraryRootPath,
            libraryRoot,
            StringComparison.OrdinalIgnoreCase));
        if (libraryIndex < 0)
        {
            return groups.ToArray();
        }

        LibraryAmbiencePresets group = groups[libraryIndex];
        List<AmbiencePreset> presets = group.Presets
            .Where(preset => !string.Equals(preset.Name, presetName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (presets.Count == 0)
        {
            groups.RemoveAt(libraryIndex);
        }
        else
        {
            groups[libraryIndex] = new LibraryAmbiencePresets(libraryRoot, presets.ToArray());
        }

        return groups.ToArray();
    }

    private bool TryGetCurrentLibraryRoot(out string libraryRoot) =>
        AmbiencePresetPath.TryNormalizeLibraryRootPath(SelectedLibraryPath, out libraryRoot);

    private static string? NormalizeLibraryPathForSelection(string? path) =>
        AmbiencePresetPath.TryNormalizeLibraryRootPath(path, out string normalizedPath)
            ? normalizedPath
            : path;

    private static string GetAmbienceRoot(string libraryRoot) =>
        Path.GetFullPath(Path.Combine(libraryRoot, "Ambience"));

    private static bool TryGetRelativeAmbiencePath(
        string filePath,
        string libraryRoot,
        string ambienceRoot,
        out string relativePath)
    {
        relativePath = string.Empty;
        try
        {
            string candidate = GetPathIdentity(filePath);
            string relative = Path.GetRelativePath(ambienceRoot, candidate);
            return AmbiencePresetPath.TryNormalizeRelativeTrackPath(
                relative,
                libraryRoot,
                out relativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void ClearLibrary()
    {
        SelectedLibraryPath = null;
        ClearCatalogContents();
    }

    private void ClearCatalogContents()
    {
        LibraryNodes = Array.Empty<LibraryTreeNode>();
        AmbienceTracks = Array.Empty<AmbienceTrackViewModel>();
        ScanIssues = Array.Empty<LibraryScanIssue>();
        SelectedPlaylist = null;
        SelectedPlaylistName = "No playlist selected";
        SelectedTracks = Array.Empty<LibraryTrack>();
    }

    private bool IsDisposed => Volatile.Read(ref disposeStarted) != 0;

    private bool CanFadeIn =>
        HasActivePlayback &&
        !IsDisposed &&
        MasterFadeState != Soundrel.Models.MasterFadeState.Full;

    private bool CanFadeOut =>
        HasActivePlayback &&
        !IsDisposed &&
        MasterFadeState != Soundrel.Models.MasterFadeState.Muted;

    private bool HasActivePlayback =>
        CurrentTrack is not null || HasActiveAmbience;

    private bool HasMusicPlayback => CurrentTrack is not null;

    private bool HasActiveAmbience =>
        hasPhysicalAmbience || AmbienceTracks.Any(track => track.IsActive);

    private TimeSpan SelectedFadeDuration => IsFastFadeSelected
        ? ConfiguredFastFadeDuration
        : ConfiguredSlowFadeDuration;

    private TimeSpan SelectedAutomaticFadeDuration => SelectedFadeDuration;

    private void UpdateFilteredAmbienceTracks()
    {
        string filter = AmbienceSearchText.Trim();
        filteredAmbienceTracks = string.IsNullOrEmpty(filter)
            ? AmbienceTracks
            : AmbienceTracks
                .Where(track => track.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        OnPropertyChanged(nameof(FilteredAmbienceTracks));
        OnPropertyChanged(nameof(VisibleAmbienceTracks));
    }

    private async Task SetImmediateTransitionModeCoreAsync(ImmediateTransitionMode mode)
    {
        AppSettings? settings = null;
        await RunOnUiContextAsync(() =>
        {
            PreferredImmediateTransitionMode = mode;
            settings = CreateSettings(SelectedLibraryPath, mode);
        });

        try
        {
            await SaveSettingsAsync(settings!);
        }
        catch (Exception exception)
        {
            await RunOnUiContextAsync(() => StatusText = JoinMessages(
                StatusText,
                $"The transition preference could not be saved to settings: {exception.Message}"));
        }
    }

    private void ApplyTimingSettings(AppSettings settings)
    {
        fastFadeSeconds = settings.FastFadeSeconds;
        mediumFadeSeconds = settings.MediumFadeSeconds;
        slowFadeSeconds = settings.SlowFadeSeconds;
        crossfadeStaggerSeconds = settings.CrossfadeStaggerSeconds;
        OnPropertyChanged(nameof(ConfiguredFastFadeDuration));
        OnPropertyChanged(nameof(ConfiguredMediumFadeDuration));
        OnPropertyChanged(nameof(ConfiguredSlowFadeDuration));
        OnPropertyChanged(nameof(ConfiguredCrossfadeStaggerDuration));
        OnPropertyChanged(nameof(FadeSpeedLabel));
        OnPropertyChanged(nameof(FadeSpeedDisplay));
        OnPropertyChanged(nameof(MasterFadeSpeedLabel));
    }

    private async Task ApplyInitialPlaybackSettingsAsync()
    {
        TimeSpan initialMediumFadeDuration = ConfiguredMediumFadeDuration;
        TimeSpan initialCrossfadeStaggerDuration = ConfiguredCrossfadeStaggerDuration;
        float initialMusicVolume = MusicVolume;
        float initialAmbienceVolume = AmbienceVolume;
        float initialMasterVolume = MasterVolume;
        try
        {
            Task applyTask = playbackDispatcher.Enqueue(async () =>
            {
                await playbackController.ConfigureTimingAsync(
                    initialMediumFadeDuration,
                    initialCrossfadeStaggerDuration).ConfigureAwait(false);
                await playbackController.SetMusicVolumeAsync(initialMusicVolume).ConfigureAwait(false);
                await playbackController.SetAmbienceVolumeAsync(initialAmbienceVolume).ConfigureAwait(false);
                await playbackController.SetMasterVolumeAsync(initialMasterVolume).ConfigureAwait(false);
                PlaybackSnapshot snapshot = playbackController.Snapshot;
                await RunOnUiContextAsync(() => ApplyPlaybackSnapshot(snapshot)).ConfigureAwait(false);
            });
            await applyTask.ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            await SetPlaybackErrorAsync("The initial playback levels could not be applied.", exception)
                .ConfigureAwait(false);
        }
    }

    private async Task SetVolumeAsync(VolumeKind kind, float volume)
    {
        ValidateVolume(volume, nameof(volume));
        if (IsDisposed)
        {
            return;
        }

        await RunOnUiContextAsync(() => SetVolumeProperty(kind, volume)).ConfigureAwait(false);

        Task workerTask;
        lock (playbackOperationLock)
        {
            if (IsDisposed)
            {
                return;
            }

            lock (volumeRequestLock)
            {
                pendingVolumeRequests[kind] = volume;
                if (!volumeWorkerScheduled)
                {
                    volumeWorkerScheduled = true;
                    volumeWorkerTask = playbackDispatcher.Enqueue(ExecuteVolumeRequestsAsync);
                }

                workerTask = volumeWorkerTask!;
            }
        }

        await workerTask.ConfigureAwait(false);
    }

    private async Task ExecuteVolumeRequestsAsync()
    {
        // Slider notifications commonly arrive in a burst. Keep one bounded
        // dispatcher item and give that burst a chance to collapse to its last
        // value before touching the engine or settings file.
        await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);

        while (true)
        {
            Dictionary<VolumeKind, float> requests;
            lock (volumeRequestLock)
            {
                if (pendingVolumeRequests.Count == 0)
                {
                    volumeWorkerScheduled = false;
                    volumeWorkerTask = null;
                    return;
                }

                requests = new Dictionary<VolumeKind, float>(pendingVolumeRequests);
                pendingVolumeRequests.Clear();
            }

            try
            {
                if (requests.TryGetValue(VolumeKind.Music, out float music))
                {
                    await playbackController.SetMusicVolumeAsync(music).ConfigureAwait(false);
                }

                if (requests.TryGetValue(VolumeKind.Ambience, out float ambience))
                {
                    await playbackController.SetAmbienceVolumeAsync(ambience).ConfigureAwait(false);
                }

                if (requests.TryGetValue(VolumeKind.Master, out float master))
                {
                    await playbackController.SetMasterVolumeAsync(master).ConfigureAwait(false);
                }

                PlaybackSnapshot snapshot = playbackController.Snapshot;
                AppSettings? settings = null;
                await RunOnUiContextAsync(() =>
                {
                    ApplyPlaybackSnapshot(snapshot);
                    settings = CreateSettings(SelectedLibraryPath, PreferredImmediateTransitionMode);
                }).ConfigureAwait(false);

                try
                {
                    await SaveSettingsAsync(settings!).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    await RunOnUiContextAsync(() => StatusText = JoinMessages(
                        StatusText,
                        $"The volume levels could not be saved to settings: {exception.Message}"))
                        .ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException) when (IsDisposed)
            {
                return;
            }
            catch (Exception exception)
            {
                await SetPlaybackErrorAsync("The volume command failed.", exception).ConfigureAwait(false);
            }

            // Also collapse values which arrived while the controller or the
            // atomic settings save was busy.
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
    }

    private void SetVolumeProperty(VolumeKind kind, float volume)
    {
        switch (kind)
        {
            case VolumeKind.Music:
                MusicVolume = volume;
                break;
            case VolumeKind.Ambience:
                AmbienceVolume = volume;
                break;
            case VolumeKind.Master:
                MasterVolume = volume;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private Task RunFadeAsync(
        PlaybackOperation operation,
        MasterFadeDirection direction,
        TimeSpan duration) =>
        RunPlaybackOperationAsync(
            operation,
             () => playbackController.FadeMasterAsync(direction, duration));

    private Task RunStopAllWithDuration(TimeSpan duration) => RunPlaybackOperationAsync(
        PlaybackOperation.StopAll,
        () => playbackController.StopAllAsync(duration));

    private Task RunStateAwareFadeAsync(
        PlaybackOperation operation,
        Func<PlaybackSnapshot, MasterFadeState> stateSelector,
        TimeSpan duration,
        Func<MasterFadeDirection, Task> action)
    {
        return RunPlaybackOperationAsync(operation, async () =>
        {
            // Resolve the direction inside the FIFO operation. Reading the
            // ViewModel state here would race with UI progress notifications,
            // while the controller snapshot is synchronized with the queued
            // operation that ran immediately before this one.
            MasterFadeState state = stateSelector(playbackController.Snapshot);
            MasterFadeDirection direction = state is MasterFadeState.Full or MasterFadeState.FadingIn
                ? MasterFadeDirection.Out
                : MasterFadeDirection.In;
            await action(direction).ConfigureAwait(false);
        });
    }

    private Task ResolvePauseResumeAsync()
    {
        return playbackController.State switch
        {
            PlaybackState.Playing => playbackController.PauseAsync(),
            PlaybackState.Paused => playbackController.ResumeAsync(),
            _ => Task.CompletedTask,
        };
    }

    private Task RunPlaybackOperationAsync(PlaybackOperation operation, Func<Task> action)
        => RunPlaybackOperationAsync(operation, null, action);

    private Task RunPlaybackOperationAsync(
        PlaybackOperation operation,
        string? identity,
        Func<Task> action)
    {
        var key = new PlaybackOperationKey(operation, identity);
        Task commandTask;
        lock (playbackOperationLock)
        {
            if (IsDisposed)
            {
                return Task.CompletedTask;
            }

            playbackOperationCounts[key] = playbackOperationCounts.TryGetValue(key, out int count)
                ? count + 1
                : 1;
            try
            {
                commandTask = playbackDispatcher.Enqueue(() => ExecutePlaybackOperationAsync(key, action));
            }
            catch
            {
                DecrementPlaybackOperationCountNoLock(key);
                throw;
            }
        }

        // A queued command is already busy from the UI's perspective. Publish
        // that fact immediately rather than waiting for the FIFO reader to
        // start the work item.
        PostToUiContext(NotifyPlaybackCommandAvailability);
        return commandTask;
    }

    private async Task ExecutePlaybackOperationAsync(PlaybackOperationKey operation, Func<Task> action)
    {
        try
        {
            await RunOnUiContextAsync(NotifyPlaybackCommandAvailability).ConfigureAwait(false);
            await action().ConfigureAwait(false);
            PlaybackSnapshot snapshot = playbackController.Snapshot;
            await RunOnUiContextAsync(() => ApplyPlaybackSnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
        }
        catch (Exception exception)
        {
            await SetPlaybackErrorAsync("The playback command failed.", exception).ConfigureAwait(false);
        }
        finally
        {
            lock (playbackOperationLock)
            {
                DecrementPlaybackOperationCountNoLock(operation);
            }

            await RunOnUiContextAsync(NotifyPlaybackCommandAvailability).ConfigureAwait(false);
        }
    }

    private bool IsPlaybackOperationInFlight(PlaybackOperation operation)
        => IsPlaybackOperationInFlight(operation, null);

    private bool IsPlaybackOperationInFlight(PlaybackOperation operation, string? identity)
    {
        var key = new PlaybackOperationKey(operation, identity);
        lock (playbackOperationLock)
        {
            return playbackOperationCounts.TryGetValue(key, out int count) && count > 0;
        }
    }

    private void DecrementPlaybackOperationCountNoLock(PlaybackOperationKey key)
    {
        if (!playbackOperationCounts.TryGetValue(key, out int count))
        {
            return;
        }

        if (count <= 1)
        {
            playbackOperationCounts.Remove(key);
        }
        else
        {
            playbackOperationCounts[key] = count - 1;
        }
    }

    private void PlaybackController_StateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        PostToUiContext(() =>
        {
            if (!IsDisposed)
            {
                ApplyPlaybackSnapshot(playbackController.Snapshot);
            }
        });
    }

    private void PlaybackController_ErrorOccurred(object? sender, PlaybackErrorEventArgs e)
    {
        if (IsDisposed ||
            !string.Equals(e.Message, playbackController.LastError, StringComparison.Ordinal))
        {
            return;
        }

        long notificationVersion = Interlocked.Increment(ref playbackErrorNotificationVersion);
        string error = FormatPlaybackError(e.Message, e.Exception);
        PostToUiContext(() =>
        {
            if (!IsDisposed &&
                notificationVersion == Volatile.Read(ref playbackErrorNotificationVersion) &&
                string.Equals(e.Message, playbackController.LastError, StringComparison.Ordinal))
            {
                LatestPlaybackError = error;
            }
        });
    }

    private void ApplyPlaybackSnapshot(PlaybackSnapshot snapshot)
    {
        bool playbackChanged = currentPlaybackId != snapshot.CurrentPlaybackId;
        currentPlaybackId = snapshot.CurrentPlaybackId;

        ActivePlaylist = snapshot.ActivePlaylist;
        PendingPlaylist = snapshot.PendingPlaylist;
        CurrentTrack = snapshot.CurrentTrack;
        CurrentPlaylist = snapshot.CurrentPlaylist;
        ExplicitQueue = snapshot.Queue;
        PlaybackState = snapshot.State;
        MasterGain = snapshot.MasterGain;
        MasterFadeState = snapshot.MasterFadeState;
        MusicVolume = snapshot.MusicVolume;
        AmbienceVolume = snapshot.AmbienceVolume;
        MasterVolume = snapshot.MasterVolume;
        MusicFadeGain = snapshot.MusicFadeGain;
        MusicFadeState = snapshot.MusicFadeState;
        AmbienceFadeGain = snapshot.AmbienceFadeGain;
        AmbienceFadeState = snapshot.AmbienceFadeState;
        ApplyLogicalAmbienceSnapshots(snapshot.AmbienceSnapshots);

        if (playbackChanged || snapshot.CurrentPlaybackId is null)
        {
            Elapsed = TimeSpan.Zero;
        }

        Duration = snapshot.CurrentDuration ?? TimeSpan.Zero;
        if (LatestPlaybackError is null && !string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            LatestPlaybackError = snapshot.LastError;
        }
    }

    private Task SetPlaybackErrorAsync(string message, Exception exception) =>
        RunOnUiContextAsync(() => LatestPlaybackError = FormatPlaybackError(message, exception));

    private void NotifyPlaybackCommandAvailability()
    {
        OnPropertyChanged(nameof(CanPlaySelectedPlaylist));
        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(CanPlayAfterCurrent));
        OnPropertyChanged(nameof(CanQueueTrack));
        OnPropertyChanged(nameof(CanPlayTrackNow));
        OnPropertyChanged(nameof(CanPauseResume));
        OnPropertyChanged(nameof(CanSkip));
        OnPropertyChanged(nameof(CanClearQueue));
        OnPropertyChanged(nameof(CanStopAll));
        OnPropertyChanged(nameof(CanApplyAmbiencePreset));
        NotifyFadeCommandAvailability();
    }

    private void ApplyLogicalAmbienceSnapshots(IReadOnlyList<AmbiencePlaybackSnapshot> snapshots) =>
        ApplyAmbienceSnapshots(
            snapshots,
            static (track, snapshot) => track.ApplyLogicalSnapshot(snapshot));

    private void ApplyRenderedAmbienceProgress(IReadOnlyList<AmbiencePlaybackSnapshot> snapshots) =>
        ApplyAmbienceSnapshots(
            snapshots,
            static (track, snapshot) => track.ApplyRenderedProgress(snapshot));

    private void ApplyAmbienceSnapshots(
        IReadOnlyList<AmbiencePlaybackSnapshot> snapshots,
        Action<AmbienceTrackViewModel, AmbiencePlaybackSnapshot> applySnapshot)
    {
        hasPhysicalAmbience = snapshots.Count > 0;
        var snapshotsByPath = snapshots.ToDictionary(
            snapshot => GetPathIdentity(snapshot.FilePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (AmbienceTrackViewModel track in AmbienceTracks)
        {
            if (snapshotsByPath.TryGetValue(GetPathIdentity(track.FilePath), out var snapshot))
            {
                applySnapshot(track, snapshot);
            }
            else
            {
                track.MarkStopped();
            }
        }

        OnPropertyChanged(nameof(CanStopAll));
        NotifyFadeCommandAvailability();
    }

    private void ApplyFadeProgress(AudioProgressSnapshot progress)
    {
        MasterGain = progress.MasterGain;
        MasterFadeState = progress.MasterFadeState;
        MusicVolume = progress.MusicVolume;
        AmbienceVolume = progress.AmbienceVolume;
        MasterVolume = progress.MasterVolume;
        MusicFadeGain = progress.MusicFadeGain;
        MusicFadeState = progress.MusicFadeState;
        AmbienceFadeGain = progress.AmbienceFadeGain;
        AmbienceFadeState = progress.AmbienceFadeState;
        ApplyRenderedAmbienceProgress(progress.AmbienceSnapshots);
    }

    private void NotifyFadeCommandAvailability()
    {
        OnPropertyChanged(nameof(CanQuickFadeIn));
        OnPropertyChanged(nameof(CanQuickFadeOut));
        OnPropertyChanged(nameof(CanSlowFadeIn));
        OnPropertyChanged(nameof(CanSlowFadeOut));
        OnPropertyChanged(nameof(CanFadeMusic));
        OnPropertyChanged(nameof(CanFadeAmbience));
        OnPropertyChanged(nameof(CanFadeMaster));
        OnPropertyChanged(nameof(CanToggleMusicFade));
        OnPropertyChanged(nameof(CanToggleAmbienceFade));
        OnPropertyChanged(nameof(CanToggleMasterFade));
    }

    private Task SaveSettingsAsync(
        string? libraryPath,
        ImmediateTransitionMode transitionMode) =>
        SaveSettingsAsync(CreateSettings(libraryPath, transitionMode));

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        await settingsSaveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AppSettings settingsToSave = settings;
            await RunOnUiContextAsync(() => settingsToSave = new AppSettings
            {
                Version = AppSettings.CurrentVersion,
                SelectedLibraryPath = settings.SelectedLibraryPath,
                PreferredImmediateTransitionMode = settings.PreferredImmediateTransitionMode,
                // A path/mode save may have been queued before a slider
                // notification reached the atomic save gate. Merge the latest
                // levels at write time so that no unrelated setting can roll
                // them back.
                MusicVolume = MusicVolume,
                AmbienceVolume = AmbienceVolume,
                MasterVolume = MasterVolume,
                FastFadeSeconds = fastFadeSeconds,
                MediumFadeSeconds = mediumFadeSeconds,
                SlowFadeSeconds = slowFadeSeconds,
                CrossfadeStaggerSeconds = crossfadeStaggerSeconds,
                AmbiencePresets = ambiencePresets,
            }).ConfigureAwait(false);
            await Task.Run(() => settingsService.Save(settingsToSave)).ConfigureAwait(false);
        }
        finally
        {
            settingsSaveGate.Release();
        }
    }

    private AppSettings CreateSettings(
        string? libraryPath,
        ImmediateTransitionMode transitionMode) => new()
    {
        Version = AppSettings.CurrentVersion,
        SelectedLibraryPath = libraryPath,
        PreferredImmediateTransitionMode = transitionMode,
        MusicVolume = MusicVolume,
        AmbienceVolume = AmbienceVolume,
        MasterVolume = MasterVolume,
        FastFadeSeconds = fastFadeSeconds,
        MediumFadeSeconds = mediumFadeSeconds,
        SlowFadeSeconds = slowFadeSeconds,
        CrossfadeStaggerSeconds = crossfadeStaggerSeconds,
        AmbiencePresets = ambiencePresets,
    };

    private Task RunOnUiContextAsync(Action action)
    {
        if (uiContext is null ||
            Environment.CurrentManagedThreadId == uiThreadId ||
            ReferenceEquals(SynchronizationContext.Current, uiContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(
            static state =>
            {
                var work = ((Action Action, TaskCompletionSource Completion))state!;
                try
                {
                    work.Action();
                    work.Completion.SetResult();
                }
                catch (Exception exception)
                {
                    work.Completion.SetException(exception);
                }
            },
            (action, completion));
        return completion.Task;
    }

    private void PostToUiContext(Action action)
    {
        if (uiContext is null ||
            Environment.CurrentManagedThreadId == uiThreadId ||
            ReferenceEquals(SynchronizationContext.Current, uiContext))
        {
            action();
            return;
        }

        uiContext.Post(static state => ((Action)state!)(), action);
    }

    private static string FormatPlaybackError(string message, Exception? exception)
    {
        if (exception is null || message.Contains(exception.Message, StringComparison.OrdinalIgnoreCase))
        {
            return message;
        }

        return $"{message} {exception.GetType().Name}: {exception.Message}";
    }

    private static string FormatTime(TimeSpan value)
    {
        value = Max(value, TimeSpan.Zero);
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first >= second ? first : second;

    private static string FormatPercent(float value) =>
        $"{Math.Clamp((int)Math.Round(NormalizeVolume(value, 0f) * 100), 0, 100)}%";

    private static string FormatFadeState(MasterFadeState state) => state switch
    {
        MasterFadeState.FadingOut => "Fading Out",
        MasterFadeState.Muted => "Muted",
        MasterFadeState.FadingIn => "Fading In",
        _ => "Full",
    };

    private static string GetFadeDirectionLabel(MasterFadeState state) =>
        state is MasterFadeState.Full or MasterFadeState.FadingIn
            ? "Fade Out"
            : "Fade In";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds == Math.Truncate(duration.TotalSeconds)
            ? $"{duration.TotalSeconds:0}s"
            : $"{duration.TotalSeconds:0.##}s";

    private static float NormalizeVolume(float value, float fallback)
    {
        if (!float.IsFinite(value))
        {
            return fallback;
        }

        return Math.Clamp(value, 0f, 1f);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Volume must be finite and between zero and one.");
        }
    }

    private static string GetPathIdentity(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    private static string DescribeCatalog(LibraryCatalog catalog)
    {
        int playlistCount = CountPlaylists(catalog.Groups) + catalog.Playlists.Count;
        string summary = $"Library loaded: {playlistCount} playlist(s), {catalog.AmbienceTracks.Count} ambience track(s).";

        if (catalog.Issues.Count == 0)
        {
            return summary;
        }

        string issueMessages = string.Join(" ", catalog.Issues.Select(issue => issue.Message));
        return $"{summary} Scan completed with {catalog.Issues.Count} issue(s): {issueMessages}";
    }

    private static int CountPlaylists(IEnumerable<LibraryGroup> groups) =>
        groups.Sum(group => group.Playlists.Count + CountPlaylists(group.Groups));

    private static string JoinMessages(params string?[] messages) =>
        string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)));

    private enum PlaybackOperation
    {
        PlayNow,
        PlayNowTrack,
        AfterCurrent,
        QueueTrack,
        PauseResume,
        Skip,
        ClearQueue,
        StopAll,
        PlayAmbience,
        StopAmbience,
        SetAmbienceGain,
        QuickFadeIn,
        QuickFadeOut,
        SlowFadeIn,
        SlowFadeOut,
        FadeMaster,
        FadeMusic,
        FadeAmbience,
        SaveAmbiencePreset,
        ApplyAmbiencePreset,
        DeleteAmbiencePreset,
    }

    private enum VolumeKind
    {
        Music,
        Ambience,
        Master,
    }

    private readonly record struct PlaybackOperationKey(
        PlaybackOperation Operation,
        string? Identity);
}

internal sealed class AsyncFifoDispatcher
{
    private readonly Channel<WorkItem> workItems = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly Task processorTask;

    public AsyncFifoDispatcher()
    {
        processorTask = Task.Run(ProcessAsync);
    }

    public Task Completion => processorTask;

    public Task Enqueue(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!workItems.Writer.TryWrite(new WorkItem(action, completion)))
        {
            throw new InvalidOperationException("The playback dispatcher is no longer accepting work.");
        }

        return completion.Task;
    }

    public void Complete() => workItems.Writer.TryComplete();

    private async Task ProcessAsync()
    {
        await foreach (WorkItem workItem in workItems.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await workItem.Action().ConfigureAwait(false);
                workItem.Completion.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                workItem.Completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                workItem.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record WorkItem(Func<Task> Action, TaskCompletionSource Completion);
}
