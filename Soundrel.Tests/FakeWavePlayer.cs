using NAudio.Wave;

namespace Soundrel.Tests;

internal sealed class FakeWavePlayer : IWavePlayer
{
    [ThreadStatic]
    private static bool _isRaisingFault;

    private IWaveProvider? _provider;
    private PlaybackState _playbackState;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public int InitCount { get; private set; }

    public int PlayCount { get; private set; }

    public int PauseCount { get; private set; }

    public int StopCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool PumpToEndOnPlay { get; set; }

    public Exception? InitException { get; set; }

    public Exception? PlayException { get; set; }

    public Exception? PlaybackStoppedExceptionOnPlay { get; set; }

    public Exception? PauseException { get; set; }

    public Exception? StopException { get; set; }

    public bool RaiseFaultChangesPlaybackState { get; set; } = true;

    public float Volume { get; set; } = 1.0f;

    public PlaybackState PlaybackState => _playbackState;

    public WaveFormat OutputWaveFormat =>
        _provider?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    public static bool IsRaisingFault => _isRaisingFault;

    public void Init(IWaveProvider waveProvider)
    {
        InitCount++;
        if (InitException is not null)
        {
            throw InitException;
        }

        _provider = waveProvider;
    }

    public void Play()
    {
        PlayCount++;
        if (PlayException is not null)
        {
            throw PlayException;
        }

        _playbackState = PlaybackState.Playing;
        if (PlaybackStoppedExceptionOnPlay is not null)
        {
            RaiseFault(PlaybackStoppedExceptionOnPlay);
        }

        if (PumpToEndOnPlay)
        {
            PumpFrames(48_000);
        }
    }

    public void Pause()
    {
        PauseCount++;
        if (PauseException is not null)
        {
            throw PauseException;
        }

        _playbackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        StopCount++;
        if (StopException is not null)
        {
            throw StopException;
        }

        _playbackState = PlaybackState.Stopped;
    }

    public int PumpFrames(int frameCount)
    {
        var buffer = ReadFrames(frameCount);
        return buffer.Length * sizeof(float);
    }

    public float[] ReadFrames(int frameCount)
    {
        if (_provider is null || _playbackState != PlaybackState.Playing)
        {
            return [];
        }

        var buffer = new byte[frameCount * _provider.WaveFormat.BlockAlign];
        var bytesRead = _provider.Read(buffer);
        var samples = new float[bytesRead / sizeof(float)];
        Buffer.BlockCopy(buffer, 0, samples, 0, bytesRead);
        return samples;
    }

    public void RaiseFault(Exception exception)
    {
        if (RaiseFaultChangesPlaybackState)
        {
            _playbackState = PlaybackState.Stopped;
        }
        _isRaisingFault = true;
        try
        {
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
        }
        finally
        {
            _isRaisingFault = false;
        }
    }

    public void Dispose()
    {
        DisposeCount++;
        _playbackState = PlaybackState.Stopped;
    }
}
