using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Ni6451.Core;

/// <summary>
/// Callback for <see cref="NpzReader.StreamFloat64"/>. A named delegate rather than
/// <c>Action&lt;ReadOnlySpan&lt;double&gt;&gt;</c> because a ref struct cannot be a generic
/// type argument.
/// </summary>
public delegate void Float64BlockHandler(ReadOnlySpan<double> block);

/// <summary>
/// Reads back the <c>.npz</c> archives this application writes. Used by the
/// <c>Ni6451.Tools</c> command line utility and by the format self-test; the
/// acquisition path itself never needs it.
/// </summary>
public sealed class NpzReader : IDisposable
{
    private readonly ZipArchive _zip;

    public NpzReader(string path)
    {
        _zip = ZipFile.OpenRead(path);
    }

    /// <summary>Array names in the archive, without the <c>.npy</c> suffix.</summary>
    public IEnumerable<string> Names => _zip.Entries
        .Where(e => e.Name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
        .Select(e => e.Name[..^4]);

    public NpyHeader GetHeader(string name)
    {
        using Stream s = OpenArray(name);
        return NpyFormat.ReadHeader(s);
    }

    /// <summary>Read a 0-D or 1-D <c>int64</c> array. Returns the first element for a scalar.</summary>
    public long ReadInt64Scalar(string name)
    {
        long[] v = ReadInt64Array(name);
        if (v.Length == 0) throw new InvalidDataException($"Array '{name}' is empty.");
        return v[0];
    }

    /// <summary>
    /// Read a 0-D or 1-D integer array, widening <c>&lt;i4</c> to <see cref="long"/>.
    /// The narrower dtype matters in practice: NumPy 1.x on Windows makes
    /// <c>np.array(500000)</c> an <c>int32</c>, so recordings written by the Python
    /// application on a lab machine store <c>sample_rate</c>, <c>channels</c> and
    /// <c>trigger_sample_index</c> as <c>&lt;i4</c> rather than the <c>&lt;i8</c> this
    /// application writes.
    /// </summary>
    public long[] ReadInt64Array(string name)
    {
        using Stream s = OpenArray(name);
        NpyHeader h = NpyFormat.ReadHeader(s);

        var result = new long[h.Count];
        switch (h.Descr)
        {
            case NpyFormat.Int64Descr:
                s.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
                break;

            case NpyFormat.Int32Descr:
                var narrow = new int[h.Count];
                s.ReadExactly(MemoryMarshal.AsBytes(narrow.AsSpan()));
                for (long i = 0; i < h.Count; i++) result[i] = narrow[i];
                break;

            default:
                throw new NotSupportedException(
                    $"Array '{name}' has dtype '{h.Descr}', expected '{NpyFormat.Int64Descr}' or '{NpyFormat.Int32Descr}'.");
        }

        return result;
    }

    /// <summary>
    /// Read a 1-D <c>float64</c> array in full. Only safe for arrays small enough to fit
    /// in memory; use <see cref="StreamFloat64"/> for full recordings.
    /// </summary>
    public double[] ReadFloat64Array(string name)
    {
        using Stream s = OpenArray(name);
        NpyHeader h = NpyFormat.ReadHeader(s);
        if (h.Descr != NpyFormat.Float64Descr)
            throw new NotSupportedException($"Array '{name}' has dtype '{h.Descr}', expected '{NpyFormat.Float64Descr}'.");
        if (h.Count > int.MaxValue / sizeof(double))
            throw new InvalidOperationException($"Array '{name}' is too large to read into memory.");

        var result = new double[h.Count];
        s.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    /// <summary>
    /// Stream a 1-D <c>float64</c> array in blocks, so arbitrarily long recordings can be
    /// processed without loading them fully. The span handed to <paramref name="onBlock"/>
    /// is only valid for the duration of the callback.
    /// </summary>
    public long StreamFloat64(string name, int blockSamples, Float64BlockHandler onBlock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSamples, 1);
        ArgumentNullException.ThrowIfNull(onBlock);

        using Stream s = OpenArray(name);
        NpyHeader h = NpyFormat.ReadHeader(s);
        if (h.Descr != NpyFormat.Float64Descr)
            throw new NotSupportedException($"Array '{name}' has dtype '{h.Descr}', expected '{NpyFormat.Float64Descr}'.");

        var block = new double[blockSamples];
        long remaining = h.Count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, blockSamples);
            s.ReadExactly(MemoryMarshal.AsBytes(block.AsSpan(0, want)));
            onBlock(block.AsSpan(0, want));
            remaining -= want;
        }

        return h.Count;
    }

    private Stream OpenArray(string name)
    {
        ZipArchiveEntry entry = _zip.GetEntry(name + ".npy")
            ?? throw new FileNotFoundException($"Array '{name}' is not present in the archive.");
        return entry.Open();
    }

    public void Dispose() => _zip.Dispose();
}
