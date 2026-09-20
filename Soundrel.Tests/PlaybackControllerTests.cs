using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class PlaybackControllerTests
{
    [TestMethod]
    public async Task ConfigureTiming_RejectsStaggerLongerThanMediumAndAcceptsEquality()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            controller.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.01)));

        await controller.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        Assert.AreEqual(
            new TimingRequest(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
            engine.TimingRequests.Single());
    }

    [TestMethod]
    public async Task PlayNow_ChangesActivePlaylistClearsPendingAndPreservesQueue()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var pending = Playlist("Pending", "pending");
        var queuedSource = Playlist("Queued", "queued");
        var replacement = Playlist("Replacement", "replacement");

        await controller.PlayNowAsync(first);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queuedSource.Tracks[0], queuedSource);
        await controller.PlayNowAsync(replacement, ImmediateTransitionMode.Crossfade);

        Assert.AreSame(replacement, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.AreSame(replacement.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(replacement, controller.CurrentPlaylist);
        Assert.HasCount(1, controller.Queue);
        Assert.AreSame(queuedSource.Tracks[0], controller.Queue[0].Track);
        Assert.AreEqual(0, engine.StopCount);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, engine.PlayRequests[1].TransitionMode);
    }

    [TestMethod]
    public async Task PlayNow_PassesTransitionModeToEveryPlaylistCandidate()
    {
        foreach (var transitionMode in new[]
                 {
                     ImmediateTransitionMode.HardCut,
                     ImmediateTransitionMode.Crossfade,
                 })
        {
            var playlist = Playlist($"{transitionMode}", "one", "two", "three");
            var engine = new FakeAudioEngine();
            foreach (var track in playlist.Tracks)
            {
                engine.UnreadablePaths.Add(track.FilePath);
            }

            await using var controller = new PlaybackController(engine, new ZeroRandom());
            await controller.PlayNowAsync(playlist, transitionMode);

            Assert.HasCount(playlist.Tracks.Count, engine.PlayRequests);
            Assert.IsTrue(engine.PlayRequests.All(request => request.TransitionMode == transitionMode));
        }
    }

    [TestMethod]
    public async Task PlayNow_RejectsUnknownTransitionMode()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            controller.PlayNowAsync(Playlist("Active", "active"), (ImmediateTransitionMode)99));
        Assert.IsEmpty(engine.PlayRequests);
    }

    [TestMethod]
    public async Task AfterCurrent_ReplacesPendingWithoutInterruptingPlayback()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var firstPending = Playlist("First Pending", "first-pending");
        var secondPending = Playlist("Second Pending", "second-pending");

        await controller.PlayNowAsync(active, ImmediateTransitionMode.Crossfade);
        var playbackId = controller.CurrentPlaybackId;
        await controller.AfterCurrentAsync(firstPending);
        await controller.AfterCurrentAsync(secondPending);

        Assert.AreSame(secondPending, controller.PendingPlaylist);
        Assert.AreSame(active.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(playbackId, controller.CurrentPlaybackId);
        Assert.HasCount(1, engine.PlayRequests);
        Assert.AreEqual(0, engine.StopCount);
    }

    [TestMethod]
    public async Task Completion_PromotesPendingBeforeStartingQueuedTrack()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var pending = Playlist("Pending", "pending");
        var queuedSource = Playlist("Queue Source", "queued");

        await controller.PlayNowAsync(active, ImmediateTransitionMode.Crossfade);
        await controller.QueueTrackAsync(queuedSource.Tracks[0], queuedSource);
        await controller.AfterCurrentAsync(pending);
        await engine.RaiseTrackEndedAsync(controller.CurrentPlaybackId!.Value);

        Assert.AreSame(pending, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.AreSame(queuedSource.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(queuedSource, controller.CurrentPlaylist);
        Assert.IsEmpty(controller.Queue);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, engine.PlayRequests[1].TransitionMode);
    }

    [TestMethod]
    public async Task Completion_PlaysQueueFifoThenReturnsToActivePlaylist()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active-one", "active-two");
        var queueSource = Playlist("Queue Source", "queued-one", "queued-two");

        await controller.PlayNowAsync(active, ImmediateTransitionMode.Crossfade);
        await controller.QueueTrackAsync(queueSource.Tracks[0], queueSource);
        await controller.QueueTrackAsync(queueSource.Tracks[1], queueSource);

        await CompleteCurrentAsync(controller, engine);
        Assert.AreSame(queueSource.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(active, controller.ActivePlaylist);

        await CompleteCurrentAsync(controller, engine);
        Assert.AreSame(queueSource.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(active, controller.ActivePlaylist);

        await CompleteCurrentAsync(controller, engine);
        Assert.AreSame(active.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(active, controller.CurrentPlaylist);
        CollectionAssert.AreEqual(
            new[] { "active-one", "queued-one", "queued-two", "active-two" },
            engine.PlayRequests.Select(request => request.Track.Name).ToArray());
        Assert.IsTrue(engine.PlayRequests.Skip(1).All(
            request => request.TransitionMode == ImmediateTransitionMode.HardCut));
    }

    [TestMethod]
    public async Task PlayNow_HardCutsAtomicallyAndIgnoresOldCompletion()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var second = Playlist("Second", "second-one", "second-two");

        await controller.PlayNowAsync(first);
        var oldId = controller.CurrentPlaybackId!.Value;
        await controller.PlayNowAsync(second);
        var newId = controller.CurrentPlaybackId;

        await engine.RaiseTrackEndedAsync(oldId);

        Assert.AreSame(second.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(newId, controller.CurrentPlaybackId);
        Assert.HasCount(2, engine.PlayRequests);
        Assert.AreEqual(0, engine.StopCount);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, engine.PlayRequests[1].TransitionMode);
    }

    [TestMethod]
    public async Task Skip_UsesPendingThenQueuePrecedence()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var pending = Playlist("Pending", "pending");
        var queueSource = Playlist("Queue Source", "queued");

        await controller.PlayNowAsync(active, ImmediateTransitionMode.Crossfade);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queueSource.Tracks[0], queueSource);
        await controller.SkipAsync();

        Assert.AreSame(pending, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.AreSame(queueSource.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(queueSource, controller.CurrentPlaylist);
        Assert.AreEqual(1, engine.StopCount);
        Assert.AreEqual(ImmediateTransitionMode.HardCut, engine.PlayRequests[1].TransitionMode);
    }

    [TestMethod]
    public async Task Skip_WithNoCurrentTrackIsNoOp()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var pending = Playlist("Pending", "pending");

        await controller.AfterCurrentAsync(pending);
        await controller.SkipAsync();

        Assert.AreSame(pending, controller.PendingPlaylist);
        Assert.IsNull(controller.ActivePlaylist);
        Assert.AreEqual(0, engine.StopCount);
        Assert.IsEmpty(engine.PlayRequests);
    }

    [TestMethod]
    public async Task PauseAndResume_UpdateStateAfterSuccessfulEngineCommands()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));

        await controller.PauseAsync();
        Assert.AreEqual(PlaybackState.Paused, controller.State);
        Assert.AreEqual(1, engine.PauseCount);

        await controller.ResumeAsync();
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(1, engine.ResumeCount);
    }

    [TestMethod]
    public async Task PauseAndResumeFailures_LeaveStateUnchangedAndReportErrors()
    {
        var engine = new FakeAudioEngine
        {
            PauseException = new InvalidOperationException("pause failed"),
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var errors = new List<string>();
        controller.ErrorOccurred += (_, error) => errors.Add(error.Message);
        await controller.PlayNowAsync(Playlist("Active", "active"));

        await controller.PauseAsync();
        Assert.AreEqual(PlaybackState.Playing, controller.State);

        engine.PauseException = null;
        await controller.PauseAsync();
        engine.ResumeException = new InvalidOperationException("resume failed");
        await controller.ResumeAsync();

        Assert.AreEqual(PlaybackState.Paused, controller.State);
        Assert.HasCount(2, errors);
        StringAssert.Contains(errors[0], "pause");
        StringAssert.Contains(errors[1], "resume");
        StringAssert.Contains(controller.LastError!, "resume");
    }

    [TestMethod]
    public async Task FadeMaster_PropagatesDirectionAndDurationAndPreservesPlaybackState()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));

        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(4));

        Assert.AreEqual(MasterFadeDirection.Out, engine.FadeRequests[^1].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(4), engine.FadeRequests[^1].FullScaleDuration);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);
        Assert.AreEqual(PlaybackState.Playing, controller.State);

        engine.MasterGain = 0.25f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();
        await controller.PauseAsync();
        await controller.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.FromSeconds(6));

        Assert.AreEqual(MasterFadeDirection.In, engine.FadeRequests[^1].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(6), engine.FadeRequests[^1].FullScaleDuration);
        Assert.AreEqual(0.25f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.MasterFadeState);
        Assert.AreEqual(PlaybackState.Paused, controller.State);
    }

    [TestMethod]
    public async Task FadeMaster_WithNoCurrentTrackIsNoOp()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));

        Assert.IsEmpty(engine.FadeRequests);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
    }

    [TestMethod]
    public async Task FadeMaster_ZeroDurationImmediatelyUpdatesEndpoints()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));

        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);

        await controller.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.Zero);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
        Assert.HasCount(4, engine.FadeRequests);
    }

    [TestMethod]
    public async Task FadeMaster_EngineFailureReportsErrorWithoutClearingCurrentPlayback()
    {
        var fadeException = new InvalidOperationException("fade failed");
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "active");
        var errors = new List<PlaybackErrorEventArgs>();
        controller.ErrorOccurred += (_, error) => errors.Add(error);
        await controller.PlayNowAsync(playlist);
        var playbackId = controller.CurrentPlaybackId;
        engine.FadeException = fadeException;

        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(3));

        Assert.AreSame(playlist.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(playbackId, controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.MasterFadeState);
        Assert.HasCount(1, errors);
        Assert.AreSame(fadeException, errors[0].Exception);
        StringAssert.Contains(errors[0].Message, "fade master");
    }

    [TestMethod]
    public async Task FadeMaster_ValidatesDirectionAndDuration()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            controller.FadeMasterAsync((MasterFadeDirection)99, TimeSpan.FromSeconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromTicks(-1)));
        Assert.IsEmpty(engine.FadeRequests);
    }

    [TestMethod]
    public async Task ClearQueue_RemovesOnlyExplicitQueueEntries()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var pending = Playlist("Pending", "pending");
        var queued = Playlist("Queued", "one", "two");

        await controller.PlayNowAsync(active);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        await controller.QueueTrackAsync(queued.Tracks[1], queued);
        var playbackId = controller.CurrentPlaybackId;
        await controller.ClearQueueAsync();

        Assert.IsEmpty(controller.Queue);
        Assert.AreSame(active, controller.ActivePlaylist);
        Assert.AreSame(pending, controller.PendingPlaylist);
        Assert.AreSame(active.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(playbackId, controller.CurrentPlaybackId);
    }

    [TestMethod]
    public async Task StopAll_ClearsTransitionalStateButKeepsActivePlaylistAndQueue()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var pending = Playlist("Pending", "pending");
        var queued = Playlist("Queued", "queued");

        await controller.PlayNowAsync(active);
        var stoppedId = controller.CurrentPlaybackId!.Value;
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        engine.MasterGain = 0.3f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();
        await controller.StopAllAsync();

        Assert.AreSame(active, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.IsNull(controller.CurrentTrack);
        Assert.IsNull(controller.CurrentPlaylist);
        Assert.IsNull(controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);
        Assert.AreEqual(0f, controller.Snapshot.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.Snapshot.MasterFadeState);
        Assert.HasCount(1, controller.Queue);

        await engine.RaiseTrackEndedAsync(stoppedId);
        Assert.IsNull(controller.CurrentTrack);
        Assert.HasCount(1, engine.PlayRequests);
    }

    [TestMethod]
    public async Task PlayNow_EmptyPlaylistStopsCleanlyWithUsefulError()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var empty = Playlist("Empty");

        await controller.PlayNowAsync(empty);

        Assert.AreSame(empty, controller.ActivePlaylist);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.IsNull(controller.CurrentTrack);
        Assert.IsEmpty(engine.PlayRequests);
        StringAssert.Contains(controller.LastError!, "Empty");
        StringAssert.Contains(controller.LastError!, "no playable tracks");
    }

    [TestMethod]
    public async Task PlayNow_UnreadableFirstPlaylistTrackContinuesToNextTrack()
    {
        var playlist = Playlist("Active", "missing", "working");
        var engine = new FakeAudioEngine();
        engine.UnreadablePaths.Add(playlist.Tracks[0].FilePath);
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        await controller.PlayNowAsync(playlist);

        Assert.AreSame(playlist.Tracks[1], controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.HasCount(2, engine.PlayRequests);
        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            engine.PlayRequests.Select(request => request.PlaybackId).ToArray());
        StringAssert.Contains(controller.LastError!, "missing");
    }

    [TestMethod]
    public async Task PlayNow_AllUnreadablePlaylistTracksAreAttemptedOnceThenStop()
    {
        var playlist = Playlist("Active", "missing-one", "missing-two", "missing-three");
        var engine = new FakeAudioEngine();
        foreach (var track in playlist.Tracks)
        {
            engine.UnreadablePaths.Add(track.FilePath);
        }

        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(playlist);

        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.IsNull(controller.CurrentTrack);
        Assert.HasCount(playlist.Tracks.Count, engine.PlayRequests);
        Assert.HasCount(
            playlist.Tracks.Count,
            engine.PlayRequests.Select(request => request.Track.FilePath).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PlayNow_UnreadableReplacementCandidatePreservesOldPlaybackUntilLaterCandidateStarts()
    {
        var engine = new FakeAudioEngine
        {
            Duration = TimeSpan.FromSeconds(73),
        };
        var random = new CallbackRandom();
        await using var controller = new PlaybackController(engine, random);
        var oldPlaylist = Playlist("Old", "old");
        var replacement = Playlist("Replacement", "missing", "working");

        await controller.PlayNowAsync(oldPlaylist);
        await controller.PauseAsync();
        var oldSnapshot = controller.Snapshot;
        var oldPlaybackId = controller.CurrentPlaybackId;
        PlaybackSnapshot? restoredSnapshot = null;
        random.BeforeNext = callCount =>
        {
            if (callCount == 3)
            {
                restoredSnapshot = controller.Snapshot;
            }
        };
        var enginePlaybackIdsDuringOpen = new List<long?>();
        engine.PlayHandler = async (track, _) =>
        {
            enginePlaybackIdsDuringOpen.Add((await engine.GetProgressAsync()).PlaybackId);
            if (track.Name == "missing")
            {
                throw new IOException("unreadable");
            }

            return new AudioPlaybackInfo(TimeSpan.FromSeconds(91));
        };

        await controller.PlayNowAsync(replacement, ImmediateTransitionMode.Crossfade);

        CollectionAssert.AreEqual(
            new long?[] { oldPlaybackId, oldPlaybackId },
            enginePlaybackIdsDuringOpen);
        Assert.IsNotNull(restoredSnapshot);
        Assert.AreSame(oldSnapshot.CurrentTrack, restoredSnapshot.CurrentTrack);
        Assert.AreSame(oldSnapshot.CurrentPlaylist, restoredSnapshot.CurrentPlaylist);
        Assert.AreEqual(oldSnapshot.CurrentPlaybackId, restoredSnapshot.CurrentPlaybackId);
        Assert.AreEqual(oldSnapshot.CurrentDuration, restoredSnapshot.CurrentDuration);
        Assert.AreEqual(oldSnapshot.State, restoredSnapshot.State);
        Assert.AreSame(replacement.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(replacement, controller.CurrentPlaylist);
        Assert.AreEqual(TimeSpan.FromSeconds(91), controller.CurrentDuration);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(0, engine.StopCount);
        Assert.IsTrue(engine.PlayRequests.Skip(1).All(
            request => request.TransitionMode == ImmediateTransitionMode.Crossfade));
    }

    [TestMethod]
    public async Task FailedReplacementRestoresPriorTrackWhenPhysicalProgressStillMatches()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var replacement = Playlist("Replacement", "decode-fails", "working");
        long? progressDuringFailure = null;
        engine.PlayHandler = (track, _) => track.Name == "decode-fails"
            ? FailAfterReadingProgressAsync()
            : Task.FromResult(new AudioPlaybackInfo(TimeSpan.FromSeconds(42)));

        async Task<AudioPlaybackInfo> FailAfterReadingProgressAsync()
        {
            progressDuringFailure = (await engine.GetProgressAsync()).PlaybackId;
            throw new IOException("decode failed");
        }

        await controller.PlayNowAsync(first);
        var priorId = controller.CurrentPlaybackId;
        await controller.PlayNowAsync(replacement);

        Assert.AreEqual(priorId, progressDuringFailure);
        Assert.AreSame(replacement.Tracks[1], controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        StringAssert.Contains(controller.LastError!, "decode-fails");
    }

    [TestMethod]
    public async Task FailedReplacementClearsPriorTrackWhenOutputStartClearsPhysicalGraph()
    {
        var engine = new FakeAudioEngine
        {
            ClearPhysicalGraphOnPlayFailure = true,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var replacement = Playlist("Replacement", "replacement");
        await controller.PlayNowAsync(first);
        engine.PlayHandler = (_, _) => Task.FromException<AudioPlaybackInfo>(
            new InvalidOperationException("output start failed"));

        await controller.PlayNowAsync(replacement);

        Assert.AreSame(replacement, controller.ActivePlaylist);
        Assert.IsNull(controller.CurrentTrack);
        Assert.IsNull(controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        StringAssert.Contains(controller.LastError!, "Could not play");
    }

    [TestMethod]
    public async Task AmbienceStartupFailureThatClearsGraphClearsMusicAndAmbienceState()
    {
        var engine = new FakeAudioEngine
        {
            PlayAmbienceException = new InvalidOperationException("output start failed"),
            ClearPhysicalGraphOnAmbienceFailure = true,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "active");
        var existing = new LibraryTrack("Existing", @"C:\Library\existing.mp3");
        var failed = new LibraryTrack("Failed", @"C:\Library\failed.mp3");
        await controller.PlayNowAsync(playlist);
        await controller.PlayAmbienceAsync(existing, 0.5f);

        await controller.PlayAmbienceAsync(failed, 0.5f);

        Assert.IsNull(controller.CurrentTrack);
        Assert.IsNull(controller.CurrentPlaybackId);
        Assert.IsEmpty(controller.Ambience);
        StringAssert.Contains(controller.LastError!, "Failed");
    }

    [TestMethod]
    public async Task OrdinaryAmbienceOpenFailurePreservesMusicAndExistingAmbience()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "active");
        var existing = new LibraryTrack("Existing", @"C:\Library\existing.mp3");
        var failed = new LibraryTrack("Failed", @"C:\Library\failed.mp3");
        engine.UnreadablePaths.Add(failed.FilePath);
        await controller.PlayNowAsync(playlist);
        await controller.PlayAmbienceAsync(existing, 0.5f);

        await controller.PlayAmbienceAsync(failed, 0.5f);

        Assert.AreSame(playlist.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.HasCount(1, controller.Ambience);
        Assert.AreEqual(existing.FilePath, controller.Ambience[0].FilePath);
        StringAssert.Contains(controller.LastError!, "Failed");
    }

    [TestMethod]
    public async Task PlayNow_AllReplacementCandidatesFailStopsOldOnceAndPreservesQueue()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var oldPlaylist = Playlist("Old", "old");
        var pending = Playlist("Pending", "pending");
        var queued = Playlist("Queued", "queued");
        var replacement = Playlist("Replacement", "missing-one", "missing-two");

        await controller.PlayNowAsync(oldPlaylist);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        foreach (var track in replacement.Tracks)
        {
            engine.UnreadablePaths.Add(track.FilePath);
        }

        await controller.PlayNowAsync(replacement, ImmediateTransitionMode.Crossfade);

        Assert.AreSame(replacement, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.IsNull(controller.CurrentTrack);
        Assert.IsNull(controller.CurrentPlaylist);
        Assert.IsNull(controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(1, engine.StopCount);
        Assert.HasCount(1, controller.Queue);
        Assert.AreSame(queued.Tracks[0], controller.Queue[0].Track);
        Assert.IsTrue(engine.PlayRequests.Skip(1).All(
            request => request.TransitionMode == ImmediateTransitionMode.Crossfade));
    }

    [TestMethod]
    public async Task PlayNow_EmptyReplacementStopsOldOnceAndPreservesQueue()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var oldPlaylist = Playlist("Old", "old");
        var pending = Playlist("Pending", "pending");
        var queued = Playlist("Queued", "queued");
        var empty = Playlist("Empty");

        await controller.PlayNowAsync(oldPlaylist);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        await controller.PlayNowAsync(empty, ImmediateTransitionMode.Crossfade);

        Assert.AreSame(empty, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.IsNull(controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(1, engine.StopCount);
        Assert.HasCount(1, controller.Queue);
        Assert.AreSame(queued.Tracks[0], controller.Queue[0].Track);
        Assert.HasCount(1, engine.PlayRequests);
        StringAssert.Contains(controller.LastError!, "no playable tracks");
    }

    [TestMethod]
    public async Task StaleOldCompletionAndFaultDuringReplacementCannotClearNewPlaybackOrMasterState()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var second = Playlist("Second", "second");

        await controller.PlayNowAsync(first);
        var oldPlaybackId = controller.CurrentPlaybackId!.Value;
        var oldTrack = controller.CurrentTrack;
        var oldPlaylist = controller.CurrentPlaylist;
        var oldDuration = controller.CurrentDuration;
        var oldState = controller.State;
        engine.MasterGain = 0.4f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();

        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, _) =>
        {
            replacementEntered.SetResult();
            await releaseReplacement.Task.ConfigureAwait(false);
            return new AudioPlaybackInfo(engine.Duration);
        };

        var replacementTask = controller.PlayNowAsync(second, ImmediateTransitionMode.Crossfade);
        await replacementEntered.Task;
        Assert.AreSame(oldTrack, controller.CurrentTrack);
        Assert.AreSame(oldPlaylist, controller.CurrentPlaylist);
        Assert.AreEqual(oldDuration, controller.CurrentDuration);
        Assert.AreEqual(oldState, controller.State);
        Assert.AreNotEqual(oldPlaybackId, controller.CurrentPlaybackId);
        var staleCompletionTask = engine.RaiseTrackEndedAsync(oldPlaybackId);
        var staleFaultTask = engine.RaiseOutputFaultAsync(new AudioOutputFault(
            "stale during replacement",
            PlaybackId: oldPlaybackId));
        Assert.IsFalse(staleCompletionTask.IsCompleted);
        Assert.IsFalse(staleFaultTask.IsCompleted);

        releaseReplacement.SetResult();
        await Task.WhenAll(replacementTask, staleCompletionTask, staleFaultTask);
        var newPlaybackId = controller.CurrentPlaybackId;
        await engine.RaiseTrackEndedAsync(oldPlaybackId);
        await engine.RaiseOutputFaultAsync(new AudioOutputFault(
            "stale after replacement",
            PlaybackId: oldPlaybackId));

        Assert.AreSame(second.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(newPlaybackId, controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(0.4f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.MasterFadeState);
    }

    [TestMethod]
    public async Task Completion_ConsumesUnreadableQueuedEntriesAndContinuesFifo()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active-one", "active-two");
        var queued = Playlist("Queued", "missing", "working");
        engine.UnreadablePaths.Add(queued.Tracks[0].FilePath);

        await controller.PlayNowAsync(active);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        await controller.QueueTrackAsync(queued.Tracks[1], queued);
        await CompleteCurrentAsync(controller, engine);

        Assert.AreSame(queued.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(queued, controller.CurrentPlaylist);
        Assert.IsEmpty(controller.Queue);
        CollectionAssert.AreEqual(
            new[] { "active-one", "missing", "working" },
            engine.PlayRequests.Select(request => request.Track.Name).ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3 },
            engine.PlayRequests.Select(request => request.PlaybackId).ToArray());

        await CompleteCurrentAsync(controller, engine);
        Assert.AreSame(active.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(active, controller.CurrentPlaylist);
    }

    [TestMethod]
    public async Task DuplicateCompletionNotificationAdvancesOnlyOnce()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "one", "two", "three");

        await controller.PlayNowAsync(playlist);
        var completedId = controller.CurrentPlaybackId!.Value;
        await engine.RaiseTrackEndedAsync(completedId);
        var replacementTrack = controller.CurrentTrack;
        var replacementId = controller.CurrentPlaybackId;
        await engine.RaiseTrackEndedAsync(completedId);

        Assert.AreSame(replacementTrack, controller.CurrentTrack);
        Assert.AreEqual(replacementId, controller.CurrentPlaybackId);
        Assert.HasCount(2, engine.PlayRequests);
    }

    [TestMethod]
    public async Task ConcurrentPlayNowCommandsAreSerialized()
    {
        var engine = new FakeAudioEngine();
        var firstPlayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, playbackId) =>
        {
            if (playbackId == 1)
            {
                firstPlayEntered.SetResult();
                await releaseFirstPlay.Task.ConfigureAwait(false);
            }

            return new AudioPlaybackInfo(engine.Duration);
        };

        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var second = Playlist("Second", "second");

        var firstCommand = controller.PlayNowAsync(first);
        await firstPlayEntered.Task;
        var secondCommand = controller.PlayNowAsync(second);

        Assert.IsFalse(secondCommand.IsCompleted);
        Assert.HasCount(1, engine.PlayRequests);

        releaseFirstPlay.SetResult();
        await Task.WhenAll(firstCommand, secondCommand);

        Assert.AreSame(second, controller.ActivePlaylist);
        Assert.AreSame(second.Tracks[0], controller.CurrentTrack);
        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            engine.PlayRequests.Select(request => request.PlaybackId).ToArray());
    }

    [TestMethod]
    public async Task CompletionQueuedDuringStartupAdvancesToNextTrack()
    {
        var engine = new FakeAudioEngine();
        var firstPlayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPlay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.PlayHandler = async (_, playbackId) =>
        {
            if (playbackId == 1)
            {
                firstPlayEntered.SetResult();
                await releaseFirstPlay.Task.ConfigureAwait(false);
            }

            return new AudioPlaybackInfo(engine.Duration);
        };
        engine.PlaybackIdsEndingOnPlayCompletion.Add(1);

        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "brief", "next");
        var stateChangedCount = 0;
        controller.StateChanged += (_, _) => stateChangedCount++;

        var playCommand = controller.PlayNowAsync(playlist);
        await firstPlayEntered.Task;

        Assert.AreEqual(1L, controller.CurrentPlaybackId);
        Assert.AreSame(playlist.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(playlist, controller.CurrentPlaylist);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(0, stateChangedCount);

        releaseFirstPlay.SetResult();
        await playCommand;
        await engine.GetTrackEndAfterPlayTask(1);

        Assert.AreEqual(2L, controller.CurrentPlaybackId);
        Assert.AreSame(playlist.Tracks[1], controller.CurrentTrack);
        Assert.AreSame(playlist, controller.CurrentPlaylist);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            engine.PlayRequests.Select(request => request.PlaybackId).ToArray());
    }

    [TestMethod]
    public async Task ProgressSnapshotComesFromEngineForCurrentPlayback()
    {
        var engine = new FakeAudioEngine
        {
            Duration = TimeSpan.FromSeconds(90),
            Position = TimeSpan.FromSeconds(12),
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        engine.MasterGain = 0.4f;
        engine.MasterFadeState = MasterFadeState.FadingIn;

        var progress = await controller.GetProgressAsync();

        Assert.AreEqual(controller.CurrentPlaybackId, progress.PlaybackId);
        Assert.AreEqual(TimeSpan.FromSeconds(12), progress.Position);
        Assert.AreEqual(TimeSpan.FromSeconds(90), progress.Duration);
        Assert.AreEqual(0.4f, progress.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, progress.MasterFadeState);
        Assert.AreEqual(TimeSpan.FromSeconds(90), controller.CurrentDuration);
        Assert.AreEqual(0.4f, controller.Snapshot.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.Snapshot.MasterFadeState);
    }

    [TestMethod]
    public async Task ProgressWithMismatchedEnginePlaybackPreservesLogicalIdentityAndCarriesMasterState()
    {
        var engine = new FakeAudioEngine
        {
            Duration = TimeSpan.FromSeconds(90),
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        var logicalPlaybackId = controller.CurrentPlaybackId;
        var logicalDuration = controller.CurrentDuration;

        engine.Position = TimeSpan.FromSeconds(45);
        engine.MasterGain = 0.2f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await engine.PlayAsync(Playlist("Rogue", "rogue").Tracks[0], 999);

        var progress = await controller.GetProgressAsync();

        Assert.AreEqual(logicalPlaybackId, progress.PlaybackId);
        Assert.AreEqual(TimeSpan.Zero, progress.Position);
        Assert.AreEqual(logicalDuration, progress.Duration);
        Assert.AreEqual(0.2f, progress.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, progress.MasterFadeState);
        Assert.AreEqual(0.2f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.MasterFadeState);
    }

    [TestMethod]
    public async Task ProgressWithoutCurrentPlaybackStillMirrorsEngineMasterState()
    {
        var engine = new FakeAudioEngine
        {
            MasterGain = 0.65f,
            MasterFadeState = MasterFadeState.FadingIn,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());

        var progress = await controller.GetProgressAsync();

        Assert.IsNull(progress.PlaybackId);
        Assert.AreEqual(TimeSpan.Zero, progress.Position);
        Assert.AreEqual(TimeSpan.Zero, progress.Duration);
        Assert.AreEqual(0.65f, progress.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, progress.MasterFadeState);
        Assert.AreEqual(0.65f, controller.Snapshot.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.Snapshot.MasterFadeState);
    }

    [TestMethod]
    public async Task ProgressFailureRetainsMirroredMasterState()
    {
        var engine = new FakeAudioEngine
        {
            MasterGain = 0.55f,
            MasterFadeState = MasterFadeState.FadingOut,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.GetProgressAsync();
        engine.MasterGain = 0f;
        engine.MasterFadeState = MasterFadeState.Muted;
        engine.ProgressException = new InvalidOperationException("progress failed");

        var progress = await controller.GetProgressAsync();

        Assert.AreEqual(0.55f, progress.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, progress.MasterFadeState);
        Assert.AreEqual(0.55f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.MasterFadeState);
        StringAssert.Contains(controller.LastError!, "progress");
    }

    [TestMethod]
    public async Task OutputFaultIsReportedAndOnlyStopsMatchingPlayback()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "one", "two");
        await controller.PlayNowAsync(playlist);
        var currentId = controller.CurrentPlaybackId!.Value;
        engine.MasterGain = 0.35f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();
        var errors = new List<PlaybackErrorEventArgs>();
        controller.ErrorOccurred += (_, error) => errors.Add(error);

        await engine.RaiseOutputFaultAsync(new AudioOutputFault("stale", PlaybackId: currentId + 100));
        Assert.IsNotNull(controller.CurrentTrack);
        Assert.AreEqual(0.35f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.MasterFadeState);
        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0].Message, "stale");

        await engine.RaiseOutputFaultAsync(new AudioOutputFault("device lost", PlaybackId: currentId));

        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.IsNull(controller.CurrentTrack);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
        Assert.HasCount(2, errors);
        StringAssert.Contains(errors[1].Message, "device lost");
        StringAssert.Contains(controller.LastError!, "device lost");
    }

    [TestMethod]
    public async Task NullIdOutputFaultIsReportedWithoutStoppingNewerPlayback()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "first");
        var second = Playlist("Second", "second");

        await controller.PlayNowAsync(first);
        await controller.PlayNowAsync(second);
        var currentId = controller.CurrentPlaybackId;
        engine.MasterGain = 0.45f;
        engine.MasterFadeState = MasterFadeState.FadingIn;
        await controller.GetProgressAsync();
        var faultException = new InvalidOperationException("stop failed");
        var errors = new List<PlaybackErrorEventArgs>();
        controller.ErrorOccurred += (_, error) => errors.Add(error);

        await engine.RaiseOutputFaultAsync(new AudioOutputFault(
            "delayed stop failure",
            faultException));

        Assert.AreSame(second.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(second, controller.CurrentPlaylist);
        Assert.AreEqual(currentId, controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(0.45f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.MasterFadeState);
        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0].Message, "delayed stop failure");
        Assert.AreSame(faultException, errors[0].Exception);
    }

    [TestMethod]
    public async Task DisposalResetsMirroredMasterState()
    {
        var engine = new FakeAudioEngine();
        var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        engine.MasterGain = 0.2f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();

        await controller.DisposeAsync();

        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
        Assert.IsTrue(engine.IsDisposed);
    }

    [TestMethod]
    public async Task AmbienceSourcesCoexistAndFadeOutUntilPhysicalRemoval()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\Ambience\Rain.mp3");
        var wind = new LibraryTrack("Wind", @"C:\Library\Ambience\Wind.mp3");

        await controller.PlayAmbienceAsync(rain, 0.4f);
        await controller.PlayAmbienceAsync(wind, 0.7f);
        await controller.SetAmbienceSourceGainAsync(rain.FilePath.ToUpperInvariant(), 0.25f);

        Assert.HasCount(2, controller.Snapshot.AmbienceSnapshots);
        Assert.AreEqual(0.25f, controller.Snapshot.AmbienceSnapshots.Single(
            source => source.FilePath.Equals(rain.FilePath, StringComparison.OrdinalIgnoreCase)).SourceGain);
        Assert.IsTrue(controller.Snapshot.AmbienceSnapshots.All(
            source => source.State == AmbiencePlaybackState.FadingIn));

        await controller.StopAmbienceAsync(rain.FilePath.ToLowerInvariant());
        Assert.AreEqual(AmbiencePlaybackState.FadingOut, controller.Snapshot.AmbienceSnapshots.Single(
            source => source.FilePath.Equals(rain.FilePath, StringComparison.OrdinalIgnoreCase)).State);
        await controller.GetProgressAsync();
        Assert.HasCount(2, controller.Snapshot.AmbienceSnapshots);

        engine.CompleteAmbienceTransition(rain.FilePath);
        await controller.GetProgressAsync();
        Assert.HasCount(1, controller.Snapshot.AmbienceSnapshots);
        Assert.AreEqual(wind.FilePath, controller.Snapshot.AmbienceSnapshots[0].FilePath);
    }

    [TestMethod]
    public async Task AmbienceFailuresLeaveControllerStateUnchanged()
    {
        var engine = new FakeAudioEngine
        {
            PlayAmbienceException = new IOException("open failed"),
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.mp3");

        await controller.PlayAmbienceAsync(rain, 0.5f);
        Assert.IsEmpty(controller.Ambience);

        engine.PlayAmbienceException = null;
        await controller.PlayAmbienceAsync(rain, 0.5f);
        var before = controller.Snapshot.AmbienceSnapshots[0];
        engine.AmbienceGainException = new InvalidOperationException("gain failed");
        await controller.SetAmbienceSourceGainAsync(rain.FilePath, 0.2f);
        Assert.AreEqual(before.SourceGain, controller.Snapshot.AmbienceSnapshots[0].SourceGain);

        engine.AmbienceGainException = null;
        engine.StopAmbienceException = new InvalidOperationException("stop failed");
        await controller.StopAmbienceAsync(rain);
        Assert.AreEqual(AmbiencePlaybackState.FadingIn, controller.Snapshot.AmbienceSnapshots[0].State);
        StringAssert.Contains(controller.LastError!, "stop ambience");
    }

    [TestMethod]
    public async Task VolumesAndStopAllPreserveConfiguredLevelsWhileClearingAmbience()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.mp3");

        await controller.SetMusicVolumeAsync(0.2f);
        await controller.SetAmbienceVolumeAsync(0.3f);
        await controller.SetMasterVolumeAsync(0.4f);
        await controller.PlayAmbienceAsync(rain, 0.8f);
        await controller.PlayNowAsync(active);
        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
        await controller.StopAllAsync();

        Assert.AreEqual(0.2f, controller.MusicVolume);
        Assert.AreEqual(0.3f, controller.AmbienceVolume);
        Assert.AreEqual(0.4f, controller.MasterVolume);
        Assert.IsEmpty(controller.Ambience);
        Assert.AreEqual(1, engine.StopCount);
        Assert.AreEqual(0, engine.StopMusicCount);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);
    }

    [TestMethod]
    public async Task SkipAndNaturalCompletionStopOnlyMusicAndKeepAmbience()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "one", "two");
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.mp3");

        await controller.PlayAmbienceAsync(rain, 0.5f);
        await controller.PlayNowAsync(playlist);
        await controller.SkipAsync();
        Assert.AreEqual(1, engine.StopMusicCount);
        Assert.HasCount(1, controller.Ambience);

        await engine.RaiseTrackEndedAsync(controller.CurrentPlaybackId!.Value);
        Assert.AreEqual(2, engine.StopMusicCount);
        Assert.HasCount(1, controller.Ambience);
    }

    [TestMethod]
    public async Task AmbienceOnlyOutputFaultReconcilesPhysicalEngineState()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.mp3");
        await controller.PlayAmbienceAsync(rain, 0.5f);

        await engine.RaiseOutputFaultAsync(new AudioOutputFault("device lost"));

        Assert.IsEmpty(controller.Ambience);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        StringAssert.Contains(controller.LastError!, "device lost");
    }

    [TestMethod]
    public async Task ExactPlayNowUsesExplicitPlaylistAndReturnsThroughQueueToActivePlaylist()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active-one", "active-two");
        var exactSource = Playlist("Exact Source", "shared", "exact-two");
        var pending = Playlist("Pending", "pending");
        var queued = Playlist("Queued", "queued");

        await controller.PlayNowAsync(active);
        await controller.AfterCurrentAsync(pending);
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        await controller.PlayNowAsync(
            exactSource.Tracks[0],
            exactSource,
            ImmediateTransitionMode.Crossfade,
            TimeSpan.FromSeconds(7));

        Assert.AreSame(exactSource, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.AreSame(exactSource.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(exactSource, controller.CurrentPlaylist);
        Assert.HasCount(1, controller.Queue);
        Assert.AreSame(queued.Tracks[0], controller.Queue[0].Track);
        Assert.AreEqual(ImmediateTransitionMode.Crossfade, engine.PlayRequests[^1].TransitionMode);

        await CompleteCurrentAsync(controller, engine);
        Assert.AreSame(queued.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(exactSource, controller.ActivePlaylist);

        await CompleteCurrentAsync(controller, engine);
        Assert.IsTrue(exactSource.Tracks.Contains(controller.CurrentTrack));
        Assert.AreSame(exactSource, controller.CurrentPlaylist);
    }

    [TestMethod]
    public async Task ExactPlayNowDoesNotInferSameNamedTrackFromAnotherPlaylist()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "shared");
        var second = Playlist("Second", "shared");

        await controller.PlayNowAsync(second.Tracks[0], second);

        Assert.AreSame(second.Tracks[0], controller.CurrentTrack);
        Assert.AreSame(second, controller.CurrentPlaylist);
        Assert.AreSame(second, controller.ActivePlaylist);
        Assert.AreSame(second.Tracks[0], engine.PlayRequests[^1].Track);
        Assert.AreNotSame(first.Tracks[0], engine.PlayRequests[^1].Track);
    }

    [TestMethod]
    public async Task FailedAmbienceOpenReconcilesIndependentMusicAndAmbienceFadeState()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var active = Playlist("Active", "active");
        var ambience = new LibraryTrack("Rain", @"C:\Library\rain.wav");
        var failedAmbience = new LibraryTrack("Failed", @"C:\Library\failed.wav");

        await controller.PlayNowAsync(active);
        await controller.PlayAmbienceAsync(ambience, 0.5f);
        engine.MusicFadeGain = 0.35f;
        engine.MusicFadeState = MasterFadeState.FadingOut;
        engine.AmbienceFadeGain = 0.65f;
        engine.AmbienceFadeState = MasterFadeState.FadingIn;
        engine.PlayAmbienceException = new IOException("ambience open failed");

        await controller.PlayAmbienceAsync(failedAmbience, 0.5f);

        Assert.AreSame(active.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(0.35f, controller.Snapshot.MusicFadeGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.Snapshot.MusicFadeState);
        Assert.AreEqual(0.65f, controller.Snapshot.AmbienceFadeGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.Snapshot.AmbienceFadeState);
    }

    [TestMethod]
    public async Task ExactPlayNowFailureClearsReplacementAndPreservesQueueAndIgnoresStaleCompletion()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var oldPlaylist = Playlist("Old", "old");
        var replacement = Playlist("Replacement", "replacement");
        var queued = Playlist("Queued", "queued");
        await controller.PlayNowAsync(oldPlaylist);
        var oldId = controller.CurrentPlaybackId!.Value;
        await controller.QueueTrackAsync(queued.Tracks[0], queued);
        engine.UnreadablePaths.Add(replacement.Tracks[0].FilePath);

        await controller.PlayNowAsync(
            replacement.Tracks[0],
            replacement,
            ImmediateTransitionMode.HardCut);
        await engine.RaiseTrackEndedAsync(oldId);

        Assert.AreSame(replacement, controller.ActivePlaylist);
        Assert.IsNull(controller.PendingPlaylist);
        Assert.IsNull(controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.HasCount(1, controller.Queue);
        Assert.AreEqual(1, engine.StopMusicCount);
        Assert.HasCount(2, engine.PlayRequests);
    }

    [TestMethod]
    public async Task PlaybackStartsFromStoppedWithSelectedAutomaticMasterFade()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "active");
        var duration = TimeSpan.FromSeconds(9);

        await controller.PlayNowAsync(playlist, ImmediateTransitionMode.HardCut, duration);

        Assert.HasCount(2, engine.FadeRequests);
        Assert.AreEqual(MasterFadeDirection.Out, engine.FadeRequests[0].Direction);
        Assert.AreEqual(TimeSpan.Zero, engine.FadeRequests[0].FullScaleDuration);
        Assert.AreEqual(MasterFadeDirection.In, engine.FadeRequests[1].Direction);
        Assert.AreEqual(duration, engine.FadeRequests[1].FullScaleDuration);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingIn, controller.MasterFadeState);
    }

    [TestMethod]
    public async Task ExactAndAmbienceStartsAlsoUseAutomaticMasterFade()
    {
        var exactEngine = new FakeAudioEngine();
        await using var exactController = new PlaybackController(exactEngine, new ZeroRandom());
        var exactPlaylist = Playlist("Exact", "exact");
        await exactController.PlayNowAsync(
            exactPlaylist.Tracks[0],
            exactPlaylist,
            ImmediateTransitionMode.HardCut,
            TimeSpan.FromSeconds(6));
        Assert.AreEqual(MasterFadeDirection.In, exactEngine.FadeRequests[^1].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(6), exactEngine.FadeRequests[^1].FullScaleDuration);

        var ambienceEngine = new FakeAudioEngine();
        await using var ambienceController = new PlaybackController(ambienceEngine, new ZeroRandom());
        await ambienceController.PlayAmbienceAsync(
            new LibraryTrack("Rain", @"C:\Library\rain.wav"),
            0.5f,
            TimeSpan.FromSeconds(8));
        Assert.AreEqual(MasterFadeDirection.Out, ambienceEngine.FadeRequests[0].Direction);
        Assert.AreEqual(MasterFadeDirection.In, ambienceEngine.FadeRequests[^1].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(8), ambienceEngine.FadeRequests[^1].FullScaleDuration);
    }

    [TestMethod]
    public async Task ResumeReplacementNaturalNextAndOtherActiveSourceDoNotAutoFade()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var first = Playlist("First", "one", "two");
        var second = Playlist("Second", "replacement");
        var ambience = new LibraryTrack("Rain", @"C:\Library\rain.wav");

        await controller.PlayNowAsync(first);
        var initialFadeCount = engine.FadeRequests.Count;
        await controller.PauseAsync();
        await controller.ResumeAsync();
        await controller.PlayNowAsync(second, ImmediateTransitionMode.Crossfade);
        await controller.PlayAmbienceAsync(ambience, 0.5f);
        Assert.HasCount(initialFadeCount, engine.FadeRequests);

        await engine.RaiseTrackEndedAsync(controller.CurrentPlaybackId!.Value);
        Assert.HasCount(initialFadeCount, engine.FadeRequests);
    }

    [TestMethod]
    public async Task StopAllWaitsForMasterFadeThenStopsAndRemainsMuted()
    {
        var engine = new FakeAudioEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FadeHandler = async (direction, duration) =>
        {
            if (direction == MasterFadeDirection.Out && duration > TimeSpan.Zero)
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        engine.MasterGain = 1f;
        await controller.GetProgressAsync();

        var stopTask = controller.StopAllAsync(TimeSpan.FromSeconds(4));
        await entered.Task;
        Assert.AreEqual(0, engine.StopCount);
        Assert.IsNull(controller.CurrentTrack);

        release.SetResult();
        await stopTask;
        Assert.AreEqual(1, engine.StopCount);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);
        Assert.AreEqual(0f, engine.MasterGain);
    }

    [TestMethod]
    public async Task RepeatedStopAllCommandsDoNotStackOrStopTwice()
    {
        var engine = new FakeAudioEngine();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FadeHandler = async (direction, duration) =>
        {
            if (direction == MasterFadeDirection.Out && duration > TimeSpan.Zero)
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            }
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        engine.MasterGain = 1f;
        await controller.GetProgressAsync();

        var first = controller.StopAllAsync(TimeSpan.FromSeconds(3));
        await entered.Task;
        var second = controller.StopAllAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, engine.StopCount);
        Assert.HasCount(1, engine.FadeRequests.Where(request =>
            request.Direction == MasterFadeDirection.Out &&
            request.FullScaleDuration == TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public async Task StopAllDropsPausedMusicImmediatelyWithoutResuming()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        await controller.PauseAsync();
        var resumeCount = engine.ResumeCount;

        await controller.StopAllAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(resumeCount, engine.ResumeCount);
        Assert.IsNull(controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(0, engine.FadeRequests.Count(request =>
            request.Direction == MasterFadeDirection.Out &&
            request.FullScaleDuration == TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, engine.StopCount);
    }

    [TestMethod]
    public async Task StopAllFadesWhenPausedMusicAndAmbienceRemain()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        await controller.PauseAsync();
        await controller.PlayAmbienceAsync(new LibraryTrack("Rain", @"C:\Library\rain.wav"), 0.5f);
        engine.MasterGain = 0.75f;
        await controller.GetProgressAsync();

        await controller.StopAllAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, engine.FadeRequests.Count(request =>
            request.Direction == MasterFadeDirection.Out &&
            request.FullScaleDuration == TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.IsEmpty(controller.Ambience);
        Assert.IsNull(controller.CurrentTrack);
    }

    [TestMethod]
    public async Task StopAllFadeFailureReportsErrorStopsSourcesAndLeavesMutedState()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var errors = new List<PlaybackErrorEventArgs>();
        controller.ErrorOccurred += (_, error) => errors.Add(error);
        await controller.PlayNowAsync(Playlist("Active", "active"));
        engine.MasterGain = 1f;
        await controller.GetProgressAsync();
        engine.FadeException = new InvalidOperationException("fade output failed");

        await controller.StopAllAsync(TimeSpan.FromSeconds(4));

        Assert.IsNotEmpty(errors);
        Assert.IsNull(controller.CurrentTrack);
        Assert.AreEqual(PlaybackState.Stopped, controller.State);
        Assert.AreEqual(0f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Muted, controller.MasterFadeState);
        Assert.AreEqual(1, engine.StopCount);
    }

    [TestMethod]
    public async Task MusicAndAmbienceFadeBusesRemainIndependentInSnapshot()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));
        await controller.FadeMusicAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(3));
        await controller.PlayAmbienceAsync(new LibraryTrack("Rain", @"C:\Library\rain.wav"), 0.5f);
        await controller.FadeAmbienceAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(4));

        engine.MusicFadeGain = 0.4f;
        engine.MusicFadeState = MasterFadeState.FadingOut;
        engine.AmbienceFadeGain = 0.7f;
        engine.AmbienceFadeState = MasterFadeState.FadingOut;
        var snapshot = (await controller.GetProgressAsync());

        Assert.AreEqual(0.4f, snapshot.MusicFadeGain);
        Assert.AreEqual(MasterFadeState.FadingOut, snapshot.MusicFadeState);
        Assert.AreEqual(0.7f, snapshot.AmbienceFadeGain);
        Assert.AreEqual(MasterFadeState.FadingOut, snapshot.AmbienceFadeState);
        Assert.AreEqual(0.4f, controller.Snapshot.MusicFadeGain);
        Assert.AreEqual(0.7f, controller.Snapshot.AmbienceFadeGain);
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_StopsAbsentRetargetsSharedAndStartsNewSources()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.wav");
        var wind = new LibraryTrack("Wind", @"C:\Library\wind.wav");
        var firePath = @"C:\Library\fire.wav";
        await controller.PlayAmbienceAsync(rain, 0.4f);
        await controller.PlayAmbienceAsync(wind, 0.6f);

        var result = await controller.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(wind.FilePath, 0.25f),
            new AmbiencePresetTarget(firePath, 0.8f),
        ]);

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(3, engine.AmbiencePlayRequests);
        Assert.AreEqual(Path.GetFullPath(rain.FilePath), engine.StopAmbienceRequests[^1]);
        Assert.AreEqual(Path.GetFullPath(wind.FilePath), engine.AmbienceGainRequests[^1].FilePath);
        Assert.AreEqual(0.25f, controller.Ambience.Single(
            source => source.FilePath.Equals(wind.FilePath, StringComparison.OrdinalIgnoreCase)).SourceGain);
        Assert.IsTrue(controller.Ambience.Any(
            source => source.FilePath.Equals(firePath, StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(AmbiencePlaybackState.FadingOut, controller.Ambience.Single(
            source => source.FilePath.Equals(rain.FilePath, StringComparison.OrdinalIgnoreCase)).State);
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_EmptySetFadesEveryTargetOnSourceOut()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.wav");
        var wind = new LibraryTrack("Wind", @"C:\Library\wind.wav");
        await controller.PlayAmbienceAsync(rain, 0.4f);
        await controller.PlayAmbienceAsync(wind, 0.5f);

        var result = await controller.ApplyAmbiencePresetAsync(Array.Empty<AmbiencePresetTarget>());

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEquivalent(
            new[] { Path.GetFullPath(rain.FilePath), Path.GetFullPath(wind.FilePath) },
            engine.StopAmbienceRequests.ToArray());
        Assert.IsTrue(controller.Ambience.All(source => source.State == AmbiencePlaybackState.FadingOut));
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_ReversesFadeOutWithoutOverlappingCopy()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var rain = new LibraryTrack("Rain", @"C:\Library\rain.wav");
        await controller.PlayAmbienceAsync(rain, 0.4f);
        await controller.StopAmbienceAsync(rain);

        var result = await controller.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(rain.FilePath, 0.7f),
        ]);

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(2, engine.AmbiencePlayRequests);
        Assert.HasCount(1, controller.Ambience);
        Assert.IsEmpty(engine.AmbienceGainRequests);
        Assert.AreEqual(AmbiencePlaybackState.FadingIn, controller.Ambience[0].State);
        Assert.AreEqual(0.7f, controller.Ambience[0].SourceGain);
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_FromStoppedFadesMasterInOnceAndNotForExistingMusic()
    {
        var stoppedEngine = new FakeAudioEngine();
        await using var stoppedController = new PlaybackController(stoppedEngine, new ZeroRandom());
        var duration = TimeSpan.FromSeconds(8);
        await stoppedController.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(@"C:\Library\rain.wav", 0.4f),
            new AmbiencePresetTarget(@"C:\Library\wind.wav", 0.5f),
        ], duration);

        Assert.HasCount(2, stoppedEngine.FadeRequests);
        Assert.AreEqual(MasterFadeDirection.Out, stoppedEngine.FadeRequests[0].Direction);
        Assert.AreEqual(MasterFadeDirection.In, stoppedEngine.FadeRequests[1].Direction);
        Assert.AreEqual(duration, stoppedEngine.FadeRequests[1].FullScaleDuration);

        var activeEngine = new FakeAudioEngine();
        await using var activeController = new PlaybackController(activeEngine, new ZeroRandom());
        await activeController.PlayNowAsync(Playlist("Active", "active"));
        await activeController.PauseAsync();
        var fadeCount = activeEngine.FadeRequests.Count;
        await activeController.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(@"C:\Library\rain.wav", 0.4f),
        ], duration);

        Assert.HasCount(fadeCount, activeEngine.FadeRequests);
        Assert.AreEqual(PlaybackState.Paused, activeController.State);
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_ValidatesAndDeduplicatesBeforeMutation()
    {
        var engine = new FakeAudioEngine();
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var path = @"C:\Library\rain.wav";

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            controller.ApplyAmbiencePresetAsync([
                new AmbiencePresetTarget(path, 0.2f),
                new AmbiencePresetTarget(@"C:\Library\invalid.wav", float.NaN),
            ]));
        Assert.IsEmpty(engine.AmbiencePlayRequests);

        var result = await controller.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(path.ToUpperInvariant(), 0.2f),
            new AmbiencePresetTarget(path, 0.8f),
        ]);

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(1, engine.AmbiencePlayRequests);
        Assert.AreEqual(0.8f, engine.AmbiencePlayRequests[0].SourceGain);
    }

    [TestMethod]
    public async Task ApplyAmbiencePreset_ContinuesAfterPerSourceFailureAndNotifiesOnce()
    {
        var engine = new FakeAudioEngine();
        var missingPath = @"C:\Library\missing.wav";
        engine.UnreadablePaths.Add(missingPath);
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var notificationCount = 0;
        PlaybackSnapshot? notifiedSnapshot = null;
        controller.StateChanged += (_, _) => notificationCount++;
        controller.StateChanged += (_, _) => notifiedSnapshot = controller.Snapshot;

        var result = await controller.ApplyAmbiencePresetAsync([
            new AmbiencePresetTarget(missingPath, 0.2f),
            new AmbiencePresetTarget(@"C:\Library\working.wav", 0.8f),
        ]);

        Assert.AreEqual(1, result.SucceededCount);
        Assert.HasCount(1, result.Failures);
        Assert.AreEqual(1, notificationCount);
        Assert.IsNotNull(notifiedSnapshot);
        Assert.HasCount(1, notifiedSnapshot!.AmbienceSnapshots);
        Assert.IsTrue(controller.Ambience.Any(source =>
            source.FilePath.Equals(@"C:\Library\working.wav", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task CompleteCurrentAsync(
        PlaybackController controller,
        FakeAudioEngine engine)
    {
        await engine.RaiseTrackEndedAsync(controller.CurrentPlaybackId!.Value);
    }

    private static LibraryPlaylist Playlist(string name, params string[] trackNames)
    {
        var directoryPath = $@"C:\Library\{name}";
        var tracks = trackNames
            .Select(trackName => new LibraryTrack(trackName, $@"{directoryPath}\{trackName}.mp3"));
        return new LibraryPlaylist(name, directoryPath, tracks);
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private sealed class CallbackRandom : Random
    {
        private int _callCount;

        public Action<int>? BeforeNext { get; set; }

        public override int Next(int maxValue)
        {
            _callCount++;
            BeforeNext?.Invoke(_callCount);
            return 0;
        }
    }
}
