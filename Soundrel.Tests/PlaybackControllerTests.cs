using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class PlaybackControllerTests
{
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

        Assert.HasCount(1, engine.FadeRequests);
        Assert.AreEqual(MasterFadeDirection.Out, engine.FadeRequests[0].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(4), engine.FadeRequests[0].FullScaleDuration);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.FadingOut, controller.MasterFadeState);
        Assert.AreEqual(PlaybackState.Playing, controller.State);

        engine.MasterGain = 0.25f;
        engine.MasterFadeState = MasterFadeState.FadingOut;
        await controller.GetProgressAsync();
        await controller.PauseAsync();
        await controller.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.FromSeconds(6));

        Assert.HasCount(2, engine.FadeRequests);
        Assert.AreEqual(MasterFadeDirection.In, engine.FadeRequests[1].Direction);
        Assert.AreEqual(TimeSpan.FromSeconds(6), engine.FadeRequests[1].FullScaleDuration);
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
        Assert.HasCount(2, engine.FadeRequests);
    }

    [TestMethod]
    public async Task FadeMaster_EngineFailureReportsErrorWithoutClearingCurrentPlayback()
    {
        var fadeException = new InvalidOperationException("fade failed");
        var engine = new FakeAudioEngine
        {
            FadeException = fadeException,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        var playlist = Playlist("Active", "active");
        var errors = new List<PlaybackErrorEventArgs>();
        controller.ErrorOccurred += (_, error) => errors.Add(error);
        await controller.PlayNowAsync(playlist);
        var playbackId = controller.CurrentPlaybackId;

        await controller.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(3));

        Assert.AreSame(playlist.Tracks[0], controller.CurrentTrack);
        Assert.AreEqual(playbackId, controller.CurrentPlaybackId);
        Assert.AreEqual(PlaybackState.Playing, controller.State);
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
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
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
        Assert.AreEqual(1f, controller.Snapshot.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.Snapshot.MasterFadeState);
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
            MasterGain = 0.4f,
            MasterFadeState = MasterFadeState.FadingIn,
        };
        await using var controller = new PlaybackController(engine, new ZeroRandom());
        await controller.PlayNowAsync(Playlist("Active", "active"));

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
        Assert.AreEqual(1f, controller.MasterGain);
        Assert.AreEqual(MasterFadeState.Full, controller.MasterFadeState);
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
