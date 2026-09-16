namespace Ni6451.Core;

/// <summary>
/// A fixed-capacity circular buffer for multi-channel time series data, used to
/// back the live plot display. Once capacity is reached, the oldest samples are
/// overwritten by new ones.
///
/// Storage is a single flat channel-major array (row <c>c</c> occupies
/// <c>[c * Capacity, (c + 1) * Capacity)</c>), which is the same memory layout the
/// NumPy <c>(n_channels, capacity)</c> array had in the Python implementation.
///
/// Instances are not thread-safe; callers that push from a DAQ callback thread and
/// read from the UI thread must serialise access themselves (see <c>LiveTraceView</c>).
/// </summary>
public sealed class RollingBuffer
{
    private readonly double[] _buf;
    private int _writeIdx;
    private int _filled;

    public RollingBuffer(int nChannels, int capacity)
    {
        if (nChannels <= 0) throw new ArgumentOutOfRangeException(nameof(nChannels));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        long total = (long)nChannels * capacity;
        if (total > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacity), "nChannels * capacity exceeds the maximum array length.");

        ChannelCount = nChannels;
        Capacity = capacity;
        _buf = new double[total];
    }

    public int ChannelCount { get; }

    public int Capacity { get; }

    /// <summary>Samples per channel currently held (saturates at <see cref="Capacity"/>).</summary>
    public int Filled => _filled;

    public void Reset()
    {
        Array.Clear(_buf);
        _writeIdx = 0;
        _filled = 0;
    }

    /// <summary>
    /// Append <paramref name="nNew"/> samples per channel. <paramref name="data"/> is
    /// channel-major with a row stride of <paramref name="nNew"/>, i.e. exactly the
    /// layout of a NumPy array of shape <c>(ChannelCount, nNew)</c>.
    /// </summary>
    public void Push(ReadOnlySpan<double> data, int nNew)
    {
        if (nNew <= 0) return;
        if (data.Length < ChannelCount * nNew)
            throw new ArgumentException("Source span is smaller than ChannelCount * nNew.", nameof(data));

        if (nNew >= Capacity)
        {
            // Only the tail fits; keep the most recent Capacity samples.
            int offset = nNew - Capacity;
            for (int c = 0; c < ChannelCount; c++)
                data.Slice(c * nNew + offset, Capacity).CopyTo(_buf.AsSpan(c * Capacity, Capacity));

            _writeIdx = 0;
            _filled = Capacity;
            return;
        }

        int end = _writeIdx + nNew;
        if (end <= Capacity)
        {
            for (int c = 0; c < ChannelCount; c++)
                data.Slice(c * nNew, nNew).CopyTo(_buf.AsSpan(c * Capacity + _writeIdx, nNew));
        }
        else
        {
            int firstPart = Capacity - _writeIdx;
            int secondPart = nNew - firstPart;
            for (int c = 0; c < ChannelCount; c++)
            {
                data.Slice(c * nNew, firstPart).CopyTo(_buf.AsSpan(c * Capacity + _writeIdx, firstPart));
                data.Slice(c * nNew + firstPart, secondPart).CopyTo(_buf.AsSpan(c * Capacity, secondPart));
            }
        }

        _writeIdx = end % Capacity;
        _filled = Math.Min(Capacity, _filled + nNew);
    }

    /// <summary>
    /// Copy the last <paramref name="n"/> samples per channel into <paramref name="dest"/>,
    /// oldest first, channel-major with a row stride equal to the return value. Returns
    /// fewer than <paramref name="n"/> if the buffer has not filled up that much yet.
    /// </summary>
    public int GetLast(int n, double[] dest)
    {
        ArgumentNullException.ThrowIfNull(dest);

        n = Math.Min(n, _filled);
        if (n <= 0) return 0;
        if (dest.Length < ChannelCount * n)
            throw new ArgumentException("Destination array is smaller than ChannelCount * n.", nameof(dest));

        int idx = _writeIdx - n;
        if (idx < 0) idx += Capacity;

        if (idx + n <= Capacity)
        {
            for (int c = 0; c < ChannelCount; c++)
                Array.Copy(_buf, c * Capacity + idx, dest, c * n, n);
        }
        else
        {
            int firstPart = Capacity - idx;
            int secondPart = n - firstPart;
            for (int c = 0; c < ChannelCount; c++)
            {
                Array.Copy(_buf, c * Capacity + idx, dest, c * n, firstPart);
                Array.Copy(_buf, c * Capacity, dest, c * n + firstPart, secondPart);
            }
        }

        return n;
    }
}
