using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;

namespace Soundrel.Services;

internal sealed class AudioTrackSource : ISampleProvider, IDisposable
{
    private const int OutputSampleRate = 48_000;
    private const int OutputChannels = 2;

    private readonly object _readLock = new();
    private readonly LoopingWaveStream _loopingReader;
    private readonly ISampleProvider _provider;
    private readonly Action<AudioTrackSource> _ended;
    private long _samplesRead;
    private int _activated;
    private int _endObserved;
    private int _endQueued;
    private int _faulted;
    private int _suppressed;
    private int _looping;
    private bool _disposed;

    private AudioTrackSource(
        WaveStream reader,
        LoopingWaveStream loopingReader,
        ISampleProvider provider,
        long playbackId,
        TimeSpan duration,
        Action<AudioTrackSource> ended)
    {
        _loopingReader = loopingReader;
        _provider = provider;
        _ended = ended;
        PlaybackId = playbackId;
        Duration = duration;
        SourceWaveFormat = reader.WaveFormat;
        ReaderType = reader.GetType();
    }

    public WaveFormat WaveFormat => _provider.WaveFormat;

    public long PlaybackId { get; }

    public TimeSpan Duration { get; }

    public long SamplesRead => Interlocked.Read(ref _samplesRead);

    internal WaveFormat SourceWaveFormat { get; }

    internal Type ReaderType { get; }

    internal bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    internal bool IsSuppressed => Volatile.Read(ref _suppressed) != 0;

    internal static AudioTrackSource Open(
        string filePath,
        long playbackId,
        Action<AudioTrackSource> ended)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(ended);

        var extension = Path.GetExtension(filePath);
        WaveStream? reader = null;

        try
        {
            reader = extension.ToLowerInvariant() switch
            {
                ".wav" => new WaveFileReader(filePath),
                ".mp3" => OpenMp3(filePath),
                _ => throw new NotSupportedException(
                    $"Audio file '{filePath}' has unsupported extension '{extension}'. Only WAV and MP3 are supported."),
            };

            var sourceFormat = reader.WaveFormat.AsStandardWaveFormat();
            ValidateFormat(filePath, sourceFormat);
            if (reader.Length <= 0 || reader.TotalTime <= TimeSpan.Zero)
            {
                throw new InvalidDataException(
                    $"Audio file '{filePath}' contains no audio data.");
            }

            var loopingReader = new LoopingWaveStream(reader);
            var provider = CreateProvider(loopingReader, sourceFormat);

            var source = new AudioTrackSource(
                reader,
                loopingReader,
                provider,
                playbackId,
                reader.TotalTime,
                ended);
            reader = null;
            return source;
        }
        catch (Exception exception) when (
            exception is not FileNotFoundException and
            not DirectoryNotFoundException and
            not UnauthorizedAccessException and
            not NotSupportedException and
            not InvalidDataException)
        {
            throw new InvalidDataException($"Could not decode audio file '{filePath}'.", exception);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    internal static AudioTrackSource FromWaveStreamForTests(
        WaveStream reader,
        long playbackId,
        Action<AudioTrackSource> ended)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(ended);

        var sourceFormat = reader.WaveFormat.AsStandardWaveFormat();
        ValidateFormat("test stream", sourceFormat);
        var loopingReader = new LoopingWaveStream(reader);
        var provider = CreateProvider(loopingReader, sourceFormat);
        return new AudioTrackSource(
            reader,
            loopingReader,
            provider,
            playbackId,
            reader.TotalTime,
            ended);
    }

    public int Read(Span<float> buffer)
    {
        int read;
        lock (_readLock)
        {
            if (_disposed)
            {
                return 0;
            }

            read = _provider.Read(buffer);
            if (read < 0 || read > buffer.Length)
            {
                throw new InvalidOperationException("The source returned an invalid sample count.");
            }
        }

        if (read > 0)
        {
            Interlocked.Add(ref _samplesRead, read);
        }

        if (Volatile.Read(ref _looping) == 0 && buffer.Length > 0 && read < buffer.Length)
        {
            Interlocked.Exchange(ref _endObserved, 1);
            QueueEndIfReady();
        }
        else if (Volatile.Read(ref _looping) != 0 && buffer.Length > 0 && read < buffer.Length)
        {
            TryMarkFaulted();
            Interlocked.Exchange(ref _endObserved, 1);
            QueueEndIfReady();
        }

        return read;
    }

    internal TimeSpan GetPosition()
    {
        var samplesRead = SamplesRead;
        if (Volatile.Read(ref _looping) != 0)
        {
            var durationSamples = (long)Math.Round(
                Duration.TotalSeconds * OutputSampleRate * OutputChannels,
                MidpointRounding.AwayFromZero);
            if (durationSamples > 0)
            {
                samplesRead %= durationSamples;
            }
        }

        var seconds = (double)samplesRead / (OutputSampleRate * OutputChannels);
        var position = TimeSpan.FromSeconds(seconds);
        return position <= Duration ? position : Duration;
    }

    internal void Activate()
    {
        Volatile.Write(ref _activated, 1);
        QueueEndIfReady();
    }

    internal void Suppress()
    {
        Volatile.Write(ref _suppressed, 1);
    }

    internal void Unsuppress()
    {
        Volatile.Write(ref _suppressed, 0);
    }

    internal bool TryMarkFaulted() => Interlocked.Exchange(ref _faulted, 1) == 0;

    internal void ConfigureLooping()
    {
        Volatile.Write(ref _looping, 1);
        _loopingReader.EnableLooping();
    }

    public void Dispose()
    {
        lock (_readLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _loopingReader.Dispose();
        }
    }

    private static WaveStream OpenMp3(string filePath)
    {
        var builder = new Mp3FileReader.FrameDecompressorBuilder(
            waveFormat => new Mp3FrameDecompressor(waveFormat));
        return new Mp3FileReaderBase(filePath, builder);
    }

    private static ISampleProvider CreateProvider(WaveStream reader, WaveFormat sourceFormat)
    {
        ISampleProvider provider = reader.ToSampleProvider();
        if (sourceFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }

        if (sourceFormat.SampleRate != OutputSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, OutputSampleRate);
        }

        return provider;
    }

    private static void ValidateFormat(string filePath, WaveFormat format)
    {
        if (format.Channels is not (1 or 2))
        {
            throw new NotSupportedException(
                $"Audio file '{filePath}' has {format.Channels} channels; only mono and stereo are supported.");
        }

        var supported = format.Encoding switch
        {
            WaveFormatEncoding.Pcm => format.BitsPerSample is 8 or 16 or 24 or 32,
            WaveFormatEncoding.IeeeFloat => format.BitsPerSample is 32 or 64,
            _ => false,
        };

        if (!supported)
        {
            throw new NotSupportedException(
                $"Audio file '{filePath}' uses unsupported format {format.Encoding} " +
                $"({format.BitsPerSample}-bit, {format.SampleRate} Hz).");
        }
    }

    private void QueueEndIfReady()
    {
        if (Volatile.Read(ref _activated) == 0 ||
            Volatile.Read(ref _endObserved) == 0 ||
            Interlocked.Exchange(ref _endQueued, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static source => source._ended(source),
            this,
            preferLocal: false);
    }
}

internal sealed class LoopingWaveStream : WaveStream
{
    private readonly WaveStream _source;
    private readonly object _gate = new();
    private int _looping;
    private bool _disposed;

    internal LoopingWaveStream(WaveStream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public override WaveFormat WaveFormat => _source.WaveFormat;

    public override long Length => _source.Length;

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                return _source.Position;
            }
        }
        set
        {
            lock (_gate)
            {
                _source.Position = value;
            }
        }
    }

    internal void EnableLooping()
    {
        Volatile.Write(ref _looping, 1);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
        {
            throw new ArgumentOutOfRangeException();
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count == 0)
            {
                return 0;
            }

            var total = 0;
            var emptyRewinds = 0;
            while (total < count)
            {
                var read = _source.Read(buffer, offset + total, count - total);
                if (read > 0)
                {
                    total += read;
                    emptyRewinds = 0;
                    continue;
                }

                if (Volatile.Read(ref _looping) == 0 ||
                    _source.Length == 0 ||
                    ++emptyRewinds > 1)
                {
                    return total;
                }

                _source.Position = 0;
            }

            return total;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_gate)
        {
            return _source.Seek(offset, origin);
        }
    }

    public override void SetLength(long value)
    {
        lock (_gate)
        {
            _source.SetLength(value);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Looping audio streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
