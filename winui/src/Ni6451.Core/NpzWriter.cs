using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Ni6451.Core;

/// <summary>
/// Writes a NumPy <c>.npz</c> archive: an uncompressed (stored) ZIP whose members are
/// <c>.npy</c> files, one per named array. This is exactly what <c>numpy.savez</c>
/// produces, so the output of the WinUI application is a drop-in replacement for the
/// output of the Python application on the <c>main</c> branch.
///
/// Large channel arrays are streamed from their spool file straight into the archive
/// in fixed-size blocks, so a multi-gigabyte recording is never materialised in RAM
/// (the Python version relied on <c>np.memmap</c> for the same reason).
/// </summary>
public sealed class NpzWriter : IDisposable
{
    private const int CopyBlockBytes = 4 * 1024 * 1024;

    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private bool _disposed;

    public NpzWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: false);
    }

    /// <summary>
    /// Append a 1-D <c>float64</c> array read from a raw little-endian spool file.
    /// <paramref name="count"/> is the number of samples, not bytes.
    /// </summary>
    public void AddFloat64FromRawFile(string name, string rawPath, long count)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Float64Descr, count);

        long remaining = count * sizeof(double);
        using var src = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20);

        byte[] block = new byte[CopyBlockBytes];
        while (remaining > 0)
        {
            int want = (int)Math.Min(remaining, block.Length);
            src.ReadExactly(block, 0, want);
            entry.Write(block, 0, want);
            remaining -= want;
        }
    }

    /// <summary>Append a 1-D <c>float64</c> array held in memory.</summary>
    public void AddFloat64Array(string name, ReadOnlySpan<double> values)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Float64Descr, values.Length);
        entry.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>Append a 1-D <c>int64</c> array.</summary>
    public void AddInt64Array(string name, ReadOnlySpan<long> values)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Int64Descr, values.Length);
        entry.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>
    /// Append a 0-D <c>int64</c> array -- the shape <c>numpy.array(5)</c> produces, and
    /// what <c>read_example.py</c> expects for <c>sample_rate</c> and
    /// <c>trigger_sample_index</c>.
    /// </summary>
    public void AddInt64Scalar(string name, long value)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Int64Descr);
        Span<long> one = [value];
        entry.Write(MemoryMarshal.AsBytes(one));
    }

    private Stream CreateEntry(string name)
    {
        // np.savez stores members uncompressed; matching that keeps writes cheap at
        // 64 MB/s of incoming data and keeps the archive streamable.
        ZipArchiveEntry e = _zip.CreateEntry(name + ".npy", CompressionLevel.NoCompression);
        return e.Open();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _zip.Dispose();   // also closes _file (leaveOpen: false)
    }
}
