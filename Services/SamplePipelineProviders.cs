using NAudio.Wave;

namespace Soundrel.Services;

internal sealed class SampleDelayGateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private long _remainingSamples;

    internal SampleDelayGateSampleProvider(ISampleProvider source, long delaySamples)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (delaySamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(delaySamples));
        }

        _source = source;
        _remainingSamples = delaySamples;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    internal long RemainingSamples => Interlocked.Read(ref _remainingSamples);

    public int Read(Span<float> buffer)
    {
        var remaining = Interlocked.Read(ref _remainingSamples);
        if (remaining == 0)
        {
            return _source.Read(buffer);
        }

        var delayedSamples = (int)Math.Min(remaining, buffer.Length);
        buffer[..delayedSamples].Clear();
        Interlocked.Add(ref _remainingSamples, -delayedSamples);
        if (delayedSamples == buffer.Length)
        {
            return buffer.Length;
        }

        var sourceRead = _source.Read(buffer[delayedSamples..]);
        if ((uint)sourceRead > (uint)(buffer.Length - delayedSamples))
        {
            throw new InvalidOperationException("The source returned an invalid sample count.");
        }

        return delayedSamples + sourceRead;
    }
}

internal sealed class PauseGateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _paused;

    internal PauseGateSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    internal bool IsPaused => Volatile.Read(ref _paused) != 0;

    internal void SetPaused(bool paused)
    {
        Volatile.Write(ref _paused, paused ? 1 : 0);
    }

    public int Read(Span<float> buffer)
    {
        if (Volatile.Read(ref _paused) != 0)
        {
            buffer.Clear();
            return buffer.Length;
        }

        var samplesRead = _source.Read(buffer);
        if ((uint)samplesRead > (uint)buffer.Length)
        {
            throw new InvalidOperationException("The source returned an invalid sample count.");
        }

        return samplesRead;
    }
}

internal enum GainRampCurve
{
    Linear,
    EqualPowerIncoming,
    EqualPowerOutgoing,
}

internal sealed class GainEnvelopeSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _stateGate = new();
    private readonly int _channels;
    private float _currentGain;
    private float _fixedGain;
    private float _frameGain;
    private float _rampStartGain;
    private float _rampTargetGain;
    private long _rampSamplesRendered;
    private long _rampTotalSamples;
    private int _channelInFrame;
    private GainRampCurve _rampCurve;
    private Action? _rampCompleted;
    private long _rampVersion;
    private long _queuedCompletionVersion;
    private bool _rampPending;
    private bool _rampActive;
    private bool _paused;

    internal GainEnvelopeSampleProvider(ISampleProvider source, float initialGain = 1f)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.SampleRate <= 0 || source.WaveFormat.Channels <= 0)
        {
            throw new ArgumentException("The source must have a valid sample rate and channel count.", nameof(source));
        }

        _source = source;
        _channels = source.WaveFormat.Channels;
        _currentGain = ClampGain(initialGain);
        _fixedGain = _currentGain;
        _frameGain = _currentGain;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    internal float CurrentGain
    {
        get
        {
            lock (_stateGate)
            {
                return _currentGain;
            }
        }
    }

    internal bool IsRampActive
    {
        get
        {
            lock (_stateGate)
            {
                return _rampActive;
            }
        }
    }

    internal void SetPaused(bool paused)
    {
        lock (_stateGate)
        {
            _paused = paused;
        }
    }

    internal void SetGain(float gain)
    {
        gain = ClampGain(gain);

        lock (_stateGate)
        {
            _rampVersion++;
            _rampCompleted = null;
            _queuedCompletionVersion = 0;
            _fixedGain = gain;
            _rampActive = false;
            _rampPending = false;

            // A split frame keeps its original gain; immediate assignments take
            // effect at the next frame boundary so all channels remain matched.
            if (_channelInFrame == 0)
            {
                _currentGain = gain;
                _frameGain = gain;
            }
        }
    }

    internal void RampTo(
        float targetGain,
        TimeSpan duration,
        GainRampCurve curve = GainRampCurve.Linear,
        Action? completed = null)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        if (curve is not GainRampCurve.Linear and
            not GainRampCurve.EqualPowerIncoming and
            not GainRampCurve.EqualPowerOutgoing)
        {
            throw new ArgumentOutOfRangeException(nameof(curve));
        }

        targetGain = ClampGain(targetGain);
        var totalSamples = DurationToSamples(duration, WaveFormat.SampleRate, _channels);
        long completedVersion = 0;

        // Commands and sample processing share this gate so retargeting snapshots
        // the last gain actually observed by every channel of the current frame.
        lock (_stateGate)
        {
            _rampVersion++;
            _rampCompleted = completed;
            _queuedCompletionVersion = 0;
            if (totalSamples == 0)
            {
                _fixedGain = targetGain;
                _rampActive = false;
                _rampPending = false;
                if (_channelInFrame == 0)
                {
                    _currentGain = targetGain;
                    _frameGain = targetGain;
                }

                completedVersion = QueueCompletionNoLock();
            }
            else
            {
                _rampStartGain = _currentGain;
                _rampTargetGain = targetGain;
                _rampCurve = curve;
                _rampSamplesRendered = 0;
                _rampTotalSamples = totalSamples;
                _fixedGain = targetGain;
                _rampPending = _channelInFrame != 0;
                _rampActive = true;
            }
        }

        if (completedVersion != 0)
        {
            QueueCompletion(completedVersion);
        }
    }

    public int Read(Span<float> buffer)
    {
        int samplesRead;
        long completedVersion = 0;

        // The engine's wrapped source only decodes and queues EOF work from Read;
        // it never disposes or invokes completion inline. Holding this short state
        // gate through decode and gain application prevents retirement from muting
        // a short final buffer after it has already been decoded.
        lock (_stateGate)
        {
            samplesRead = _source.Read(buffer);
            if ((uint)samplesRead > (uint)buffer.Length)
            {
                throw new InvalidOperationException("The source returned an invalid sample count.");
            }

            for (var index = 0; index < samplesRead; index++)
            {
                if (_channelInFrame == 0)
                {
                    BeginFrame();
                }

                buffer[index] *= _frameGain;
                _channelInFrame++;

                if (_channelInFrame == _channels)
                {
                    _channelInFrame = 0;
                    var justCompletedVersion = CompleteFrame();
                    if (justCompletedVersion != 0)
                    {
                        completedVersion = justCompletedVersion;
                    }
                }
            }
        }

        if (completedVersion != 0)
        {
            QueueCompletion(completedVersion);
        }

        return samplesRead;
    }

    private static float ClampGain(float gain) =>
        float.IsNaN(gain) ? 0f : Math.Clamp(gain, 0f, 1f);

    private static long DurationToSamples(TimeSpan duration, int sampleRate, int channels)
    {
        var exactFrames = (decimal)duration.Ticks * sampleRate / TimeSpan.TicksPerSecond;
        var wholeFrames = decimal.Round(exactFrames, 0, MidpointRounding.AwayFromZero);
        var interleavedSamples = wholeFrames * channels;
        if (interleavedSamples > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return decimal.ToInt64(interleavedSamples);
    }

    private void BeginFrame()
    {
        if (_paused)
        {
            _frameGain = _currentGain;
            return;
        }

        if (!_rampActive)
        {
            _frameGain = _fixedGain;
            _currentGain = _frameGain;
            return;
        }

        _rampPending = false;
        if (_rampTotalSamples == _channels ||
            _rampSamplesRendered == _rampTotalSamples - _channels)
        {
            _frameGain = _rampTargetGain;
        }
        else if (_rampSamplesRendered == 0)
        {
            _frameGain = _rampStartGain;
        }
        else
        {
            var progress = (double)_rampSamplesRendered / (_rampTotalSamples - _channels);
            var interpolation = _rampCurve switch
            {
                GainRampCurve.Linear => progress,
                GainRampCurve.EqualPowerIncoming => Math.Sin(progress * Math.PI / 2),
                GainRampCurve.EqualPowerOutgoing => 1 - Math.Cos(progress * Math.PI / 2),
                _ => throw new InvalidOperationException("The active gain ramp has an invalid curve."),
            };
            _frameGain = ClampGain(
                _rampStartGain + ((_rampTargetGain - _rampStartGain) * (float)interpolation));
        }

        _currentGain = _frameGain;
    }

    private long CompleteFrame()
    {
        if (_paused || !_rampActive || _rampPending)
        {
            return 0;
        }

        _rampSamplesRendered += _channels;
        if (_rampSamplesRendered < _rampTotalSamples)
        {
            return 0;
        }

        _rampActive = false;
        _currentGain = _rampTargetGain;
        _frameGain = _rampTargetGain;
        return QueueCompletionNoLock();
    }

    private long QueueCompletionNoLock()
    {
        if (_rampCompleted is null)
        {
            return 0;
        }

        _queuedCompletionVersion = _rampVersion;
        return _rampVersion;
    }

    private void QueueCompletion(long version)
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Provider.InvokeCompletion(state.Version),
            (Provider: this, Version: version),
            preferLocal: false);
    }

    private void InvokeCompletion(long version)
    {
        Action? completed;
        lock (_stateGate)
        {
            if (version != _rampVersion || version != _queuedCompletionVersion)
            {
                return;
            }

            _queuedCompletionVersion = 0;
            completed = _rampCompleted;
            _rampCompleted = null;
        }

        completed?.Invoke();
    }
}

internal sealed class SampleSafetySampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    internal SampleSafetySampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var samplesRead = _source.Read(buffer);
        if ((uint)samplesRead > (uint)buffer.Length)
        {
            throw new InvalidOperationException("The source returned an invalid sample count.");
        }

        for (var index = 0; index < samplesRead; index++)
        {
            var sample = buffer[index];
            // This is a final finite output bound, not a headroom or
            // distortion-prevention stage.
            buffer[index] = float.IsFinite(sample)
                ? Math.Clamp(sample, -1f, 1f)
                : 0f;
        }

        return samplesRead;
    }
}
