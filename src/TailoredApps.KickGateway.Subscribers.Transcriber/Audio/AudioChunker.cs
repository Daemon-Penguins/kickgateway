namespace TailoredApps.KickGateway.Subscribers.Transcriber.Audio;

/// <summary>
/// Accumulates decoded PCM and cuts it into Whisper-sized chunks. Once at least
/// <c>targetSeconds</c> are buffered, the cut is placed in the middle of the quietest 100 ms frame
/// found in the last <c>searchWindowSeconds</c> before the target, so chunk boundaries fall on
/// pauses rather than mid-word. Pure and synchronous — the caller serializes access.
/// </summary>
public sealed class AudioChunker
{
    private readonly int _rate;
    private readonly int _targetSamples;
    private readonly int _minSamples;
    private readonly int _searchSamples;
    private readonly int _frameSamples;

    private float[] _buf;
    private int _len;
    private long _startSample; // absolute position of _buf[0] on the channel's audio clock

    public AudioChunker(int sampleRate, double targetSeconds, double minSeconds,
        double searchWindowSeconds = 5, double frameSeconds = 0.1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (targetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetSeconds));
        if (minSeconds < 0 || minSeconds > targetSeconds) throw new ArgumentOutOfRangeException(nameof(minSeconds));

        _rate = sampleRate;
        _targetSamples = (int)(targetSeconds * sampleRate);
        _minSamples = Math.Max(1, (int)(minSeconds * sampleRate));
        _searchSamples = (int)(searchWindowSeconds * sampleRate);
        _frameSamples = Math.Max(1, (int)(frameSeconds * sampleRate));
        _buf = new float[_targetSamples + sampleRate * 2];
    }

    /// <summary>Samples currently buffered (not yet emitted).</summary>
    public int Buffered => _len;

    public double BufferedSeconds => (double)_len / _rate;

    /// <summary>Absolute sample position of the end of the buffered audio.</summary>
    public long Position => _startSample + _len;

    /// <summary>Appends samples and emits every full chunk that became available.</summary>
    public void Append(ReadOnlySpan<float> samples, List<AudioChunk> output)
    {
        if (samples.IsEmpty) return;
        EnsureCapacity(_len + samples.Length);
        samples.CopyTo(_buf.AsSpan(_len));
        _len += samples.Length;

        while (_len >= _targetSamples)
            output.Add(Cut(FindCutPoint()));
    }

    /// <summary>
    /// Emits whatever is buffered — when at least the minimum length is present, or anything at
    /// all when <paramref name="force"/> is set. Returns null if nothing was emitted.
    /// </summary>
    public AudioChunk? Flush(bool force = false)
    {
        if (_len == 0) return null;
        if (!force && _len < _minSamples) return null;
        return Cut(_len);
    }

    /// <summary>Drops any buffered audio and moves the clock to <paramref name="position"/> (used when the decoder is restarted).</summary>
    public void Reset(long position)
    {
        _len = 0;
        _startSample = position;
    }

    private int FindCutPoint()
    {
        var windowStart = Math.Max(_minSamples, _targetSamples - _searchSamples);
        var windowEnd = _targetSamples;
        if (windowEnd - windowStart < _frameSamples) return _targetSamples;

        var bestStart = -1;
        var bestEnergy = double.MaxValue;
        for (var start = windowStart; start + _frameSamples <= windowEnd; start += _frameSamples)
        {
            double energy = 0;
            var span = _buf.AsSpan(start, _frameSamples);
            foreach (var s in span) energy += (double)s * s;
            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                bestStart = start;
            }
        }

        return bestStart < 0 ? _targetSamples : bestStart + _frameSamples / 2;
    }

    private AudioChunk Cut(int count)
    {
        var chunk = new AudioChunk(_buf.AsSpan(0, count).ToArray(), _startSample, _startSample + count);
        var remaining = _len - count;
        if (remaining > 0) Buffer.BlockCopy(_buf, count * sizeof(float), _buf, 0, remaining * sizeof(float));
        _len = remaining;
        _startSample += count;
        return chunk;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        var size = _buf.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buf, size);
    }
}
