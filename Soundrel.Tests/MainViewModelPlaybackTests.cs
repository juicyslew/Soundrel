using System.Collections.Concurrent;
using Soundrel.Models;
using Soundrel.Services;
using Soundrel.ViewModels;

namespace Soundrel.Tests;

[TestClass]
public sealed class MainViewModelPlaybackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private TemporaryPlaybackWorkspace workspace = null!;
    private FakeAudioEngine engine = null!;
    private PlaybackController controller = null!;
    private MainViewModel viewModel = null!;

    [TestInitialize]
    public void Initialize()
    {
        workspace = new TemporaryPlaybackWorkspace();
        engine = new FakeAudioEngine();
        controller = new PlaybackController(engine, new ZeroRandom());
        viewModel = new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            controller);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await viewModel.DisposeAsync();
        workspace.Dispose();
    }

    [TestMethod]
    public async Task SelectedPlaylistPlayNowDisplaysControllerState()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Battle", "charge.mp3");

        viewModel.SelectNode(playlistNode);
        Assert.AreSame(playlistNode.Playlist, viewModel.SelectedPlaylist);
        Assert.IsTrue(viewModel.CanPlaySelectedPlaylist);
        Assert.IsTrue(viewModel.CanPlayNow);
        await viewModel.QueueTrackAsync(playlistNode.Playlist!.Tracks[0]);

        await viewModel.PlayNowAsync();

        Assert.AreSame(playlistNode.Playlist, viewModel.ActivePlaylist);
        Assert.AreSame(playlistNode.Playlist, viewModel.CurrentPlaylist);
        Assert.AreSame(playlistNode.Playlist!.Tracks[0], viewModel.CurrentTrack);
        Assert.AreEqual("charge", viewModel.CurrentTrackDisplayName);
        Assert.AreEqual("charge.mp3", viewModel.CurrentFileName);
        Assert.AreEqual(PlaybackState.Playing, viewModel.PlaybackState);
        Assert.HasCount(1, viewModel.ExplicitQueue);
        Assert.AreEqual("Playing", viewModel.PlaybackStateDisplay);
        Assert.IsTrue(viewModel.CanPauseResume);
        Assert.IsTrue(viewModel.CanSkip);
        Assert.IsTrue(viewModel.CanStopAll);
    }

    [TestMethod]
    public async Task AmbienceRowAllowsStopToPlayFadeReversal()
    {
        string libraryPath = workspace.CreateLibrary();
        workspace.CreateTrack("Ambience", "rain.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);
        AmbienceTrackViewModel row = viewModel.AmbienceTracks.Single();

        Assert.IsTrue(row.CanPlay);
        Assert.IsFalse(row.CanStop);

        await viewModel.PlayAmbienceAsync(row);
        Assert.AreEqual(AmbiencePlaybackState.FadingIn, row.PlaybackState);
        Assert.IsFalse(row.CanPlay);
        Assert.IsTrue(row.CanStop);

        await viewModel.StopAmbienceAsync(row);
        Assert.AreEqual(AmbiencePlaybackState.FadingOut, row.PlaybackState);
        Assert.IsTrue(row.CanPlay);
        Assert.IsFalse(row.CanStop);
    }

    [TestMethod]
    public async Task RapidAmbienceStopThenPlayPreservesFifoFadeReversal()
    {
        string libraryPath = workspace.CreateLibrary();
        workspace.CreateTrack("Ambience", "rain.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);
        AmbienceTrackViewModel row = viewModel.AmbienceTracks.Single();
        await viewModel.PlayAmbienceAsync(row);

        Task stop = viewModel.StopAmbienceAsync(row);
        Task play = viewModel.PlayAmbienceAsync(row);
        await Task.WhenAll(stop, play).WaitAsync(TestTimeout);

        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(row.FilePath) },
            engine.AmbienceStopRequests.ToArray());
        Assert.HasCount(2, engine.AmbiencePlayRequests);
        Assert.AreEqual(AmbiencePlaybackState.FadingIn, row.PlaybackState);
        Assert.IsFalse(row.CanPlay);
        Assert.IsTrue(row.CanStop);
    }

    [TestMethod]
    public async Task RepeatedAmbienceGainCommandsRunFifoAndKeepTheLastValue()
    {
        string libraryPath = workspace.CreateLibrary();
        workspace.CreateTrack("Ambience", "rain.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);
        AmbienceTrackViewModel row = viewModel.AmbienceTracks.Single();
        await viewModel.PlayAmbienceAsync(row);

        var fadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFade = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FadeHandler = async (_, _) =>
        {
            fadeEntered.TrySetResult();
            await releaseFade.Task.ConfigureAwait(false);
        };

        Task fade = viewModel.QuickFadeOutAsync();
        await fadeEntered.Task.WaitAsync(TestTimeout);
        Task firstGain = viewModel.SetAmbienceSourceGainAsync(row, 0.2f);
        Task lastGain = viewModel.SetAmbienceSourceGainAsync(row, 0.8f);

        releaseFade.TrySetResult();
        await Task.WhenAll(fade, firstGain, lastGain).WaitAsync(TestTimeout);

        CollectionAssert.AreEqual(
            new[] { 0.2f, 0.8f },
            engine.AmbienceGainRequests.Select(request => request.SourceGain).ToArray());
        Assert.AreEqual(0.8f, controller.Snapshot.AmbienceSnapshots.Single().SourceGain);
        Assert.AreEqual(0.8f, row.Gain);
    }

    [TestMethod]
    public async Task RapidMusicVolumeRequestsApplyAndPersistTheFinalValue()
    {
        string libraryPath = workspace.CreateLibrary();
        await viewModel.SelectLibraryAsync(libraryPath);

        Task[] requests =
        [
            viewModel.SetMusicVolumeAsync(0.1f),
            viewModel.SetMusicVolumeAsync(0.4f),
            viewModel.SetMusicVolumeAsync(0.9f),
        ];
        await Task.WhenAll(requests).WaitAsync(TestTimeout);

        Assert.AreEqual(0.9f, engine.MusicVolume);
        Assert.AreEqual(0.9f, viewModel.MusicVolume);
        Assert.AreEqual(0.9f, workspace.CreateSettingsService().Load().Settings.MusicVolume);
    }

    [TestMethod]
    public async Task RejectedVolumeApplicationSurfacesErrorAndDoesNotPersistRejectedValue()
    {
        string libraryPath = workspace.CreateLibrary();
        await viewModel.SelectLibraryAsync(libraryPath);
        engine.MusicVolumeException = new IOException("volume device failure");

        await viewModel.SetMusicVolumeAsync(0.25f);

        Assert.IsTrue(viewModel.HasPlaybackError);
        StringAssert.Contains(viewModel.LatestPlaybackError!, "Could not set music volume");
        Assert.AreEqual(1f, viewModel.MusicVolume);
        Assert.AreEqual(1f, workspace.CreateSettingsService().Load().Settings.MusicVolume);
    }

    [TestMethod]
    public async Task RapidSameOperationCommandsAreQueuedInsteadOfDropped()
    {
        var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _) =>
        {
            playEntered.TrySetResult();
            await releasePlay.Task.ConfigureAwait(false);
            return new AudioPlaybackInfo(engine.Duration);
        };

        IReadOnlyList<LibraryTreeNode> nodes = await LoadPlaylistsAsync(
            ("First", new[] { "first.mp3" }),
            ("Second", new[] { "second.mp3" }));
        LibraryTreeNode first = nodes.Single(node => node.Name == "First");
        LibraryTreeNode second = nodes.Single(node => node.Name == "Second");
        viewModel.SelectNode(first);
        Task firstPlay = viewModel.PlayNowAsync();
        await playEntered.Task.WaitAsync(TestTimeout);

        viewModel.SelectNode(second);
        Task secondPlay = viewModel.PlayNowAsync();
        Assert.IsFalse(viewModel.CanPlayNow);

        releasePlay.TrySetResult();
        await Task.WhenAll(firstPlay, secondPlay).WaitAsync(TestTimeout);

        Assert.HasCount(2, engine.PlayRequests);
        Assert.AreSame(second.Playlist, viewModel.CurrentPlaylist);
        Assert.AreSame(second.Playlist!.Tracks[0], viewModel.CurrentTrack);
    }

    [TestMethod]
    public async Task StopAllRemainsAvailableWhenRescanHidesPhysicalAmbience()
    {
        string libraryPath = workspace.CreateLibrary();
        string ambiencePath = workspace.CreateTrack("Ambience", "rain.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);
        AmbienceTrackViewModel row = viewModel.AmbienceTracks.Single();
        await viewModel.PlayAmbienceAsync(row);

        File.Delete(ambiencePath);
        await viewModel.RescanAsync();

        Assert.IsEmpty(viewModel.AmbienceTracks);
        Assert.IsTrue(viewModel.CanStopAll);

        await viewModel.StopAllAsync();

        Assert.IsFalse(viewModel.CanStopAll);
        Assert.IsEmpty(controller.Snapshot.AmbienceSnapshots);
    }

    [TestMethod]
    public async Task AfterCurrentDisplaysPendingPlaylistWithoutInterruptingCurrentTrack()
    {
        IReadOnlyList<LibraryTreeNode> nodes = await LoadPlaylistsAsync(
            ("Active", new[] { "active.mp3" }),
            ("Next", new[] { "next.mp3" }));
        LibraryTreeNode active = nodes.Single(node => node.Name == "Active");
        LibraryTreeNode next = nodes.Single(node => node.Name == "Next");
        viewModel.SelectNode(active);
        await viewModel.PlayNowAsync();
        LibraryTrack? currentTrack = viewModel.CurrentTrack;

        viewModel.SelectNode(next);
        await viewModel.AfterCurrentAsync();

        Assert.AreSame(active.Playlist, viewModel.ActivePlaylist);
        Assert.AreSame(next.Playlist, viewModel.PendingPlaylist);
        Assert.AreEqual("Next", viewModel.PendingPlaylistDisplay);
        Assert.AreSame(currentTrack, viewModel.CurrentTrack);
        Assert.HasCount(1, engine.PlayRequests);
        Assert.AreEqual(0, engine.StopCount);
    }

    [TestMethod]
    public async Task QueueTrackPreservesSourcePlaylistAndDisplaysFifoOrder()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync(
            "Scenes",
            "first.mp3",
            "second.wav");
        viewModel.SelectNode(playlistNode);

        await viewModel.QueueTrackAsync(playlistNode.Playlist!.Tracks[0]);
        await viewModel.QueueTrackAsync(playlistNode.Playlist.Tracks[1]);

        Assert.HasCount(2, viewModel.ExplicitQueue);
        CollectionAssert.AreEqual(
            new[] { "first", "second" },
            viewModel.ExplicitQueue.Select(entry => entry.Track.Name).ToArray());
        Assert.IsTrue(viewModel.ExplicitQueue.All(
            entry => ReferenceEquals(playlistNode.Playlist, entry.SourcePlaylist)));
        Assert.IsTrue(viewModel.HasExplicitQueue);
        Assert.IsTrue(viewModel.CanClearQueue);
    }

    [TestMethod]
    public async Task PauseResumeUpdatesLabelStateAndEngineCommands()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Travel", "road.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        Assert.AreEqual("Pause", viewModel.PauseResumeLabel);
        await viewModel.PauseResumeAsync();

        Assert.AreEqual(PlaybackState.Paused, viewModel.PlaybackState);
        Assert.AreEqual("Resume", viewModel.PauseResumeLabel);
        Assert.AreEqual(1, engine.PauseCount);

        await viewModel.PauseResumeAsync();

        Assert.AreEqual(PlaybackState.Playing, viewModel.PlaybackState);
        Assert.AreEqual("Pause", viewModel.PauseResumeLabel);
        Assert.AreEqual(1, engine.ResumeCount);
    }

    [TestMethod]
    public async Task SkipStopAndClearQueueHaveCorrectEnablementAndBehavior()
    {
        IReadOnlyList<LibraryTreeNode> nodes = await LoadPlaylistsAsync(
            ("Active", new[] { "one.mp3", "two.mp3" }),
            ("Queued", new[] { "queued.mp3" }));
        LibraryTreeNode active = nodes.Single(node => node.Name == "Active");
        LibraryTreeNode queued = nodes.Single(node => node.Name == "Queued");
        viewModel.SelectNode(active);
        await viewModel.PlayNowAsync();
        LibraryTrack firstTrack = viewModel.CurrentTrack!;
        viewModel.SelectNode(queued);
        await viewModel.QueueTrackAsync(queued.Playlist!.Tracks[0]);

        Assert.IsTrue(viewModel.CanSkip);
        Assert.IsTrue(viewModel.CanStopAll);
        Assert.IsTrue(viewModel.CanClearQueue);

        await viewModel.ClearQueueAsync();
        Assert.IsEmpty(viewModel.ExplicitQueue);
        Assert.IsFalse(viewModel.CanClearQueue);
        Assert.AreSame(firstTrack, viewModel.CurrentTrack);

        await viewModel.QueueTrackAsync(queued.Playlist.Tracks[0]);
        await viewModel.SkipAsync();
        Assert.AreSame(queued.Playlist.Tracks[0], viewModel.CurrentTrack);
        Assert.IsEmpty(viewModel.ExplicitQueue);
        Assert.AreEqual(1, engine.StopCount);

        await viewModel.QueueTrackAsync(queued.Playlist.Tracks[0]);
        await viewModel.StopAllAsync();
        Assert.AreEqual(PlaybackState.Stopped, viewModel.PlaybackState);
        Assert.IsNull(viewModel.CurrentTrack);
        Assert.IsNull(viewModel.CurrentPlaylist);
        Assert.IsFalse(viewModel.CanSkip);
        Assert.IsFalse(viewModel.CanStopAll);
        Assert.IsTrue(viewModel.CanClearQueue);

        await viewModel.ClearQueueAsync();
        Assert.IsFalse(viewModel.CanClearQueue);
    }

    [TestMethod]
    public async Task UpdatePlaybackProgressFormatsElapsedAndDuration()
    {
        engine.Duration = TimeSpan.FromSeconds(90);
        engine.Position = TimeSpan.FromSeconds(12);
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Clock", "tick.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        await viewModel.UpdatePlaybackProgressAsync();

        Assert.AreEqual(TimeSpan.FromSeconds(12), viewModel.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(90), viewModel.Duration);
        Assert.AreEqual("0:12", viewModel.ElapsedDisplay);
        Assert.AreEqual("1:30", viewModel.DurationDisplay);
        Assert.AreEqual("0:12 / 1:30", viewModel.ProgressDisplay);
    }

    [TestMethod]
    public async Task PlayNowUsesTransitionModeCapturedAtInvocation()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Captured", "captured.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.Crossfade);
        var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _) =>
        {
            playEntered.TrySetResult();
            await releasePlay.Task.ConfigureAwait(false);
            return new AudioPlaybackInfo(engine.Duration);
        };

        Task playTask = viewModel.PlayNowAsync();
        try
        {
            await playEntered.Task.WaitAsync(TestTimeout);
            await viewModel.SetImmediateTransitionModeAsync(ImmediateTransitionMode.HardCut);

            Assert.HasCount(1, engine.PlayRequests);
            Assert.AreEqual(
                ImmediateTransitionMode.Crossfade,
                engine.PlayRequests[0].TransitionMode);

            releasePlay.TrySetResult();
            await playTask.WaitAsync(TestTimeout);
            Assert.AreEqual(
                ImmediateTransitionMode.HardCut,
                viewModel.PreferredImmediateTransitionMode);
        }
        finally
        {
            releasePlay.TrySetResult();
            await playTask.WaitAsync(TestTimeout);
        }
    }

    [TestMethod]
    public async Task RapidFadeReversalsPreserveFifoOrderAndFinalState()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Fades", "fade.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();
        engine.FadeHandler = (direction, _) =>
        {
            engine.MasterGain = direction == MasterFadeDirection.In ? 1f : 0f;
            return Task.CompletedTask;
        };

        Task fadeOut = viewModel.QuickFadeOutAsync();
        Task reverseIn = viewModel.QuickFadeInAsync();
        Task finalFadeOut = viewModel.SlowFadeOutAsync();
        await Task.WhenAll(fadeOut, reverseIn, finalFadeOut).WaitAsync(TestTimeout);
        await viewModel.UpdatePlaybackProgressAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                new FadeRequest(MasterFadeDirection.Out, MainViewModel.QuickFadeDuration),
                new FadeRequest(MasterFadeDirection.In, MainViewModel.QuickFadeDuration),
                new FadeRequest(MasterFadeDirection.Out, MainViewModel.SlowFadeDuration),
            },
            engine.FadeRequests.ToArray());
        Assert.AreEqual(0f, viewModel.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, viewModel.MasterFadeState);
        Assert.AreEqual("0%", viewModel.MasterGainDisplay);
        Assert.AreEqual("Muted", viewModel.MasterFadeStateDisplay);
    }

    [TestMethod]
    public async Task MasterDisplayUpdatesFromCommandSnapshotsAndProgressPolling()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Meter", "meter.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        Assert.AreEqual(TimeSpan.FromSeconds(2), MainViewModel.QuickFadeDuration);
        Assert.AreEqual(TimeSpan.FromSeconds(10), MainViewModel.SlowFadeDuration);
        Assert.AreEqual(1f, viewModel.MasterGain);
        Assert.AreEqual("100%", viewModel.MasterGainDisplay);
        Assert.AreEqual(MasterFadeState.Full, viewModel.MasterFadeState);
        Assert.AreEqual("Full", viewModel.MasterFadeStateDisplay);

        await viewModel.QuickFadeOutAsync();

        Assert.AreEqual(MasterFadeState.FadingOut, viewModel.MasterFadeState);
        Assert.AreEqual("Fading Out", viewModel.MasterFadeStateDisplay);

        engine.MasterGain = 0.42f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await viewModel.UpdatePlaybackProgressAsync();

        Assert.AreEqual(0.42f, viewModel.MasterGain);
        Assert.AreEqual("42%", viewModel.MasterGainDisplay);
        Assert.AreEqual(MasterFadeState.FadingOut, viewModel.MasterFadeState);
    }

    [TestMethod]
    public async Task FadeAvailabilityTracksPlaybackFadeOperationAndDisposalState()
    {
        AssertFadeAvailability(
            canQuickIn: false,
            canQuickOut: false,
            canSlowIn: false,
            canSlowOut: false);
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Controls", "controls.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        AssertFadeAvailability(
            canQuickIn: false,
            canQuickOut: true,
            canSlowIn: false,
            canSlowOut: true);

        await viewModel.PauseResumeAsync();
        Assert.AreEqual(PlaybackState.Paused, viewModel.PlaybackState);
        AssertFadeAvailability(
            canQuickIn: false,
            canQuickOut: true,
            canSlowIn: false,
            canSlowOut: true);

        engine.MasterGain = 0.5f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await viewModel.UpdatePlaybackProgressAsync();
        AssertFadeAvailability(
            canQuickIn: true,
            canQuickOut: true,
            canSlowIn: true,
            canSlowOut: true);

        var fadeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFade = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FadeHandler = async (_, _) =>
        {
            fadeEntered.TrySetResult();
            await releaseFade.Task.ConfigureAwait(false);
        };
        Task fadeTask = viewModel.QuickFadeOutAsync();
        try
        {
            await fadeEntered.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(viewModel.CanQuickFadeOut);
            Assert.IsTrue(viewModel.CanSlowFadeOut);
            Assert.IsTrue(viewModel.CanQuickFadeIn);
            Assert.IsTrue(viewModel.CanSlowFadeIn);
        }
        finally
        {
            releaseFade.TrySetResult();
            await fadeTask.WaitAsync(TestTimeout);
        }

        engine.MasterGain = 0.5f;
        engine.MasterFadeState = MasterFadeState.FadingIn;
        await viewModel.UpdatePlaybackProgressAsync();
        AssertFadeAvailability(
            canQuickIn: true,
            canQuickOut: true,
            canSlowIn: true,
            canSlowOut: true);

        engine.MasterGain = 0f;
        engine.MasterFadeState = MasterFadeState.Muted;
        await viewModel.UpdatePlaybackProgressAsync();
        AssertFadeAvailability(
            canQuickIn: true,
            canQuickOut: false,
            canSlowIn: true,
            canSlowOut: false);

        await viewModel.StopAllAsync();
        AssertFadeAvailability(
            canQuickIn: false,
            canQuickOut: false,
            canSlowIn: false,
            canSlowOut: false);

        await viewModel.PlayNowAsync();
        Assert.IsTrue(viewModel.CanQuickFadeOut);
        await viewModel.DisposeAsync();
        AssertFadeAvailability(
            canQuickIn: false,
            canQuickOut: false,
            canSlowIn: false,
            canSlowOut: false);
    }

    [TestMethod]
    public async Task FadeFailureIsVisibleAndPreservesCurrentMusic()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Stable", "stable.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();
        LibraryTrack currentTrack = viewModel.CurrentTrack!;
        engine.FadeException = new InvalidOperationException("fade device failure");

        await viewModel.QuickFadeOutAsync();

        Assert.AreSame(currentTrack, viewModel.CurrentTrack);
        Assert.AreEqual(PlaybackState.Playing, viewModel.PlaybackState);
        Assert.IsTrue(viewModel.HasPlaybackError);
        StringAssert.Contains(viewModel.LatestPlaybackError!, "Could not fade master playback level");
        StringAssert.Contains(viewModel.LatestPlaybackError!, "fade device failure");
    }

    [TestMethod]
    public async Task StopAllAndDisposalResetMasterDisplay()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Reset", "reset.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();
        engine.MasterGain = 0f;
        engine.MasterFadeState = MasterFadeState.Muted;
        await viewModel.UpdatePlaybackProgressAsync();

        await viewModel.StopAllAsync();

        Assert.AreEqual(1f, viewModel.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, viewModel.MasterFadeState);
        Assert.AreEqual("100%", viewModel.MasterGainDisplay);
        Assert.AreEqual("Full", viewModel.MasterFadeStateDisplay);

        await viewModel.PlayNowAsync();
        engine.MasterGain = 0f;
        engine.MasterFadeState = MasterFadeState.Muted;
        await viewModel.UpdatePlaybackProgressAsync();
        Assert.AreEqual(MasterFadeState.Muted, viewModel.MasterFadeState);

        await viewModel.DisposeAsync();

        Assert.AreEqual(1f, viewModel.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, viewModel.MasterFadeState);
        Assert.AreEqual("100%", viewModel.MasterGainDisplay);
        Assert.AreEqual("Full", viewModel.MasterFadeStateDisplay);
    }

    [TestMethod]
    public async Task MissingTrackPlaybackErrorIsVisibleWithUsefulDetail()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Broken", "missing.mp3");
        viewModel.SelectNode(playlistNode);
        LibraryTrack track = playlistNode.Playlist!.Tracks[0];
        File.Delete(track.FilePath);
        engine.UnreadablePaths.Add(track.FilePath);

        await viewModel.PlayNowAsync();

        Assert.IsTrue(viewModel.HasPlaybackError);
        StringAssert.Contains(viewModel.LatestPlaybackError!, "Could not play 'missing'");
        StringAssert.Contains(viewModel.LatestPlaybackError!, track.FilePath);
        StringAssert.Contains(viewModel.LatestPlaybackError!, "Cannot open");
        Assert.AreEqual(PlaybackState.Stopped, viewModel.PlaybackState);
        Assert.IsNull(viewModel.CurrentTrack);
    }

    [TestMethod]
    public async Task SelectingGroupClearsSelectedPlaylistAndCommandAvailability()
    {
        string libraryPath = workspace.CreateLibrary();
        workspace.CreateTrack("Music", "Root", "root.mp3");
        workspace.CreateTrack("Music", "World", "Nested", "nested.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);
        LibraryTreeNode playlist = viewModel.LibraryNodes.Single(node => node.Name == "Root");
        LibraryTreeNode group = viewModel.LibraryNodes.Single(node => node.Name == "World");
        viewModel.SelectNode(playlist);
        Assert.IsTrue(viewModel.CanPlaySelectedPlaylist);

        viewModel.SelectNode(group);

        Assert.IsNull(viewModel.SelectedPlaylist);
        Assert.AreEqual("No playlist selected", viewModel.SelectedPlaylistName);
        Assert.IsEmpty(viewModel.SelectedTracks);
        Assert.IsFalse(viewModel.CanPlaySelectedPlaylist);
        Assert.IsFalse(viewModel.CanPlayNow);
        Assert.IsFalse(viewModel.CanPlayAfterCurrent);
        Assert.IsFalse(viewModel.CanQueueTrack);
    }

    [TestMethod]
    public async Task GroupedSiblingLeafPlaylistsSupportPlayNowAndAfterCurrentIndependently()
    {
        string libraryPath = workspace.CreateLibrary();
        workspace.CreateTrack("Music", "Forest", "Calm", "calm.mp3");
        workspace.CreateTrack("Music", "Forest", "Tense", "tense.mp3");
        await viewModel.SelectLibraryAsync(libraryPath);

        LibraryTreeNode forest = viewModel.LibraryNodes.Single(node => node.Name == "Forest");
        LibraryTreeNode calm = forest.Children.Single(node => node.Name == "Calm");
        LibraryTreeNode tense = forest.Children.Single(node => node.Name == "Tense");

        viewModel.SelectNode(calm);
        await viewModel.PlayNowAsync();
        viewModel.SelectNode(tense);
        await viewModel.AfterCurrentAsync();

        Assert.AreSame(calm.Playlist, viewModel.ActivePlaylist);
        Assert.AreSame(tense.Playlist, viewModel.PendingPlaylist);
        Assert.AreSame(calm.Playlist, viewModel.CurrentPlaylist);
    }

    [TestMethod]
    public async Task PlaybackCommandIsDisabledOnlyWhileItsUiOperationIsInFlight()
    {
        var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _) =>
        {
            playEntered.SetResult();
            await releasePlay.Task.ConfigureAwait(false);
            return new AudioPlaybackInfo(engine.Duration);
        };
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Slow", "slow.mp3");
        viewModel.SelectNode(playlistNode);

        Task playTask = viewModel.PlayNowAsync();
        await playEntered.Task;

        Assert.IsFalse(viewModel.CanPlayNow);
        Assert.IsTrue(viewModel.CanPlayAfterCurrent);

        releasePlay.SetResult();
        await playTask;
        Assert.IsTrue(viewModel.CanPlayNow);
    }

    [TestMethod]
    public async Task MixedPlaybackCommandsBeginInInvocationOrderAndFinalStateMatchesLastCommand()
    {
        var playEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _) =>
        {
            playEntered.TrySetResult();
            await releasePlay.Task.ConfigureAwait(false);
            return new AudioPlaybackInfo(engine.Duration);
        };
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Ordered", "ordered.mp3");
        viewModel.SelectNode(playlistNode);

        Task playTask = viewModel.PlayNowAsync();
        Task stopTask = viewModel.StopAllAsync();

        try
        {
            await playEntered.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(0, engine.StopCount);
            Assert.IsFalse(stopTask.IsCompleted);

            releasePlay.TrySetResult();
            await Task.WhenAll(playTask, stopTask).WaitAsync(TestTimeout);

            Assert.HasCount(1, engine.PlayRequests);
            Assert.AreEqual(1, engine.StopCount);
            Assert.AreEqual(PlaybackState.Stopped, viewModel.PlaybackState);
            Assert.IsNull(viewModel.CurrentTrack);
            Assert.IsFalse(viewModel.CanStopAll);
        }
        finally
        {
            releasePlay.TrySetResult();
        }
    }

    [TestMethod]
    public async Task FailedPlaybackCommandDoesNotPreventLaterQueuedCommand()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Recovery", "recovery.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        var pauseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePause = new ManualResetEventSlim();
        engine.PauseHandler = () =>
        {
            pauseEntered.TrySetResult();
            releasePause.Wait();
        };
        EventHandler? failNextNotification = null;
        failNextNotification = (_, _) =>
        {
            controller.StateChanged -= failNextNotification;
            throw new InvalidOperationException("forced command failure");
        };
        controller.StateChanged += failNextNotification;

        Task pauseTask = viewModel.PauseResumeAsync();
        Task stopTask = Task.CompletedTask;
        try
        {
            await pauseEntered.Task.WaitAsync(TestTimeout);
            stopTask = viewModel.StopAllAsync();
            Assert.IsFalse(stopTask.IsCompleted);

            releasePause.Set();
            await Task.WhenAll(pauseTask, stopTask).WaitAsync(TestTimeout);

            Assert.AreEqual(1, engine.PauseCount);
            Assert.AreEqual(1, engine.StopCount);
            StringAssert.Contains(viewModel.LatestPlaybackError!, "forced command failure");
            Assert.AreEqual(PlaybackState.Stopped, viewModel.PlaybackState);
            Assert.IsNull(viewModel.CurrentTrack);
            Assert.IsFalse(viewModel.CanStopAll);
        }
        finally
        {
            controller.StateChanged -= failNextNotification;
            releasePause.Set();
            await Task.WhenAll(pauseTask, stopTask).WaitAsync(TestTimeout);
        }
    }

    [TestMethod]
    public async Task FifoDispatcherContinuesAfterFaultedWorkItem()
    {
        var dispatcher = new AsyncFifoDispatcher();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionOrder = new ConcurrentQueue<string>();

        Task firstTask = dispatcher.Enqueue(async () =>
        {
            executionOrder.Enqueue("first-started");
            firstEntered.TrySetResult();
            await releaseFirst.Task.ConfigureAwait(false);
            executionOrder.Enqueue("first-failed");
            throw new InvalidOperationException("expected failure");
        });
        Task secondTask = dispatcher.Enqueue(() =>
        {
            executionOrder.Enqueue("second");
            return Task.CompletedTask;
        });
        dispatcher.Complete();

        await firstEntered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(secondTask.IsCompleted);
        releaseFirst.TrySetResult();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstTask.WaitAsync(TestTimeout));
        await secondTask.WaitAsync(TestTimeout);
        await dispatcher.Completion.WaitAsync(TestTimeout);

        Assert.AreEqual("expected failure", exception.Message);
        CollectionAssert.AreEqual(
            new[] { "first-started", "first-failed", "second" },
            executionOrder.ToArray());
    }

    [TestMethod]
    public async Task SynchronouslyBlockingTransportRunsOffUiThreadAndUpdatesState()
    {
        using var uiContext = new DedicatedThreadSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var track = new LibraryTrack("blocking", Path.Combine(workspace.RootPath, "blocking.mp3"));
        var playlist = new LibraryPlaylist("Blocking", workspace.RootPath, [track]);
        await Task.Run(() => localController.PlayNowAsync(playlist)).WaitAsync(TestTimeout);

        MainViewModel localViewModel = await uiContext.InvokeAsync(() => new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            uiContext));
        var transportEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseTransport = new ManualResetEventSlim();
        localEngine.PauseHandler = () =>
        {
            transportEntered.TrySetResult(Environment.CurrentManagedThreadId);
            releaseTransport.Wait();
        };

        Task uiOperation = uiContext.InvokeAsync(async () =>
        {
            Task commandTask = localViewModel.PauseResumeAsync();
            commandReturned.SetResult();
            await commandTask;
        });

        try
        {
            int transportThreadId = await transportEntered.Task.WaitAsync(TestTimeout);
            Assert.AreNotEqual(uiContext.ThreadId, transportThreadId);
            await commandReturned.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(uiOperation.IsCompleted);

            releaseTransport.Set();
            await uiOperation.WaitAsync(TestTimeout);

            Assert.AreEqual(PlaybackState.Paused, localViewModel.PlaybackState);
            Assert.AreEqual("Resume", localViewModel.PauseResumeLabel);
            Assert.AreEqual(1, localEngine.PauseCount);
        }
        finally
        {
            releaseTransport.Set();
            try
            {
                await uiOperation.WaitAsync(TestTimeout);
            }
            catch
            {
            }

            await uiContext.InvokeAsync(() => localViewModel.DisposeAsync().AsTask()).WaitAsync(TestTimeout);
        }
    }

    [TestMethod]
    public async Task AllFadeCommandsUseExpectedArgumentsAndRunOffUiThreadWhenEngineBlocks()
    {
        using var uiContext = new DedicatedThreadSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var track = new LibraryTrack("blocking-fade", Path.Combine(workspace.RootPath, "blocking-fade.mp3"));
        var playlist = new LibraryPlaylist("Blocking Fade", workspace.RootPath, [track]);
        await Task.Run(() => localController.PlayNowAsync(playlist)).WaitAsync(TestTimeout);

        MainViewModel localViewModel = await uiContext.InvokeAsync(() => new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            uiContext));
        var commands = new (MasterFadeDirection Direction, TimeSpan Duration, Func<MainViewModel, Task> Run)[]
        {
            (MasterFadeDirection.In, MainViewModel.QuickFadeDuration, static vm => vm.QuickFadeInAsync()),
            (MasterFadeDirection.Out, MainViewModel.QuickFadeDuration, static vm => vm.QuickFadeOutAsync()),
            (MasterFadeDirection.In, MainViewModel.SlowFadeDuration, static vm => vm.SlowFadeInAsync()),
            (MasterFadeDirection.Out, MainViewModel.SlowFadeDuration, static vm => vm.SlowFadeOutAsync()),
        };

        try
        {
            foreach (var command in commands)
            {
                var fadeEntered = new TaskCompletionSource<int>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var commandReturned = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var releaseFade = new ManualResetEventSlim();
                localEngine.FadeHandler = (_, _) =>
                {
                    fadeEntered.TrySetResult(Environment.CurrentManagedThreadId);
                    releaseFade.Wait();
                    return Task.CompletedTask;
                };

                Task uiOperation = uiContext.InvokeAsync(async () =>
                {
                    Task commandTask = command.Run(localViewModel);
                    commandReturned.TrySetResult();
                    await commandTask;
                });

                try
                {
                    int fadeThreadId = await fadeEntered.Task.WaitAsync(TestTimeout);
                    Assert.AreNotEqual(uiContext.ThreadId, fadeThreadId);
                    await commandReturned.Task.WaitAsync(TestTimeout);
                    Assert.IsFalse(uiOperation.IsCompleted);
                }
                finally
                {
                    releaseFade.Set();
                    await uiOperation.WaitAsync(TestTimeout);
                }
            }

            CollectionAssert.AreEqual(
                commands
                    .Select(command => new FadeRequest(command.Direction, command.Duration))
                    .ToArray(),
                localEngine.FadeRequests.ToArray());
        }
        finally
        {
            localEngine.FadeHandler = null;
            await uiContext.InvokeAsync(() => localViewModel.DisposeAsync().AsTask()).WaitAsync(TestTimeout);
        }
    }

    [TestMethod]
    public async Task SynchronouslyBlockingShutdownRunsOffUiThreadAndUpdatesState()
    {
        using var uiContext = new DedicatedThreadSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var track = new LibraryTrack("shutdown", Path.Combine(workspace.RootPath, "shutdown.mp3"));
        var playlist = new LibraryPlaylist("Shutdown", workspace.RootPath, [track]);
        await Task.Run(() => localController.PlayNowAsync(playlist)).WaitAsync(TestTimeout);

        MainViewModel localViewModel = await uiContext.InvokeAsync(() => new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            uiContext));
        var shutdownEntered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposeReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseShutdown = new ManualResetEventSlim();
        localEngine.DisposeHandler = () =>
        {
            shutdownEntered.TrySetResult(Environment.CurrentManagedThreadId);
            releaseShutdown.Wait();
        };

        Task uiOperation = uiContext.InvokeAsync(async () =>
        {
            Task disposeTask = localViewModel.DisposeAsync().AsTask();
            disposeReturned.SetResult();
            await disposeTask;
        });

        try
        {
            int shutdownThreadId = await shutdownEntered.Task.WaitAsync(TestTimeout);
            Assert.AreNotEqual(uiContext.ThreadId, shutdownThreadId);
            await disposeReturned.Task.WaitAsync(TestTimeout);
            Assert.IsFalse(uiOperation.IsCompleted);

            releaseShutdown.Set();
            await uiOperation.WaitAsync(TestTimeout);

            Assert.IsTrue(localEngine.IsDisposed);
            Assert.AreEqual(PlaybackState.Stopped, localViewModel.PlaybackState);
            Assert.IsNull(localViewModel.CurrentTrack);
            Assert.IsFalse(localViewModel.CanPlaySelectedPlaylist);
            Assert.IsFalse(localViewModel.CanPauseResume);
            Assert.IsFalse(localViewModel.CanSkip);
            Assert.IsFalse(localViewModel.CanStopAll);

            await uiContext.InvokeAsync(() => localViewModel.DisposeAsync().AsTask()).WaitAsync(TestTimeout);
        }
        finally
        {
            releaseShutdown.Set();
            try
            {
                await uiOperation.WaitAsync(TestTimeout);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task DisposeStopsPlaybackDisposesEngineAndDisablesCommands()
    {
        LibraryTreeNode playlistNode = await LoadPlaylistAsync("Active", "active.mp3");
        viewModel.SelectNode(playlistNode);
        await viewModel.PlayNowAsync();

        await viewModel.DisposeAsync();
        await viewModel.DisposeAsync();

        Assert.IsTrue(engine.IsDisposed);
        Assert.AreEqual(1, engine.StopCount);
        Assert.AreEqual(PlaybackState.Stopped, viewModel.PlaybackState);
        Assert.IsNull(viewModel.CurrentTrack);
        Assert.IsFalse(viewModel.CanPlaySelectedPlaylist);
        Assert.IsFalse(viewModel.CanPauseResume);
        Assert.IsFalse(viewModel.CanSkip);
        Assert.IsFalse(viewModel.CanStopAll);
    }

    [TestMethod]
    public async Task ControllerCallbacksAreMarshaledAndOutputFaultsRemainVisibleWithoutWpfApplication()
    {
        var synchronizationContext = new QueuedSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var localViewModel = new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            synchronizationContext);
        var track = new LibraryTrack("worker", Path.Combine(workspace.RootPath, "worker.mp3"));
        var playlist = new LibraryPlaylist("Worker", workspace.RootPath, [track]);

        try
        {
            Task.Run(() => localController.PlayNowAsync(playlist)).GetAwaiter().GetResult();

            Assert.IsNull(localViewModel.CurrentTrack);
            Assert.IsGreaterThan(0, synchronizationContext.PendingCount);
            synchronizationContext.Drain();
            Assert.AreSame(track, localViewModel.CurrentTrack);

            localEngine.MasterGain = 0f;
            localEngine.MasterFadeState = MasterFadeState.Muted;
            await localViewModel.UpdatePlaybackProgressAsync();
            Assert.AreEqual(MasterFadeState.Muted, localViewModel.MasterFadeState);

            long playbackId = localController.CurrentPlaybackId!.Value;
            Task.Run(() => localEngine.RaiseOutputFaultAsync(new AudioOutputFault(
                "device disconnected",
                new InvalidOperationException("endpoint unavailable"),
                playbackId))).GetAwaiter().GetResult();

            Assert.IsFalse(localViewModel.HasPlaybackError);
            synchronizationContext.Drain();
            Assert.IsTrue(localViewModel.HasPlaybackError);
            StringAssert.Contains(localViewModel.LatestPlaybackError!, "device disconnected");
            StringAssert.Contains(localViewModel.LatestPlaybackError!, "endpoint unavailable");
            Assert.AreEqual(PlaybackState.Stopped, localViewModel.PlaybackState);
            Assert.AreEqual(1f, localViewModel.MasterGain);
            Assert.AreEqual(MasterFadeState.Full, localViewModel.MasterFadeState);
            Assert.AreEqual("100%", localViewModel.MasterGainDisplay);
        }
        finally
        {
            Task disposeTask = localViewModel.DisposeAsync().AsTask();
            while (!disposeTask.IsCompleted)
            {
                synchronizationContext.Drain();
                await Task.Yield();
            }

            synchronizationContext.Drain();
            await disposeTask;
        }
    }

    [TestMethod]
    public async Task QueuedStateCallbackAppliesLatestControllerSnapshot()
    {
        var synchronizationContext = new QueuedSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var localViewModel = new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            synchronizationContext);
        var track = new LibraryTrack("current", Path.Combine(workspace.RootPath, "current.mp3"));
        var playlist = new LibraryPlaylist("Current", workspace.RootPath, [track]);

        try
        {
            Task.Run(() => localController.PlayNowAsync(playlist)).GetAwaiter().GetResult();
            Task.Run(localController.PauseAsync).GetAwaiter().GetResult();

            Assert.AreEqual(2, synchronizationContext.PendingCount);
            Assert.AreEqual(PlaybackState.Stopped, localViewModel.PlaybackState);

            Assert.IsTrue(synchronizationContext.ExecuteNext());

            Assert.AreSame(track, localViewModel.CurrentTrack);
            Assert.AreEqual(PlaybackState.Paused, localViewModel.PlaybackState);
            Assert.AreEqual(1, synchronizationContext.PendingCount);
        }
        finally
        {
            await DisposeAsync(localViewModel, synchronizationContext);
        }
    }

    [TestMethod]
    public async Task QueuedErrorCallbackDoesNotOverwriteNewerControllerError()
    {
        var synchronizationContext = new QueuedSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var localViewModel = new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            synchronizationContext);

        try
        {
            Task.Run(() => localEngine.RaiseOutputFaultAsync(new AudioOutputFault(
                "older fault",
                new InvalidOperationException("older detail"))))
                .GetAwaiter()
                .GetResult();
            Task.Run(() => localEngine.RaiseOutputFaultAsync(new AudioOutputFault(
                "newer fault",
                new IOException("newer detail"))))
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual(4, synchronizationContext.PendingCount);

            Assert.IsTrue(synchronizationContext.ExecuteNext());
            Assert.IsFalse(localViewModel.HasPlaybackError);

            Assert.IsTrue(synchronizationContext.ExecuteNext());
            Assert.IsTrue(synchronizationContext.ExecuteNext());

            StringAssert.Contains(localViewModel.LatestPlaybackError!, "newer fault");
            StringAssert.Contains(localViewModel.LatestPlaybackError!, "IOException: newer detail");
            Assert.DoesNotContain("older", localViewModel.LatestPlaybackError!);
        }
        finally
        {
            await DisposeAsync(localViewModel, synchronizationContext);
        }
    }

    [TestMethod]
    public async Task QueuedControllerCallbacksDoNotPublishAfterDisposalStarts()
    {
        var synchronizationContext = new QueuedSynchronizationContext();
        var localEngine = new FakeAudioEngine();
        var localController = new PlaybackController(localEngine, new ZeroRandom());
        var localViewModel = new MainViewModel(
            new LibraryScanner(),
            workspace.CreateSettingsService(),
            localController,
            synchronizationContext);
        var track = new LibraryTrack("stale", Path.Combine(workspace.RootPath, "stale.mp3"));
        var playlist = new LibraryPlaylist("Stale", workspace.RootPath, [track]);

        localEngine.RaiseOutputFaultAsync(new AudioOutputFault(
            "baseline fault",
            new InvalidOperationException("baseline detail")))
            .GetAwaiter()
            .GetResult();
        string baselineError = localViewModel.LatestPlaybackError!;

        try
        {
            Task.Run(() => localController.PlayNowAsync(playlist)).GetAwaiter().GetResult();
            localEngine.PauseException = new InvalidOperationException("queued error detail");
            Task.Run(localController.PauseAsync).GetAwaiter().GetResult();
            Assert.AreEqual(3, synchronizationContext.PendingCount);

            Task disposeTask = localViewModel.DisposeAsync().AsTask();

            Assert.IsTrue(synchronizationContext.ExecuteNext());
            Assert.IsTrue(synchronizationContext.ExecuteNext());
            Assert.IsTrue(synchronizationContext.ExecuteNext());
            Assert.AreEqual(PlaybackState.Stopped, localViewModel.PlaybackState);
            Assert.IsNull(localViewModel.CurrentTrack);
            Assert.AreEqual(baselineError, localViewModel.LatestPlaybackError);

            await PumpUntilCompletedAsync(disposeTask, synchronizationContext);

            Assert.AreEqual(PlaybackState.Stopped, localViewModel.PlaybackState);
            Assert.IsNull(localViewModel.CurrentTrack);
            Assert.AreEqual(baselineError, localViewModel.LatestPlaybackError);
            Assert.DoesNotContain("queued error detail", localViewModel.LatestPlaybackError!);
        }
        finally
        {
            await DisposeAsync(localViewModel, synchronizationContext);
        }
    }

    private async Task<LibraryTreeNode> LoadPlaylistAsync(
        string playlistName,
        params string[] fileNames)
    {
        IReadOnlyList<LibraryTreeNode> nodes = await LoadPlaylistsAsync((playlistName, fileNames));
        return nodes.Single();
    }

    private async Task<IReadOnlyList<LibraryTreeNode>> LoadPlaylistsAsync(
        params (string Name, string[] Files)[] playlists)
    {
        string libraryPath = workspace.CreateLibrary();
        foreach ((string name, string[] files) in playlists)
        {
            foreach (string file in files)
            {
                workspace.CreateTrack("Music", name, file);
            }
        }

        await viewModel.SelectLibraryAsync(libraryPath);
        return viewModel.LibraryNodes;
    }

    private void AssertFadeAvailability(
        bool canQuickIn,
        bool canQuickOut,
        bool canSlowIn,
        bool canSlowOut)
    {
        Assert.AreEqual(canQuickIn, viewModel.CanQuickFadeIn);
        Assert.AreEqual(canQuickOut, viewModel.CanQuickFadeOut);
        Assert.AreEqual(canSlowIn, viewModel.CanSlowFadeIn);
        Assert.AreEqual(canSlowOut, viewModel.CanSlowFadeOut);
    }

    private static async Task DisposeAsync(
        MainViewModel viewModel,
        QueuedSynchronizationContext synchronizationContext)
    {
        Task disposeTask = viewModel.DisposeAsync().AsTask();
        await PumpUntilCompletedAsync(disposeTask, synchronizationContext);
    }

    private static async Task PumpUntilCompletedAsync(
        Task task,
        QueuedSynchronizationContext synchronizationContext)
    {
        await PumpUntilCompletedCoreAsync(task, synchronizationContext).WaitAsync(TestTimeout);
        await task.WaitAsync(TestTimeout);
    }

    private static async Task PumpUntilCompletedCoreAsync(
        Task task,
        QueuedSynchronizationContext synchronizationContext)
    {
        while (!task.IsCompleted)
        {
            if (!synchronizationContext.ExecuteNext())
            {
                await Task.Yield();
            }
        }

        synchronizationContext.Drain();
    }

    private sealed class TemporaryPlaybackWorkspace : IDisposable
    {
        public TemporaryPlaybackWorkspace()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "Soundrel.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public SettingsService CreateSettingsService() =>
            new(Path.Combine(RootPath, "settings.json"));

        public string CreateLibrary()
        {
            string path = Path.Combine(RootPath, "Library");
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateTrack(params string[] relativeParts)
        {
            string path = relativeParts.Aggregate(
                Path.Combine(RootPath, "Library"),
                Path.Combine);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();

        public int PendingCount => callbacks.Count;

        public override void Post(SendOrPostCallback d, object? state) => callbacks.Enqueue((d, state));

        public bool ExecuteNext()
        {
            if (!callbacks.TryDequeue(out var work))
            {
                return false;
            }

            SynchronizationContext? previousContext = Current;
            SetSynchronizationContext(this);
            try
            {
                work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previousContext);
            }

            return true;
        }

        public void Drain()
        {
            while (ExecuteNext())
            {
            }
        }
    }

    private sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = [];
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread thread;

        public DedicatedThreadSynchronizationContext()
        {
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Soundrel test UI thread",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => callbacks.Add((d, state));

        public Task InvokeAsync(Func<Task> action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                async state =>
                {
                    var work = ((Func<Task> Action, TaskCompletionSource Completion))state!;
                    try
                    {
                        await work.Action();
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

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                state =>
                {
                    var work = ((Func<T> Action, TaskCompletionSource<T> Completion))state!;
                    try
                    {
                        work.Completion.SetResult(work.Action());
                    }
                    catch (Exception exception)
                    {
                        work.Completion.SetException(exception);
                    }
                },
                (action, completion));
            return completion.Task;
        }

        public void Dispose()
        {
            callbacks.CompleteAdding();
            thread.Join();
            callbacks.Dispose();
        }

        private void Run()
        {
            SynchronizationContext.SetSynchronizationContext(this);
            ThreadId = Environment.CurrentManagedThreadId;
            started.SetResult();

            foreach ((SendOrPostCallback callback, object? state) in callbacks.GetConsumingEnumerable())
            {
                callback(state);
            }
        }
    }
}
