using NAudio.Wave;
using Soundrel.Models;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class AudioEngineTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task WavSource_StreamsWithWaveFileReaderAndSignalsEndOnce()
    {
        var endCount = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = AudioTrackSource.Open(
            Fixture("mono-22050-tone.wav"),
            11,
            _ =>
            {
                Interlocked.Increment(ref endCount);
                ended.TrySetResult();
            });

        Assert.AreEqual(typeof(WaveFileReader), source.ReaderType);
        Assert.AreEqual(22_050, source.SourceWaveFormat.SampleRate);
        Assert.AreEqual(1, source.SourceWaveFormat.Channels);
        AssertNormalized(source.WaveFormat);
        Assert.IsTrue(source.Duration > TimeSpan.Zero);

        source.Activate();
        var samples = ReadToEnd(source);
        source.Read(new float[32]);
        await ended.Task.WaitAsync(TestTimeout);
        await Task.Delay(50);

        Assert.IsGreaterThan(0, samples);
        Assert.AreEqual(1, endCount);
    }

    [TestMethod]
    public async Task Mp3Source_StreamsThroughNLayerAndSignalsEndOnce()
    {
        var endCount = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var source = AudioTrackSource.Open(
            Fixture("silent-12-frame.mp3"),
            12,
            _ =>
            {
                Interlocked.Increment(ref endCount);
                ended.TrySetResult();
            });

        Assert.AreEqual(typeof(Mp3FileReaderBase), source.ReaderType);
        AssertNormalized(source.WaveFormat);
        Assert.IsTrue(source.Duration > TimeSpan.Zero);

        source.Activate();
        var samples = ReadToEnd(source);
        source.Read(new float[32]);
        await ended.Task.WaitAsync(TestTimeout);
        await Task.Delay(50);

        Assert.IsGreaterThan(0, samples);
        Assert.AreEqual(1, endCount);
    }

    [TestMethod]
    public void MonoWav_IsDuplicatedToStereoAndResampled()
    {
        using var source = AudioTrackSource.Open(
            Fixture("mono-22050-tone.wav"),
            13,
            _ => { });
        var buffer = new float[4096];
        var read = source.Read(buffer);

        AssertNormalized(source.WaveFormat);
        Assert.IsGreaterThan(0, read);
        for (var index = 0; index + 1 < read; index += 2)
        {
            Assert.AreEqual(buffer[index], buffer[index + 1], 0.000001f);
        }

        Assert.IsTrue(buffer.Take(read).Any(sample => Math.Abs(sample) > 0.001f));
    }

    [TestMethod]
    public void FloatStereoWav_IsAcceptedAndResampled()
    {
        var path = TemporaryPath(".wav");
        try
        {
            var samples = Enumerable.Range(0, 4_410)
                .Select(index => (float)Math.Sin(2 * Math.PI * 220 * index / 44_100))
                .SelectMany(sample => new[] { sample, -sample })
                .ToArray();
            using (var writer = new WaveFileWriter(
                path,
                WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2)))
            {
                writer.WriteSamples(samples, 0, samples.Length);
            }

            using var source = AudioTrackSource.Open(path, 14, _ => { });
            var buffer = new float[4096];

            Assert.AreEqual(WaveFormatEncoding.IeeeFloat, source.SourceWaveFormat.Encoding);
            AssertNormalized(source.WaveFormat);
            Assert.IsGreaterThan(0, source.Read(buffer));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Source_RejectsUnsupportedExtensionAndCorruptInput()
    {
        var unsupported = Assert.Throws<NotSupportedException>(() =>
            AudioTrackSource.Open(Fixture("README.md"), 15, _ => { }));
        StringAssert.Contains(unsupported.Message, "unsupported extension");

        var corruptPath = TemporaryPath(".wav");
        try
        {
            File.WriteAllBytes(corruptPath, [1, 2, 3, 4, 5]);
            Assert.Throws<InvalidDataException>(() =>
                AudioTrackSource.Open(corruptPath, 16, _ => { }));
        }
        finally
        {
            File.Delete(corruptPath);
        }
    }

    [TestMethod]
    public void Source_RejectsUnsupportedChannelLayoutAndEncoding()
    {
        var channelPath = TemporaryPath(".wav");
        var encodingPath = TemporaryPath(".wav");
        try
        {
            using (var writer = new WaveFileWriter(channelPath, new WaveFormat(48_000, 16, 3)))
            {
                writer.Write(new byte[48_000 * 3 * 2 / 100]);
            }

            var exception = Assert.Throws<NotSupportedException>(() =>
                AudioTrackSource.Open(channelPath, 17, _ => { }));
            StringAssert.Contains(exception.Message, "3 channels");

            using (var writer = new WaveFileWriter(
                encodingPath,
                WaveFormat.CreateALawFormat(8_000, 1)))
            {
                writer.Write(new byte[800]);
            }

            exception = Assert.Throws<NotSupportedException>(() =>
                AudioTrackSource.Open(encodingPath, 18, _ => { }));
            StringAssert.Contains(exception.Message, "unsupported format");
        }
        finally
        {
            File.Delete(channelPath);
            File.Delete(encodingPath);
        }
    }

    [TestMethod]
    public async Task Play_HardReplacesSourceWithoutCompletingRemovedPlayback()
    {
        var firstPath = CopyFixtureToTemporaryFile("mono-22050-tone.wav");
        var output = new FakeWavePlayer();
        var outputCount = 0;
        await using var engine = new AudioEngine(() =>
        {
            outputCount++;
            return output;
        });
        var endedIds = new List<long>();
        engine.TrackEnded += notification =>
        {
            lock (endedIds)
            {
                endedIds.Add(notification.PlaybackId);
            }

            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 21);
            await engine.PlayAsync(Track(Fixture("silent-12-frame.mp3")), 22);
            await Task.Delay(50);

            var progress = await engine.GetProgressAsync();
            Assert.AreEqual(22L, progress.PlaybackId);
            Assert.AreEqual(1, outputCount);
            Assert.AreEqual(1, output.InitCount);
            Assert.AreEqual(1, output.PlayCount);
            Assert.AreEqual(0, output.StopCount);
            lock (endedIds)
            {
                CollectionAssert.DoesNotContain(endedIds, 21L);
            }

            using var released = File.Open(firstPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            File.Delete(firstPath);
        }
    }

    [TestMethod]
    public async Task Stop_SuppressesCompletionAndClearsProgress()
    {
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        await engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 31);
        await engine.StopAsync();
        output.PumpFrames(48_000);
        await Task.Delay(50);

        Assert.AreEqual(0, endCount);
        Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
        Assert.AreEqual(1, output.StopCount);
    }

    [TestMethod]
    public async Task NaturalEnd_NotifiesOnceAfterPlayCompletes()
    {
        var output = new FakeWavePlayer
        {
            PumpToEndOnPlay = true,
        };
        await using var engine = new AudioEngine(() => output);
        var endCount = 0;
        var ended = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AudioPlaybackInfo>? playTask = null;
        var playWasCompleteAtNotification = false;
        engine.TrackEnded += notification =>
        {
            playWasCompleteAtNotification = playTask?.IsCompletedSuccessfully == true;
            Interlocked.Increment(ref endCount);
            ended.TrySetResult(notification.PlaybackId);
            return Task.CompletedTask;
        };

        playTask = engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 41);
        var info = await playTask;
        var endedId = await ended.Task.WaitAsync(TestTimeout);
        output.PumpFrames(48_000);
        await Task.Delay(50);

        Assert.IsTrue(info.Duration > TimeSpan.Zero);
        Assert.AreEqual(41L, endedId);
        Assert.AreEqual(1, endCount);
        Assert.IsTrue(playWasCompleteAtNotification);
    }

    [TestMethod]
    public async Task Progress_UsesNormalizedSamplesAndDoesNotAdvanceWhilePaused()
    {
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);
        var info = await engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 51);

        output.PumpFrames(2_400);
        var playing = await engine.GetProgressAsync();
        Assert.AreEqual(51L, playing.PlaybackId);
        Assert.AreEqual(info.Duration, playing.Duration);
        Assert.IsTrue(playing.Position > TimeSpan.Zero);

        await engine.PauseAsync();
        output.PumpFrames(2_400);
        var paused = await engine.GetProgressAsync();
        Assert.AreEqual(playing.Position, paused.Position);

        await engine.ResumeAsync();
        output.PumpFrames(2_400);
        var resumed = await engine.GetProgressAsync();
        Assert.IsTrue(resumed.Position > paused.Position);
    }

    [TestMethod]
    public async Task Output_IsCreatedAndInitializedOnceAcrossStopAndReplay()
    {
        var output = new FakeWavePlayer();
        var createCount = 0;
        await using var engine = new AudioEngine(() =>
        {
            createCount++;
            return output;
        });

        Assert.AreEqual(0, createCount);
        await engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 61);
        await engine.StopAsync();
        await engine.PlayAsync(Track(Fixture("silent-12-frame.mp3")), 62);

        Assert.AreEqual(1, createCount);
        Assert.AreEqual(1, output.InitCount);
        Assert.AreEqual(2, output.PlayCount);
        AssertNormalized(output.OutputWaveFormat);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotentAndReleasesOpenFile()
    {
        var path = CopyFixtureToTemporaryFile("mono-22050-tone.wav");
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);

        try
        {
            await engine.PlayAsync(Track(path), 71);
            await engine.DisposeAsync();
            await engine.DisposeAsync();

            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(1, output.StopCount);
            using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PlaybackStoppedFault_IsForwardedOffCallbackAndReleasesSource()
    {
        var path = CopyFixtureToTemporaryFile("mono-22050-tone.wav");
        var output = new FakeWavePlayer();
        var replacementOutput = new FakeWavePlayer();
        var createCount = 0;
        await using var engine = new AudioEngine(() =>
            ++createCount == 1 ? output : replacementOutput);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var invokedInsideOutputCallback = false;
        engine.OutputFaulted += fault =>
        {
            invokedInsideOutputCallback = FakeWavePlayer.IsRaisingFault;
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(path), 81);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            output.RaiseFault(new InvalidOperationException("device lost"));
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.AreEqual(81L, fault.PlaybackId);
            StringAssert.Contains(fault.Message, "device lost");
            Assert.IsFalse(invokedInsideOutputCallback);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
            Assert.AreEqual(1, output.DisposeCount);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }

            await engine.PlayAsync(Track(path), 82);
            Assert.AreEqual(2, createCount);
            Assert.AreEqual(1, replacementOutput.InitCount);
            Assert.AreEqual(1f, (await engine.GetProgressAsync()).MasterGain);
            Assert.IsTrue(replacementOutput.ReadFrames(100)
                .Any(sample => Math.Abs(sample) > 0.001f));
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task StalePlaybackFault_DoesNotDetachSuccessfulNewerRun()
    {
        var firstPath = CreateConstantWaveFile(3, 0.2f);
        var secondPath = CreateConstantWaveFile(3, 0.4f);
        var output = new FakeWavePlayer { RaiseFaultChangesPlaybackState = false };
        var replacementOutput = new FakeWavePlayer();
        var outputCount = 0;
        var secondOpenStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowSecondOpen = new ManualResetEventSlim();
        var engine = new AudioEngine(
            () => ++outputCount == 1 ? output : replacementOutput,
            (path, playbackId, ended) =>
            {
                if (playbackId == 84)
                {
                    secondOpenStarted.TrySetResult();
                    if (!allowSecondOpen.Wait(TestTimeout))
                    {
                        throw new TimeoutException("The replacement source was not released.");
                    }
                }

                return AudioTrackSource.Open(path, playbackId, ended);
            });
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 83);
            var replacement = engine.PlayAsync(Track(secondPath), 84);
            await secondOpenStarted.Task.WaitAsync(TestTimeout);

            output.RaiseFault(new InvalidOperationException("stale device stop"));
            allowSecondOpen.Set();
            await replacement;
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.AreEqual(83L, fault.PlaybackId);
            Assert.AreEqual(84L, engine.CurrentPlaybackId);
            Assert.AreEqual(1, engine.MusicInputCount);
            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(NAudio.Wave.PlaybackState.Playing, replacementOutput.PlaybackState);
            Assert.AreEqual(0.4f, replacementOutput.ReadFrames(1)[0], 0.000001f);
            AssertFileReleased(firstPath);
        }
        finally
        {
            allowSecondOpen.Set();
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task PlaybackStoppedWithNoCurrentSlot_RetiresOutputAndAllowsReplay()
    {
        var shortPath = CreateConstantWaveFile(0.01);
        var replayPath = CreateConstantWaveFile(1);
        var output = new FakeWavePlayer();
        var replacementOutput = new FakeWavePlayer();
        var createCount = 0;
        var engine = new AudioEngine(() =>
            ++createCount == 1 ? output : replacementOutput);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(shortPath), 85);
            output.PumpFrames(1_000);
            await WaitUntilAsync(() => engine.CurrentPlaybackId is null);

            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            output.RaiseFault(new InvalidOperationException("silence consumer failed"));
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.IsNull(fault.PlaybackId);
            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());

            await engine.PlayAsync(Track(replayPath), 86);
            Assert.AreEqual(2, createCount);
            Assert.AreEqual(1, replacementOutput.InitCount);
            Assert.AreEqual(0.25f, replacementOutput.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(shortPath);
            File.Delete(replayPath);
        }
    }

    [TestMethod]
    public async Task StartFailure_IsForwardedAndDoesNotLeaveCurrentPlayback()
    {
        var outputException = new InvalidOperationException("cannot start device");
        var output = new FakeWavePlayer
        {
            PlayException = outputException,
        };
        await using var engine = new AudioEngine(() => output);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 91));
        var fault = await faulted.Task.WaitAsync(TestTimeout);

        Assert.AreSame(outputException, thrown);
        Assert.AreEqual(91L, fault.PlaybackId);
        Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
    }

    [TestMethod]
    public async Task PlaybackStoppedDuringStart_IsSurfacedAndFreshOutputCanReplay()
    {
        var path = CreateConstantWaveFile(1);
        var callbackException = new InvalidOperationException("start callback failed");
        var output = new FakeWavePlayer
        {
            PlaybackStoppedExceptionOnPlay = callbackException,
        };
        var replacementOutput = new FakeWavePlayer();
        var createCount = 0;
        var engine = new AudioEngine(() =>
            ++createCount == 1 ? output : replacementOutput);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.PlayAsync(Track(path), 92));
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.AreSame(callbackException, thrown);
            Assert.AreSame(callbackException, fault.Exception);
            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());

            await engine.PlayAsync(Track(path), 93);
            Assert.AreEqual(2, createCount);
            Assert.AreEqual(1, replacementOutput.InitCount);
            Assert.AreEqual(0.25f, replacementOutput.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PauseFailure_ResetsMasterAndFreshOutputCanReplay()
    {
        var path = CreateConstantWaveFile(1);
        var output = new FakeWavePlayer();
        var replacementOutput = new FakeWavePlayer();
        var createCount = 0;
        var engine = new AudioEngine(() =>
            ++createCount == 1 ? output : replacementOutput);
        try
        {
            await engine.PlayAsync(Track(path), 94);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            output.PauseException = new InvalidOperationException("pause failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PauseAsync());

            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
            await engine.PlayAsync(Track(path), 95);
            Assert.AreEqual(2, createCount);
            Assert.AreEqual(0.25f, replacementOutput.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task ResumeFailure_ResetsMasterAndFreshOutputCanReplay()
    {
        var path = CreateConstantWaveFile(1);
        var output = new FakeWavePlayer();
        var replacementOutput = new FakeWavePlayer();
        var createCount = 0;
        var engine = new AudioEngine(() =>
            ++createCount == 1 ? output : replacementOutput);
        try
        {
            await engine.PlayAsync(Track(path), 96);
            await engine.PauseAsync();
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            output.PlayException = new InvalidOperationException("resume failed");

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeAsync());

            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
            await engine.PlayAsync(Track(path), 97);
            Assert.AreEqual(2, createCount);
            Assert.AreEqual(0.25f, replacementOutput.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Dispose_InvalidatesRemainingCapturedFaultSubscribers()
    {
        var output = new FakeWavePlayer
        {
            PlayException = new InvalidOperationException("queued failure"),
        };
        var engine = new AudioEngine(() => output);
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource();
        var secondInvocationCount = 0;
        engine.OutputFaulted += _ =>
        {
            firstEntered.TrySetResult();
            return releaseFirst.Task;
        };
        engine.OutputFaulted += _ =>
        {
            Interlocked.Increment(ref secondInvocationCount);
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.PlayAsync(Track(Fixture("mono-22050-tone.wav")), 98));
        await firstEntered.Task.WaitAsync(TestTimeout);
        await engine.DisposeAsync();

        releaseFirst.TrySetResult();
        Assert.AreEqual(0, Volatile.Read(ref secondInvocationCount));
    }

    [TestMethod]
    public async Task InitialCrossfadeSelection_StartsOneSourceAtFullGain()
    {
        var path = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.PlayAsync(Track(path), 101, ImmediateTransitionMode.Crossfade);

            Assert.AreEqual(1, engine.MusicInputCount);
            Assert.AreEqual(101L, engine.CurrentPlaybackId);
            Assert.IsNull(engine.OutgoingPlaybackId);
            Assert.AreEqual(1f, engine.CurrentMusicGain);
            Assert.AreEqual(0.25f, output.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Crossfade_UsesEqualPowerSamplesAndSwitchesProgressImmediately()
    {
        var firstPath = CreateConstantWaveFile(3);
        var secondPath = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endedIds = new List<long>();
        engine.TrackEnded += notification =>
        {
            lock (endedIds)
            {
                endedIds.Add(notification.PlaybackId);
            }

            return Task.CompletedTask;
        };

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 111);
            output.PumpFrames(100);
            await engine.PlayAsync(Track(secondPath), 112, ImmediateTransitionMode.Crossfade);

            var progress = await engine.GetProgressAsync();
            Assert.AreEqual(112L, progress.PlaybackId);
            Assert.AreEqual(TimeSpan.Zero, progress.Position);
            Assert.AreEqual(2, engine.MusicInputCount);
            Assert.AreEqual(0f, engine.CurrentMusicGain);
            Assert.AreEqual(1f, engine.OutgoingMusicGain);

            var start = output.ReadFrames(1);
            var throughMidpoint = output.ReadFrames(47_999);
            var throughEnd = output.ReadFrames(48_000);
            var expectedMidpoint = 0.25f * MathF.Sqrt(2f);

            Assert.AreEqual(0.25f, start[0], 0.00001f);
            Assert.AreEqual(expectedMidpoint, throughMidpoint[^2], 0.00002f);
            Assert.AreEqual(0.25f, throughEnd[^2], 0.00001f);
            Assert.IsTrue(start.Concat(throughMidpoint).Concat(throughEnd)
                .All(sample => float.IsFinite(sample) && Math.Abs(sample) <= 1f));

            await WaitUntilAsync(() => engine.OutgoingPlaybackId is null);
            Assert.AreEqual(1, engine.MusicInputCount);
            AssertFileReleased(firstPath);
            lock (endedIds)
            {
                Assert.IsEmpty(endedIds);
            }
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task Crossfade_UsesConfiguredMediumAndStaggerWithoutAdvancingIncomingSource()
    {
        var firstPath = CreateConstantWaveFile(5);
        var secondPath = CreateConstantWaveFile(5);
        var output = new FakeWavePlayer();
        AudioTrackSource? incomingSource = null;
        await using var engine = new AudioEngine(
            () => output,
            (path, playbackId, ended) =>
            {
                var source = AudioTrackSource.Open(path, playbackId, ended);
                if (playbackId == 114)
                {
                    incomingSource = source;
                }

                return source;
            });

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
            await engine.PlayAsync(Track(firstPath), 113);
            await engine.PlayAsync(
                Track(secondPath),
                114,
                ImmediateTransitionMode.Crossfade);
            Assert.IsNotNull(incomingSource);

            var throughStagger = output.ReadFrames(48_000);
            Assert.IsTrue(throughStagger.Any(sample => Math.Abs(sample) > 0f));
            Assert.AreEqual(TimeSpan.Zero, (await engine.GetProgressAsync()).Position);
            Assert.AreEqual(0, incomingSource!.SamplesRead);

            var firstIncomingFrame = output.ReadFrames(1);
            Assert.IsTrue(firstIncomingFrame.Any(sample => Math.Abs(sample) > 0f));
            Assert.IsTrue((await engine.GetProgressAsync()).Position > TimeSpan.Zero);
            Assert.IsGreaterThan(0, incomingSource!.SamplesRead);

            var afterIncomingStart = output.ReadFrames(24_000);
            Assert.IsTrue(afterIncomingStart.Any(sample => Math.Abs(sample) > 0f));
            Assert.IsGreaterThan(0f, engine.CurrentMusicGain);

            output.PumpFrames(48_000);
            await WaitUntilAsync(() => engine.OutgoingPlaybackId is null);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task ConfigureTiming_RejectsStaggerLongerThanMediumAndAcceptsEquality()
    {
        await using var engine = new AudioEngine(() => new FakeWavePlayer());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.01)));

        await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task SupersededDelayedCrossfade_RetiresStaleSourceAndPreservesLatestIdentity()
    {
        var firstPath = CreateConstantWaveFile(5, 0.2f);
        var delayedPath = CreateConstantWaveFile(5, 0.4f);
        var replacementPath = CreateConstantWaveFile(5, 0.6f);
        var output = new FakeWavePlayer();
        AudioTrackSource? delayedSource = null;
        await using var engine = new AudioEngine(
            () => output,
            (path, playbackId, ended) =>
            {
                var source = AudioTrackSource.Open(path, playbackId, ended);
                if (playbackId == 115)
                {
                    delayedSource = source;
                }

                return source;
            });

        try
        {
            await engine.ConfigureTimingAsync(
                TimeSpan.FromSeconds(4),
                TimeSpan.FromSeconds(1));
            await engine.PlayAsync(Track(firstPath), 114);
            await engine.PlayAsync(Track(delayedPath), 115, ImmediateTransitionMode.Crossfade);
            Assert.AreEqual(0, delayedSource!.SamplesRead);

            await engine.PlayAsync(Track(replacementPath), 116, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(48_000);

            Assert.AreEqual(116L, engine.CurrentPlaybackId);
            Assert.AreEqual(114L, engine.OutgoingPlaybackId);
            Assert.AreEqual(2, engine.MusicInputCount);
            Assert.AreEqual(0, delayedSource.SamplesRead);
            AssertFileReleased(delayedPath);

            output.PumpFrames(48_000);
            Assert.AreEqual(0, delayedSource.SamplesRead);
            Assert.AreEqual(116L, engine.CurrentPlaybackId);
            Assert.AreEqual(114L, engine.OutgoingPlaybackId);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(delayedPath);
            File.Delete(replacementPath);
        }
    }

    [TestMethod]
    public async Task OutgoingPhysicalEnd_ReleasesFileWithoutTrackEnded()
    {
        var firstPath = CreateConstantWaveFile(0.05);
        var secondPath = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 121);
            await engine.PlayAsync(Track(secondPath), 122, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(4_800);

            await WaitUntilAsync(() => engine.OutgoingPlaybackId is null);
            Assert.AreEqual(122L, engine.CurrentPlaybackId);
            Assert.AreEqual(0, endCount);
            AssertFileReleased(firstPath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task IncomingPhysicalEnd_NotifiesOnceAndRetiresOutgoing()
    {
        var firstPath = CreateConstantWaveFile(3);
        var secondPath = CreateConstantWaveFile(0.05);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        var ended = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.TrackEnded += notification =>
        {
            Interlocked.Increment(ref endCount);
            ended.TrySetResult(notification.PlaybackId);
            return Task.CompletedTask;
        };

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 131);
            await engine.PlayAsync(Track(secondPath), 132, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(4_800);

            Assert.AreEqual(132L, await ended.Task.WaitAsync(TestTimeout));
            Assert.AreEqual(1, endCount);
            Assert.IsNull(engine.CurrentPlaybackId);
            Assert.IsNull(engine.OutgoingPlaybackId);
            Assert.AreEqual(0, engine.MusicInputCount);
            AssertFileReleased(firstPath);
            AssertFileReleased(secondPath);
            output.PumpFrames(4_800);
            Assert.AreEqual(1, endCount);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task RapidCrossfades_KeepOnlyLatestAndOneOutgoing()
    {
        var firstPath = CreateConstantWaveFile(4);
        var secondPath = CreateConstantWaveFile(4);
        var thirdPath = CreateConstantWaveFile(4);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 141);
            await engine.PlayAsync(Track(secondPath), 142, ImmediateTransitionMode.Crossfade);
            await engine.PlayAsync(Track(thirdPath), 143, ImmediateTransitionMode.Crossfade);

            Assert.AreEqual(2, engine.MusicInputCount);
            Assert.AreEqual(143L, engine.CurrentPlaybackId);
            Assert.AreEqual(141L, engine.OutgoingPlaybackId);
            AssertFileReleased(secondPath);
            var firstFrame = output.ReadFrames(1);
            Assert.AreEqual(0.25f, firstFrame[0], 0.000001f);
            Assert.AreEqual(0.25f, firstFrame[1], 0.000001f);

            output.PumpFrames(95_999);
            await WaitUntilAsync(() => engine.OutgoingPlaybackId is null);

            Assert.AreEqual(143L, engine.CurrentPlaybackId);
            Assert.AreEqual(1, engine.MusicInputCount);
            AssertFileReleased(firstPath);
            Assert.AreEqual(0, endCount);

            await engine.StopAsync();
            AssertFileReleased(thirdPath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(thirdPath);
        }
    }

    [TestMethod]
    [DataRow(24_000, 141L, 0.2f)]
    [DataRow(72_000, 142L, 0.4f)]
    public async Task InterruptedCrossfade_RetainsTheMoreAudibleSource(
        int framesBeforeInterruption,
        long expectedOutgoingId,
        float expectedOutgoingSample)
    {
        var firstPath = CreateConstantWaveFile(5, 0.2f);
        var secondPath = CreateConstantWaveFile(5, 0.4f);
        var thirdPath = CreateConstantWaveFile(5, 0.6f);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 141);
            await engine.PlayAsync(Track(secondPath), 142, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(framesBeforeInterruption);

            var firstGain = engine.OutgoingMusicGain;
            var secondGain = engine.CurrentMusicGain;
            var retainedGain = Math.Max(firstGain, secondGain);
            await engine.PlayAsync(Track(thirdPath), 143, ImmediateTransitionMode.Crossfade);
            var firstFrame = output.ReadFrames(1);

            Assert.AreEqual(143L, engine.CurrentPlaybackId);
            Assert.AreEqual(expectedOutgoingId, engine.OutgoingPlaybackId);
            Assert.AreEqual(2, engine.MusicInputCount);
            Assert.IsGreaterThan(0.1f, firstFrame[0]);
            Assert.AreEqual(expectedOutgoingSample * retainedGain, firstFrame[0], 0.00002f);
            Assert.AreEqual(firstFrame[0], firstFrame[1], 0.000001f);

            if (expectedOutgoingId == 141L)
            {
                AssertFileReleased(secondPath);
            }
            else
            {
                AssertFileReleased(firstPath);
            }
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(thirdPath);
        }
    }

    [TestMethod]
    public async Task InterruptedCrossfade_ScalesRetainedOutgoingFadeFromCurrentGain()
    {
        var firstPath = CreateConstantWaveFile(8, 0.2f);
        var secondPath = CreateConstantWaveFile(8, 0.4f);
        var thirdPath = CreateConstantWaveFile(8, 0.6f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(4), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 144);
            await engine.PlayAsync(Track(secondPath), 145, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(144_000);

            Assert.AreEqual(145L, engine.CurrentPlaybackId);
            Assert.IsGreaterThan(0.9f, engine.CurrentMusicGain);
            Assert.IsLessThan(0.94f, engine.CurrentMusicGain);

            await engine.PlayAsync(Track(thirdPath), 146, ImmediateTransitionMode.Crossfade);
            Assert.AreEqual(145L, engine.OutgoingPlaybackId);

            output.PumpFrames(182_400);
            await WaitUntilAsync(() => engine.OutgoingPlaybackId is null);

            Assert.AreEqual(146L, engine.CurrentPlaybackId);
            Assert.AreEqual(1, engine.MusicInputCount);
            AssertFileReleased(secondPath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(thirdPath);
        }
    }

    [TestMethod]
    public async Task Pause_FreezesCrossfadeAndMasterFadeUntilResume()
    {
        var firstPath = CreateConstantWaveFile(4);
        var secondPath = CreateConstantWaveFile(4);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 151);
            await engine.PlayAsync(Track(secondPath), 152, ImmediateTransitionMode.Crossfade);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
            output.PumpFrames(24_000);

            var beforePause = await engine.GetProgressAsync();
            var incomingGain = engine.CurrentMusicGain;
            var outgoingGain = engine.OutgoingMusicGain;
            await engine.PauseAsync();
            Assert.AreEqual(0, output.PumpFrames(24_000));
            var whilePaused = await engine.GetProgressAsync();

            Assert.AreEqual(beforePause.Position, whilePaused.Position);
            Assert.AreEqual(beforePause.MasterGain, whilePaused.MasterGain);
            Assert.AreEqual(incomingGain, engine.CurrentMusicGain);
            Assert.AreEqual(outgoingGain, engine.OutgoingMusicGain);

            await engine.ResumeAsync();
            output.PumpFrames(24_000);
            var resumed = await engine.GetProgressAsync();
            Assert.IsGreaterThan(whilePaused.Position, resumed.Position);
            Assert.IsLessThan(whilePaused.MasterGain, resumed.MasterGain);
            Assert.IsGreaterThan(incomingGain, engine.CurrentMusicGain);
            Assert.IsLessThan(outgoingGain, engine.OutgoingMusicGain);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(10)]
    public async Task MasterFadeOut_UsesExactFrameCountAndLeavesPlaybackRunning(int seconds)
    {
        var path = CreateConstantWaveFile(seconds + 2);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(path), 161);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(seconds));
            var frameCount = seconds * 48_000;

            output.PumpFrames(frameCount - 1);
            var beforeLastFrame = await engine.GetProgressAsync();
            Assert.AreEqual(MasterFadeState.FadingOut, beforeLastFrame.MasterFadeState);
            Assert.IsGreaterThan(0f, beforeLastFrame.MasterGain);

            output.PumpFrames(1);
            var muted = await engine.GetProgressAsync();
            Assert.AreEqual(MasterFadeState.Muted, muted.MasterFadeState);
            Assert.AreEqual(0f, muted.MasterGain);
            Assert.AreEqual(0, output.StopCount);
            Assert.AreEqual(0, endCount);

            var positionBeforeSilence = muted.Position;
            var silence = output.ReadFrames(100);
            Assert.IsTrue(silence.All(sample => sample == 0f));
            Assert.IsGreaterThan(positionBeforeSilence, (await engine.GetProgressAsync()).Position);

            await engine.StopAsync();
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MasterFade_ReversalIsContinuousAndUsesRemainingDistance()
    {
        var path = CreateConstantWaveFile(5);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.PlayAsync(Track(path), 171);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
            var firstHalf = output.ReadFrames(48_000);
            var midpoint = await engine.GetProgressAsync();
            Assert.AreEqual(0.5f, midpoint.MasterGain, 0.00002f);

            await engine.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.FromSeconds(2));
            var firstReversedFrame = output.ReadFrames(1);
            Assert.AreEqual(firstHalf[^2], firstReversedFrame[0], 0.000001f);

            var scaledTicks = decimal.ToInt64(decimal.Round(
                TimeSpan.FromSeconds(2).Ticks * (decimal)(1f - midpoint.MasterGain),
                0,
                MidpointRounding.AwayFromZero));
            var expectedFrames = decimal.ToInt32(decimal.Round(
                scaledTicks * 48_000m / TimeSpan.TicksPerSecond,
                0,
                MidpointRounding.AwayFromZero));
            output.PumpFrames(expectedFrames - 2);
            Assert.AreEqual(
                MasterFadeState.FadingIn,
                (await engine.GetProgressAsync()).MasterFadeState);
            output.PumpFrames(1);

            var full = await engine.GetProgressAsync();
            Assert.AreEqual(MasterFadeState.Full, full.MasterFadeState);
            Assert.AreEqual(1f, full.MasterGain);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task MasterFade_ValidatesArgumentsAndZeroDurationIsImmediate()
    {
        var path = CreateConstantWaveFile(1);
        var engine = new AudioEngine(() => new FakeWavePlayer());
        try
        {
            await engine.PlayAsync(Track(path), 181);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                engine.FadeMasterAsync((MasterFadeDirection)99, TimeSpan.FromSeconds(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                engine.PlayAsync(Track(path), 182, (ImmediateTransitionMode)99));

            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            var muted = await engine.GetProgressAsync();
            Assert.AreEqual(0f, muted.MasterGain);
            Assert.AreEqual(MasterFadeState.Muted, muted.MasterFadeState);

            await engine.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.Zero);
            var full = await engine.GetProgressAsync();
            Assert.AreEqual(1f, full.MasterGain);
            Assert.AreEqual(MasterFadeState.Full, full.MasterFadeState);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Progress_ReportsMasterStateWithoutCurrentMusic()
    {
        var path = CreateConstantWaveFile(0.01);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());

            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            var beforePlay = await engine.GetProgressAsync();
            Assert.IsNull(beforePlay.PlaybackId);
            Assert.AreEqual(TimeSpan.Zero, beforePlay.Position);
            Assert.AreEqual(TimeSpan.Zero, beforePlay.Duration);
            Assert.AreEqual(0f, beforePlay.MasterGain);
            Assert.AreEqual(MasterFadeState.Muted, beforePlay.MasterFadeState);

            await engine.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.Zero);
            await engine.PlayAsync(Track(path), 185);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            output.PumpFrames(1_000);
            await WaitUntilAsync(() => engine.CurrentPlaybackId is null);

            var afterEnd = await engine.GetProgressAsync();
            Assert.IsNull(afterEnd.PlaybackId);
            Assert.AreEqual(TimeSpan.Zero, afterEnd.Position);
            Assert.AreEqual(TimeSpan.Zero, afterEnd.Duration);
            Assert.AreEqual(0f, afterEnd.MasterGain);
            Assert.AreEqual(MasterFadeState.Muted, afterEnd.MasterFadeState);
            Assert.IsTrue(output.ReadFrames(1).All(sample => sample == 0f));

            await engine.StopAsync();
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task StopDuringOverlap_ReleasesBothAndResetsMaster()
    {
        var firstPath = CreateConstantWaveFile(3);
        var secondPath = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 191);
            await engine.PlayAsync(Track(secondPath), 192, ImmediateTransitionMode.Crossfade);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
            output.PumpFrames(24_000);
            await engine.StopAsync();

            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
            Assert.AreEqual(0, engine.MusicInputCount);
            Assert.AreEqual(1, output.StopCount);
            Assert.AreEqual(0, endCount);
            AssertFileReleased(firstPath);
            AssertFileReleased(secondPath);

            await engine.PlayAsync(Track(secondPath), 193);
            var replayProgress = await engine.GetProgressAsync();
            Assert.AreEqual(1f, replayProgress.MasterGain);
            Assert.AreEqual(MasterFadeState.Full, replayProgress.MasterFadeState);
            Assert.AreEqual(0.25f, output.ReadFrames(1)[0], 0.000001f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task BusFades_AreIndependentFromPersistentVolumesAndMasterFade()
    {
        var musicPath = CreateConstantWaveFile(5, 0.2f);
        var ambiencePath = CreateConstantWaveFile(5, 0.1f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.SetMusicVolumeAsync(0.5f);
            await engine.SetAmbienceVolumeAsync(0.5f);
            await engine.SetMasterVolumeAsync(0.5f);
            await engine.PlayAsync(Track(musicPath), 195);
            await engine.PlayAmbienceAsync(Track(ambiencePath), 0.5f);
            output.PumpFrames(96_000);

            var full = await engine.GetProgressAsync();
            Assert.AreEqual(1f, full.MusicFadeGain, 0.00002f);
            Assert.AreEqual(1f, full.AmbienceFadeGain, 0.00002f);
            Assert.AreEqual(0.5f, full.MusicVolume, 0.00002f);
            Assert.AreEqual(0.5f, full.AmbienceVolume, 0.00002f);
            Assert.AreEqual(0.5f, full.MasterVolume, 0.00002f);

            await engine.FadeMusicAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
            output.PumpFrames(48_000);
            var halfway = await engine.GetProgressAsync();
            Assert.AreEqual(0.5f, halfway.MusicFadeGain, 0.00002f);
            Assert.AreEqual(MasterFadeState.FadingOut, halfway.MusicFadeState);
            Assert.AreEqual(1f, halfway.AmbienceFadeGain, 0.00002f);
            Assert.AreEqual(0.5f, halfway.MasterVolume, 0.00002f);

            await engine.SetMusicVolumeAsync(1f);
            Assert.AreEqual(1f, (await engine.GetProgressAsync()).MusicVolume, 0.00002f);
            Assert.AreEqual(0.5f, (await engine.GetProgressAsync()).MusicFadeGain, 0.00002f);

            await engine.FadeAmbienceAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            var mutedAmbience = await engine.GetProgressAsync();
            Assert.AreEqual(0f, mutedAmbience.AmbienceFadeGain);
            Assert.AreEqual(MasterFadeState.Muted, mutedAmbience.AmbienceFadeState);
            Assert.AreEqual(0.5f, mutedAmbience.MusicFadeGain, 0.00002f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task PersistentVolumes_ActiveStagesRampAtMediumRateAndRetargetFromRenderedGain()
    {
        var musicPath = CreateConstantWaveFile(12, 0.2f);
        var ambiencePath = CreateConstantWaveFile(12, 0.1f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(4), TimeSpan.Zero);
            await engine.PlayAsync(Track(musicPath), 198);
            await engine.PlayAmbienceAsync(Track(ambiencePath), 1f);
            output.PumpFrames(192_000);

            await engine.SetMusicVolumeAsync(0.25f);
            await engine.SetAmbienceVolumeAsync(0.5f);
            await engine.SetMasterVolumeAsync(0.75f);

            var requested = await engine.GetProgressAsync();
            Assert.AreEqual(0.25f, requested.MusicVolume, 0.00002f);
            Assert.AreEqual(0.5f, requested.AmbienceVolume, 0.00002f);
            Assert.AreEqual(0.75f, requested.MasterVolume, 0.00002f);
            Assert.AreEqual(1f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(1f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(1f, engine.CurrentMasterVolumeGain, 0.00002f);

            output.PumpFrames(96_000);
            Assert.AreEqual(0.5f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.5f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.75f, engine.CurrentMasterVolumeGain, 0.00002f);
            requested = await engine.GetProgressAsync();
            Assert.AreEqual(0.25f, requested.MusicVolume, 0.00002f);
            Assert.AreEqual(0.5f, requested.AmbienceVolume, 0.00002f);
            Assert.AreEqual(0.75f, requested.MasterVolume, 0.00002f);

            await engine.SetMusicVolumeAsync(1f);
            await engine.SetAmbienceVolumeAsync(0.25f);
            await engine.SetMasterVolumeAsync(0f);
            Assert.AreEqual(0.5f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.5f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.75f, engine.CurrentMasterVolumeGain, 0.00002f);

            output.ReadFrames(1);
            Assert.AreEqual(0.5f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.5f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.75f, engine.CurrentMasterVolumeGain, 0.00002f);

            // The replacement targets use 2, 1, and 3 seconds from the
            // rendered gain, rather than restarting their old ramps.
            output.PumpFrames(48_000);
            Assert.AreEqual(0.75f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.25f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.5f, engine.CurrentMasterVolumeGain, 0.00002f);

            output.PumpFrames(48_000);
            Assert.AreEqual(1f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.25f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.25f, engine.CurrentMasterVolumeGain, 0.00002f);
            output.PumpFrames(48_000);
            Assert.AreEqual(0f, engine.CurrentMasterVolumeGain, 0.00002f);

            requested = await engine.GetProgressAsync();
            Assert.AreEqual(1f, requested.MusicVolume, 0.00002f);
            Assert.AreEqual(0.25f, requested.AmbienceVolume, 0.00002f);
            Assert.AreEqual(0f, requested.MasterVolume, 0.00002f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task PersistentVolumes_InactiveStagesApplyImmediatelyBeforePlayback()
    {
        var path = CreateConstantWaveFile(3, 0.25f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(4), TimeSpan.Zero);
            await engine.SetMusicVolumeAsync(0.2f);
            await engine.SetAmbienceVolumeAsync(0.5f);
            await engine.SetMasterVolumeAsync(0.4f);

            Assert.AreEqual(0.2f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.5f, engine.CurrentAmbienceVolumeGain, 0.00002f);
            Assert.AreEqual(0.4f, engine.CurrentMasterVolumeGain, 0.00002f);
            var progress = await engine.GetProgressAsync();
            Assert.AreEqual(0.2f, progress.MusicVolume, 0.00002f);
            Assert.AreEqual(0.5f, progress.AmbienceVolume, 0.00002f);
            Assert.AreEqual(0.4f, progress.MasterVolume, 0.00002f);
            Assert.AreEqual(0, output.InitCount);

            await engine.PlayAsync(Track(path), 199);
            Assert.AreEqual(0.02f, output.ReadFrames(1)[0], 0.00002f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PersistentVolumes_PausedMusicOnlyAppliesImmediatelyForResume()
    {
        var path = CreateConstantWaveFile(3, 0.25f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(4), TimeSpan.Zero);
            await engine.PlayAsync(Track(path), 200);
            await engine.PauseAsync();
            await engine.SetMusicVolumeAsync(0.2f);
            await engine.SetMasterVolumeAsync(0.4f);

            Assert.AreEqual(0.2f, engine.CurrentMusicVolumeGain, 0.00002f);
            Assert.AreEqual(0.4f, engine.CurrentMasterVolumeGain, 0.00002f);
            var progress = await engine.GetProgressAsync();
            Assert.AreEqual(0.2f, progress.MusicVolume, 0.00002f);
            Assert.AreEqual(0.4f, progress.MasterVolume, 0.00002f);

            await engine.ResumeAsync();
            Assert.AreEqual(0.02f, output.ReadFrames(1)[0], 0.00002f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task CompletionAwareMasterFade_CompletesAtSampleTargetAndIsCanceledWhenSuperseded()
    {
        var path = CreateConstantWaveFile(5);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.PlayAsync(Track(path), 196);
            var first = engine.FadeMasterAndWaitAsync(
                MasterFadeDirection.Out,
                TimeSpan.FromSeconds(2));
            output.PumpFrames(48_000);
            Assert.IsFalse(first.IsCompleted);

            await engine.FadeMasterAsync(MasterFadeDirection.In, TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TaskCanceledException>(async () => await first);

            var second = engine.FadeMasterAndWaitAsync(
                MasterFadeDirection.Out,
                TimeSpan.FromSeconds(2));
            output.PumpFrames(47_999);
            Assert.IsFalse(second.IsCompleted);
            output.PumpFrames(1);
            await second.WaitAsync(TestTimeout);
            Assert.AreEqual(0f, (await engine.GetProgressAsync()).MasterFadeGain);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PreserveZeroStop_DetachesAllSourcesWithoutResettingMasterFade()
    {
        var musicPath = CreateConstantWaveFile(5, 0.2f);
        var ambiencePath = CreateConstantWaveFile(5, 0.1f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.PlayAsync(Track(musicPath), 197);
            await engine.PlayAmbienceAsync(Track(ambiencePath), 1f);
            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.Zero);
            await engine.StopSourcesPreservingMasterFadeAsync();

            var progress = await engine.GetProgressAsync();
            Assert.IsNull(progress.PlaybackId);
            Assert.IsEmpty(progress.AmbienceSnapshots);
            Assert.AreEqual(0f, progress.MasterFadeGain);
            Assert.AreEqual(MasterFadeState.Muted, progress.MasterFadeState);
            Assert.AreEqual(0, engine.MusicInputCount);
            Assert.AreEqual(0, engine.AmbienceInputCount);
            Assert.AreEqual(1, output.StopCount);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task OutputFaultDuringOverlap_ReleasesBothWithIncomingId()
    {
        var firstPath = CreateConstantWaveFile(3);
        var secondPath = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var endCount = 0;
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 201);
            await engine.PlayAsync(Track(secondPath), 202, ImmediateTransitionMode.Crossfade);
            output.RaiseFault(new InvalidOperationException("device removed"));

            var fault = await faulted.Task.WaitAsync(TestTimeout);
            Assert.AreEqual(202L, fault.PlaybackId);
            Assert.AreEqual(AudioProgressSnapshot.Stopped, await engine.GetProgressAsync());
            Assert.AreEqual(0, engine.MusicInputCount);
            Assert.AreEqual(0, endCount);
            AssertFileReleased(firstPath);
            AssertFileReleased(secondPath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task DisposeDuringOverlap_IsIdempotentAndReleasesBoth()
    {
        var firstPath = CreateConstantWaveFile(3);
        var secondPath = CreateConstantWaveFile(3);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        var endCount = 0;
        engine.TrackEnded += _ =>
        {
            Interlocked.Increment(ref endCount);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(firstPath), 211);
            await engine.PlayAsync(Track(secondPath), 212, ImmediateTransitionMode.Crossfade);
            await engine.DisposeAsync();
            await engine.DisposeAsync();

            Assert.AreEqual(1, output.DisposeCount);
            Assert.AreEqual(1, output.StopCount);
            Assert.AreEqual(0, endCount);
            AssertFileReleased(firstPath);
            AssertFileReleased(secondPath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task CorruptReplacement_LeavesCurrentPlaybackIntact()
    {
        var validPath = CreateConstantWaveFile(3);
        var corruptPath = TemporaryPath(".wav");
        File.WriteAllBytes(corruptPath, [1, 2, 3, 4]);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.PlayAsync(Track(validPath), 221);
            output.PumpFrames(100);
            var before = await engine.GetProgressAsync();

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                engine.PlayAsync(Track(corruptPath), 222, ImmediateTransitionMode.Crossfade));

            var after = await engine.GetProgressAsync();
            Assert.AreEqual(221L, after.PlaybackId);
            Assert.AreEqual(before.Position, after.Position);
            Assert.AreEqual(1, engine.MusicInputCount);
            Assert.AreEqual(1, output.PlayCount);
            Assert.AreEqual(0, output.StopCount);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(validPath);
            File.Delete(corruptPath);
        }
    }

    [TestMethod]
    public async Task SafetyStage_BoundsCorrelatedCrossfadeOverlap()
    {
        var firstPath = CreateConstantWaveFile(3, 0.9f);
        var secondPath = CreateConstantWaveFile(3, 0.9f);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(() => output);
        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAsync(Track(firstPath), 231);
            await engine.PlayAsync(Track(secondPath), 232, ImmediateTransitionMode.Crossfade);
            output.PumpFrames(47_999);
            var midpoint = output.ReadFrames(1);

            Assert.IsTrue(midpoint.All(sample => float.IsFinite(sample) && Math.Abs(sample) <= 1f));
            Assert.AreEqual(1f, midpoint[0]);
            Assert.AreEqual(1f, midpoint[1]);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    [TestMethod]
    public async Task Ambience_LoopsThroughBoundariesAndStopsAfterExactFade()
    {
        var path = CreateConstantWaveFile(0.05, 0.2f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(2), TimeSpan.Zero);
            await engine.PlayAmbienceAsync(Track(path), 0.5f);
            Assert.AreEqual(1, engine.AmbienceInputCount);
            Assert.AreEqual(0f, output.ReadFrames(1)[0]);

            var throughFade = output.ReadFrames(96_000);
            Assert.AreEqual(0.1f, throughFade[^2], 0.00002f);
            var snapshot = await engine.GetProgressAsync();
            Assert.HasCount(1, snapshot.AmbienceSnapshots);
            Assert.AreEqual(0.5f, snapshot.AmbienceSnapshots[0].SourceGain);

            await engine.StopAmbienceAsync(path.ToUpperInvariant());
            output.PumpFrames(95_999);
            Assert.HasCount(1, (await engine.GetProgressAsync()).AmbienceSnapshots);
            output.PumpFrames(1);
            await WaitUntilAsync(() => engine.AmbienceInputCount == 0);
            Assert.IsEmpty((await engine.GetProgressAsync()).AmbienceSnapshots);
            AssertFileReleased(path);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task AmbienceSourceGain_UsesIndependentMediumRampAndRetargetsFromRenderedGain()
    {
        var path = CreateConstantWaveFile(5, 0.2f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.ConfigureTimingAsync(TimeSpan.FromSeconds(4), TimeSpan.Zero);
            await engine.PlayAmbienceAsync(Track(path), 0.25f);
            output.PumpFrames(192_000);
            Assert.AreEqual(0.25f, (await engine.GetProgressAsync()).AmbienceSnapshots.Single().SourceGain, 0.00002f);

            await engine.SetAmbienceSourceGainAsync(path, 0.75f);
            output.PumpFrames(48_000);
            var halfway = (await engine.GetProgressAsync()).AmbienceSnapshots.Single();
            Assert.AreEqual(0.5f, halfway.SourceGain, 0.00002f, $"lifecycle={halfway.LifecycleGain}, position={halfway.Position}");
            Assert.AreEqual(1f, halfway.LifecycleGain, 0.00002f);

            await engine.SetAmbienceSourceGainAsync(path, 0.25f);
            var firstRetargetedFrame = output.ReadFrames(1);
            Assert.AreEqual(0.1f, firstRetargetedFrame[0], 0.00002f);

            output.PumpFrames(48_000);
            var completed = (await engine.GetProgressAsync()).AmbienceSnapshots.Single();
            Assert.AreEqual(0.25f, completed.SourceGain, 0.00002f);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task AmbienceMp3_LoopsWithoutReopeningTheReader()
    {
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        await engine.PlayAmbienceAsync(Track(Fixture("silent-12-frame.mp3")), 1f);
        try
        {
            output.PumpFrames(144_000);
            Assert.HasCount(1, (await engine.GetProgressAsync()).AmbienceSnapshots);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [TestMethod]
    public async Task MusicPause_WithAmbienceKeepsSharedOutputActiveAndMusicFrozen()
    {
        var musicPath = CreateConstantWaveFile(4, 0.2f);
        var ambiencePath = CreateConstantWaveFile(0.07, 0.1f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.PlayAmbienceAsync(Track(ambiencePath), 1f);
            await engine.PlayAsync(Track(musicPath), 301);
            output.PumpFrames(24_000);
            var beforePause = await engine.GetProgressAsync();
            var ambienceBeforePause = beforePause.AmbienceSnapshots[0].Position;

            await engine.FadeMasterAsync(MasterFadeDirection.Out, TimeSpan.FromSeconds(2));
            await engine.PauseAsync();
            Assert.AreEqual(0, output.PauseCount);
            var audibleWhilePaused = output.ReadFrames(48_000);
            var paused = await engine.GetProgressAsync();

            Assert.AreEqual(beforePause.Position, paused.Position);
            Assert.IsGreaterThan(ambienceBeforePause, paused.AmbienceSnapshots[0].Position);
            Assert.AreEqual(0.5f, paused.MasterGain, 0.00002f);
            Assert.IsTrue(audibleWhilePaused.Any(sample => Math.Abs(sample) > 0.001f));

            output.PumpFrames(48_000);
            var completed = await engine.GetProgressAsync();
            Assert.AreEqual(MasterFadeState.Muted, completed.MasterFadeState);
            Assert.AreEqual(0f, completed.MasterGain);
            Assert.AreEqual(paused.Position, completed.Position);
            await engine.ResumeAsync();
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task AmbienceOnlyOutputFault_ReleasesSourcesAndAllowsRecovery()
    {
        var path = CopyFixtureToTemporaryFile("mono-22050-tone.wav");
        var output = new FakeWavePlayer();
        var replacement = new FakeWavePlayer();
        var createCount = 0;
        output.RaiseFaultChangesPlaybackState = false;
        await using var engine = new AudioEngine(() => ++createCount == 1 ? output : replacement);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAmbienceAsync(Track(path), 1f);
            output.RaiseFault(new InvalidOperationException("ambience device lost"));
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.IsNotNull(fault.Exception);
            Assert.IsEmpty((await engine.GetProgressAsync()).AmbienceSnapshots);
            Assert.AreEqual(0, engine.AmbienceInputCount);
            Assert.AreEqual(1, output.DisposeCount);
            AssertFileReleased(path);

            await engine.PlayAmbienceAsync(Track(path), 1f);
            Assert.AreEqual(2, createCount);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task StopMusicThenOutputFault_ClearsAmbienceAndUsesFreshOutput()
    {
        var musicPath = CreateConstantWaveFile(3, 0.2f);
        var ambiencePath = CreateConstantWaveFile(0.07, 0.1f);
        var output = new FakeWavePlayer { RaiseFaultChangesPlaybackState = false };
        var replacement = new FakeWavePlayer();
        var outputCount = 0;
        await using var engine = new AudioEngine(() => ++outputCount == 1 ? output : replacement);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faultCount = 0;
        engine.OutputFaulted += fault =>
        {
            Interlocked.Increment(ref faultCount);
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(musicPath), 311);
            await engine.PlayAmbienceAsync(Track(ambiencePath), 1f);
            await engine.StopMusicAsync();
            output.RaiseFault(new InvalidOperationException("stopped output"));
            var fault = await faulted.Task.WaitAsync(TestTimeout);

            Assert.IsNull((await engine.GetProgressAsync()).PlaybackId);
            Assert.IsEmpty((await engine.GetProgressAsync()).AmbienceSnapshots);
            Assert.AreEqual(1, faultCount);
            Assert.AreEqual(1, output.DisposeCount);
            AssertFileReleased(musicPath);
            AssertFileReleased(ambiencePath);

            await engine.PlayAmbienceAsync(Track(ambiencePath), 1f);
            Assert.AreEqual(2, outputCount);
            Assert.AreEqual(NAudio.Wave.PlaybackState.Playing, replacement.PlaybackState);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task ZeroDataAmbience_IsRejectedWithoutAddingInputOrLeakingFile()
    {
        var path = TemporaryPath(".wav");
        using (var writer = new WaveFileWriter(
            path,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2)))
        {
        }

        await using var engine = new AudioEngine(() => new FakeWavePlayer());
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                engine.PlayAmbienceAsync(Track(path), 1f));
            Assert.AreEqual(0, engine.AmbienceInputCount);
            Assert.IsEmpty((await engine.GetProgressAsync()).AmbienceSnapshots);
            AssertFileReleased(path);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task PartialThenEmptyLoopingSource_CleansUpGraphOffReadCallback()
    {
        var path = TemporaryPath(".raw");
        File.WriteAllBytes(path, new byte[16]);
        var output = new FakeWavePlayer();
        var engine = new AudioEngine(
            () => output,
            (sourcePath, playbackId, ended) =>
                AudioTrackSource.FromWaveStreamForTests(
                    new PartialThenEmptyWaveStream(sourcePath),
                    playbackId,
                    ended));
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAmbienceAsync(Track(path), 1f);
            output.PumpFrames(1);
            await faulted.Task.WaitAsync(TestTimeout);

            Assert.AreEqual(0, engine.AmbienceInputCount);
            Assert.IsEmpty((await engine.GetProgressAsync()).AmbienceSnapshots);
            Assert.AreEqual(1, output.DisposeCount);
            AssertFileReleased(path);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task AmbienceStartFailure_ClearsPausedMusicAndReportsCurrentMusic()
    {
        var musicPath = CreateConstantWaveFile(3, 0.2f);
        var ambiencePath = CreateConstantWaveFile(0.07, 0.1f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);
        var faulted = new TaskCompletionSource<AudioOutputFault>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OutputFaulted += fault =>
        {
            faulted.TrySetResult(fault);
            return Task.CompletedTask;
        };

        try
        {
            await engine.PlayAsync(Track(musicPath), 321);
            await engine.PauseAsync();
            output.PlayException = new InvalidOperationException("resume failed");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.PlayAmbienceAsync(Track(ambiencePath), 1f));
            var fault = await faulted.Task.WaitAsync(TestTimeout);
            var progress = await engine.GetProgressAsync();

            Assert.AreEqual(321L, fault.PlaybackId);
            Assert.IsNull(progress.PlaybackId);
            Assert.IsEmpty(progress.AmbienceSnapshots);
            Assert.AreEqual(1, output.DisposeCount);
            AssertFileReleased(musicPath);
            AssertFileReleased(ambiencePath);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(musicPath);
            File.Delete(ambiencePath);
        }
    }

    [TestMethod]
    public async Task AmbienceTransitionCompletions_IgnoreStaleQueuedOrders()
    {
        var path = CreateConstantWaveFile(0.05, 0.2f);
        var output = new FakeWavePlayer();
        await using var engine = new AudioEngine(() => output);

        try
        {
            await engine.PlayAmbienceAsync(Track(path), 1f);
            var fadeInVersion = engine.GetAmbienceTransitionVersionForTests(path);
            output.PumpFrames(48_000);
            await engine.StopAmbienceAsync(path);
            var fadeOutVersion = engine.GetAmbienceTransitionVersionForTests(path);

            await engine.CompleteAmbienceTransitionForTests(path, fadeInVersion, desiredPlaying: true);
            Assert.AreEqual(1, engine.AmbienceInputCount);

            await engine.PlayAmbienceAsync(Track(path), 1f);
            var resumedVersion = engine.GetAmbienceTransitionVersionForTests(path);
            await engine.CompleteAmbienceTransitionForTests(path, fadeOutVersion, desiredPlaying: false);
            Assert.AreEqual(1, engine.AmbienceInputCount);
            await engine.CompleteAmbienceTransitionForTests(path, resumedVersion, desiredPlaying: true);
            Assert.AreEqual(1, engine.AmbienceInputCount);
        }
        finally
        {
            await engine.DisposeAsync();
            File.Delete(path);
        }
    }

    private sealed class PartialThenEmptyWaveStream : WaveStream
    {
        private readonly FileStream _file;
        private bool _firstRead = true;

        internal PartialThenEmptyWaveStream(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public override WaveFormat WaveFormat =>
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public override long Length => _file.Length;

        public override long Position
        {
            get => _file.Position;
            set => _file.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_firstRead)
            {
                return 0;
            }

            _firstRead = false;
            return _file.Read(buffer, offset, Math.Min(count, 4));
        }

        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);

        public override void SetLength(long value) => _file.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static int ReadToEnd(AudioTrackSource source)
    {
        var buffer = new float[4096];
        var total = 0;

        while (true)
        {
            var read = source.Read(buffer);
            total += read;
            if (read == 0)
            {
                return total;
            }
        }
    }

    private static void AssertNormalized(WaveFormat format)
    {
        Assert.AreEqual(WaveFormatEncoding.IeeeFloat, format.Encoding);
        Assert.AreEqual(48_000, format.SampleRate);
        Assert.AreEqual(2, format.Channels);
        Assert.AreEqual(32, format.BitsPerSample);
    }

    private static LibraryTrack Track(string path) => new(Path.GetFileName(path), path);

    private static string Fixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string TemporaryPath(string extension) =>
        Path.Combine(Path.GetTempPath(), $"Soundrel-{Guid.NewGuid():N}{extension}");

    private static string CopyFixtureToTemporaryFile(string fileName)
    {
        var path = TemporaryPath(Path.GetExtension(fileName));
        File.Copy(Fixture(fileName), path);
        return path;
    }

    private static string CreateConstantWaveFile(double seconds, float sample = 0.25f)
    {
        var path = TemporaryPath(".wav");
        var framesRemaining = checked((int)Math.Round(seconds * 48_000));
        var buffer = new float[8_192];
        buffer.AsSpan().Fill(sample);
        using var writer = new WaveFileWriter(
            path,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));

        while (framesRemaining > 0)
        {
            var frames = Math.Min(framesRemaining, buffer.Length / 2);
            writer.WriteSamples(buffer, 0, frames * 2);
            framesRemaining -= frames;
        }

        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Task.Run(async () =>
            {
                while (!condition())
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
            }).WaitAsync(TestTimeout);
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    private static void AssertFileReleased(string path)
    {
        using var released = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
