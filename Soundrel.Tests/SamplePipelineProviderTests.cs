using NAudio.Wave;
using Soundrel.Services;

namespace Soundrel.Tests;

[TestClass]
public sealed class SamplePipelineProviderTests
{
    [ThreadStatic]
    private static bool _insideRead;

    [TestMethod]
    public void GainEnvelope_AppliesIdenticalGainToStereoFrameAcrossSplitReads()
    {
        var source = new ConstantSampleProvider(1_000, 2, 8);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        provider.RampTo(1f, TimeSpan.FromMilliseconds(4));
        var output = new float[8];

        Assert.AreEqual(3, provider.Read(output.AsSpan(0, 3)));
        Assert.AreEqual(1, provider.Read(output.AsSpan(3, 1)));
        Assert.AreEqual(3, provider.Read(output.AsSpan(4, 3)));
        Assert.AreEqual(1, provider.Read(output.AsSpan(7, 1)));

        for (var frame = 0; frame < 4; frame++)
        {
            Assert.AreEqual(output[frame * 2], output[(frame * 2) + 1], 0.000001f);
        }
    }

    [TestMethod]
    public void LinearRamp_HasExactEndpointsAndCompletesAfterWholeFrames()
    {
        var source = new ConstantSampleProvider(1_000, 2, 10);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        provider.RampTo(1f, TimeSpan.FromMilliseconds(5));
        var output = new float[10];

        Assert.AreEqual(3, provider.Read(output.AsSpan(0, 3)));
        Assert.IsTrue(provider.IsRampActive);
        Assert.AreEqual(6, provider.Read(output.AsSpan(3, 6)));
        Assert.IsTrue(provider.IsRampActive);
        Assert.AreEqual(1, provider.Read(output.AsSpan(9, 1)));

        Assert.IsFalse(provider.IsRampActive);
        Assert.AreEqual(1f, provider.CurrentGain);
        var expectedFrameGains = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
        for (var frame = 0; frame < expectedFrameGains.Length; frame++)
        {
            Assert.AreEqual(expectedFrameGains[frame], output[frame * 2], 0.000001f);
            Assert.AreEqual(expectedFrameGains[frame], output[(frame * 2) + 1], 0.000001f);
        }
    }

    [TestMethod]
    [DataRow(2, 96_000L)]
    [DataRow(10, 480_000L)]
    public void RampDuration_UsesExact48KhzFrameCount(int seconds, long expectedFrames)
    {
        var expectedSamples = checked(expectedFrames * 2);
        var source = new ConstantSampleProvider(48_000, 2, expectedSamples);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        provider.RampTo(1f, TimeSpan.FromSeconds(seconds));
        var allButLastSample = new float[checked((int)expectedSamples - 1)];

        Assert.AreEqual(allButLastSample.Length, provider.Read(allButLastSample));
        Assert.IsTrue(provider.IsRampActive);

        var lastSample = new float[1];
        Assert.AreEqual(1, provider.Read(lastSample));
        Assert.IsFalse(provider.IsRampActive);
        Assert.AreEqual(expectedSamples, source.SamplesRead);
        Assert.AreEqual(1f, provider.CurrentGain);
    }

    [TestMethod]
    public void RampRetarget_ReversesContinuouslyFromObservedGain()
    {
        var source = new ConstantSampleProvider(1_000, 1, 6);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        provider.RampTo(1f, TimeSpan.FromMilliseconds(5));
        var initialRamp = new float[3];
        Assert.AreEqual(3, provider.Read(initialRamp));
        Assert.AreEqual(0.5f, provider.CurrentGain, 0.000001f);

        provider.RampTo(0f, TimeSpan.FromMilliseconds(3));
        var reversedRamp = new float[3];
        Assert.AreEqual(3, provider.Read(reversedRamp));

        Assert.AreEqual(0.5f, reversedRamp[0], 0.000001f);
        Assert.AreEqual(0.25f, reversedRamp[1], 0.000001f);
        Assert.AreEqual(0f, reversedRamp[2], 0.000001f);
        Assert.IsFalse(provider.IsRampActive);
    }

    [TestMethod]
    public void EqualPowerCurves_HavePairedStartMidpointAndEnd()
    {
        var incoming = new GainEnvelopeSampleProvider(
            new ConstantSampleProvider(1_000, 1, 5),
            0f);
        var outgoing = new GainEnvelopeSampleProvider(
            new ConstantSampleProvider(1_000, 1, 5),
            1f);
        incoming.RampTo(1f, TimeSpan.FromMilliseconds(5), GainRampCurve.EqualPowerIncoming);
        outgoing.RampTo(0f, TimeSpan.FromMilliseconds(5), GainRampCurve.EqualPowerOutgoing);
        var incomingOutput = new float[5];
        var outgoingOutput = new float[5];

        Assert.AreEqual(5, incoming.Read(incomingOutput));
        Assert.AreEqual(5, outgoing.Read(outgoingOutput));

        Assert.AreEqual(0f, incomingOutput[0]);
        Assert.AreEqual(1f, outgoingOutput[0]);
        Assert.AreEqual(MathF.Sqrt(0.5f), incomingOutput[2], 0.000001f);
        Assert.AreEqual(MathF.Sqrt(0.5f), outgoingOutput[2], 0.000001f);
        Assert.AreEqual(1f, incomingOutput[4]);
        Assert.AreEqual(0f, outgoingOutput[4]);

        for (var frame = 0; frame < incomingOutput.Length; frame++)
        {
            var squaredGainSum =
                (incomingOutput[frame] * incomingOutput[frame]) +
                (outgoingOutput[frame] * outgoingOutput[frame]);
            Assert.AreEqual(1f, squaredGainSum, 0.000001f);
        }
    }

    [TestMethod]
    public void GainAssignments_ClampAndZeroDurationIsImmediate()
    {
        var source = new ConstantSampleProvider(48_000, 1, 3);
        var provider = new GainEnvelopeSampleProvider(source, -1f);
        var output = new float[1];
        Assert.AreEqual(0f, provider.CurrentGain);

        provider.SetGain(2f);
        Assert.AreEqual(1f, provider.CurrentGain);
        Assert.AreEqual(1, provider.Read(output));
        Assert.AreEqual(1f, output[0]);

        provider.RampTo(-2f, TimeSpan.Zero);
        Assert.AreEqual(0f, provider.CurrentGain);
        Assert.IsFalse(provider.IsRampActive);
        Assert.AreEqual(1, provider.Read(output));
        Assert.AreEqual(0f, output[0]);

        provider.SetGain(float.PositiveInfinity);
        Assert.AreEqual(1f, provider.CurrentGain);
        Assert.AreEqual(1, provider.Read(output));
        Assert.AreEqual(1f, output[0]);
        provider.SetGain(float.NaN);
        Assert.AreEqual(0f, provider.CurrentGain);
    }

    [TestMethod]
    public async Task RampCompletion_IsQueuedOffReadAndRaisedOnce()
    {
        var source = new ConstantSampleProvider(1_000, 1, 6);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        var completionCount = 0;
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.RampTo(
            1f,
            TimeSpan.FromMilliseconds(5),
            completed: () =>
            {
                Interlocked.Increment(ref completionCount);
                completed.TrySetResult(_insideRead);
            });

        _insideRead = true;
        try
        {
            Assert.AreEqual(5, provider.Read(new float[5]));
        }
        finally
        {
            _insideRead = false;
        }

        Assert.IsFalse(await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, provider.Read(new float[1]));
        Assert.AreEqual(1, completionCount);
    }

    [TestMethod]
    public async Task RampReplacement_InvalidatesPriorCompletion()
    {
        var source = new ConstantSampleProvider(1_000, 1, 10);
        var provider = new GainEnvelopeSampleProvider(source, 0f);
        var staleCompletionCount = 0;
        var replacementCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.RampTo(
            1f,
            TimeSpan.FromMilliseconds(5),
            completed: () => Interlocked.Increment(ref staleCompletionCount));
        Assert.AreEqual(2, provider.Read(new float[2]));

        provider.RampTo(
            0f,
            TimeSpan.FromMilliseconds(3),
            completed: () => replacementCompleted.TrySetResult());
        Assert.AreEqual(3, provider.Read(new float[3]));
        await replacementCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, staleCompletionCount);
        Assert.AreEqual(0f, provider.CurrentGain);
    }

    [TestMethod]
    public async Task FinalShortRead_AppliesCapturedGainBeforeConcurrentRetirement()
    {
        using var source = new GatedFinalSampleProvider([0.8f, -0.4f]);
        var provider = new GainEnvelopeSampleProvider(source, 0.75f);
        var output = new float[8];
        var readTask = Task.Run(() => provider.Read(output));
        await source.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var retirementStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retirementTask = Task.Run(() =>
        {
            retirementStarted.TrySetResult();
            provider.SetGain(0f);
        });
        await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.AllowReadToReturn.Set();
        Assert.AreEqual(2, await readTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await retirementTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0.6f, output[0], 0.000001f);
        Assert.AreEqual(-0.3f, output[1], 0.000001f);
        Assert.AreEqual(0f, provider.CurrentGain);
    }

    [TestMethod]
    public void SampleSafety_BoundsFiniteValuesAndSanitizesNonFiniteValues()
    {
        float[] input = [1.5f, -2f, 0.25f, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        var provider = new SampleSafetySampleProvider(new SequenceSampleProvider(input));
        var output = new float[input.Length];

        Assert.AreEqual(output.Length, provider.Read(output));

        var expected = new[] { 1f, -1f, 0.25f, 0f, 0f, 0f };
        CollectionAssert.AreEqual(expected, output);
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _sample;
        private long _remainingSamples;

        internal ConstantSampleProvider(
            int sampleRate,
            int channels,
            long sampleCount,
            float sample = 1f)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _remainingSamples = sampleCount;
            _sample = sample;
        }

        public WaveFormat WaveFormat { get; }

        internal long SamplesRead { get; private set; }

        public int Read(Span<float> buffer)
        {
            var samplesRead = (int)Math.Min(buffer.Length, _remainingSamples);
            buffer[..samplesRead].Fill(_sample);
            _remainingSamples -= samplesRead;
            SamplesRead += samplesRead;
            return samplesRead;
        }
    }

    private sealed class SequenceSampleProvider(float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 1);

        public int Read(Span<float> buffer)
        {
            var samplesRead = Math.Min(buffer.Length, samples.Length - _position);
            samples.AsSpan(_position, samplesRead).CopyTo(buffer);
            _position += samplesRead;
            return samplesRead;
        }
    }

    private sealed class GatedFinalSampleProvider(float[] samples) : ISampleProvider, IDisposable
    {
        private int _read;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        internal TaskCompletionSource ReadEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim AllowReadToReturn { get; } = new();

        public int Read(Span<float> buffer)
        {
            if (Interlocked.Exchange(ref _read, 1) != 0)
            {
                return 0;
            }

            samples.CopyTo(buffer);
            ReadEntered.TrySetResult();
            if (!AllowReadToReturn.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The final source read was not released.");
            }

            return samples.Length;
        }

        public void Dispose() => AllowReadToReturn.Dispose();
    }
}
