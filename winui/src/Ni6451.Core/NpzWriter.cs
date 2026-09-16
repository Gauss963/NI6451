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
    ///
    /// Reads are double-buffered: the next block is pulled from the spool file with overlapped
    /// I/O while the current one is being written into the archive, so a merge is bounded by
    /// the slower of the two devices instead of by their sum. On a multi-gigabyte recording
    /// that roughly halves the wait.
    /// </summary>
    /// <param name="onBytesWritten">Invoked per block with the byte count, for progress reporting.</param>
    public void AddFloat64FromRawFile(string name, string rawPath, long count, Action<int>? onBytesWritten = null)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Float64Descr, count);

        long remaining = count * sizeof(double);
        using var src = new FileStream(rawPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                       bufferSize: 0, options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        byte[] front = new byte[CopyBlockBytes];
        byte[] back = new byte[CopyBlockBytes];

        Task<int> pending = ReadBlockAsync(src, front, (int)Math.Min(remaining, front.Length));
        while (remaining > 0)
        {
            int n = pending.GetAwaiter().GetResult();
            if (n == 0) throw new EndOfStreamException($"Spool file '{rawPath}' is shorter than the recorded sample count.");

            remaining -= n;

            // Start the next read before writing, so the two overlap.
            pending = ReadBlockAsync(src, back, (int)Math.Min(remaining, back.Length));

            entry.Write(front, 0, n);
            onBytesWritten?.Invoke(n);

            (front, back) = (back, front);
        }
    }

    private static async Task<int> ReadBlockAsync(Stream source, byte[] buffer, int count)
    {
        if (count <= 0) return 0;
        await source.ReadExactlyAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
        return count;
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
