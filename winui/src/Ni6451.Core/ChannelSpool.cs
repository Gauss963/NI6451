using System.Runtime.InteropServices;

namespace Ni6451.Core;

/// <summary>
/// Owns the temporary per-channel raw files that acquired data is streamed into, so
/// full-rate data is never held entirely in RAM. One <c>ai{n}.raw</c> file per active
/// channel, holding bare little-endian <c>float64</c> samples with no header --
/// byte-identical to the layout the Python implementation spooled, and to the payload
/// of the corresponding <c>.npy</c> member in the final archive.
/// </summary>
public sealed class ChannelSpool : IDisposable
{
    private readonly FileStream[] _files;
    private readonly int[] _channels;
    private long _samplesSinceFlush;
    private bool _filesClosed;

    /// <param name="saveDir">Folder the temp directory is created inside.</param>
    /// <param name="channels">Active AI channel numbers, e.g. [0, 3, 7].</param>
    public ChannelSpool(string saveDir, IReadOnlyList<int> channels)
    {
        ArgumentNullException.ThrowIfNull(saveDir);
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0) throw new ArgumentException("At least one channel is required.", nameof(channels));

        _channels = channels.ToArray();
        TempDir = Path.Combine(saveDir, $"_tmp_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(TempDir);

        _files = new FileStream[_channels.Length];
        try
        {
            for (int i = 0; i < _channels.Length; i++)
                _files[i] = new FileStream(ChannelPath(i), FileMode.Create, FileAccess.Write, FileShare.Read,
                                           bufferSize: 1 << 20);
        }
        catch
        {
            CloseFiles();
            RemoveTempDir();
            throw;
        }
    }

    public string TempDir { get; }

    public IReadOnlyList<int> Channels => _channels;

    /// <summary>Samples per channel handed to <see cref="Write"/> so far.</summary>
    public long TotalSamplesWritten { get; private set; }

    /// <summary><paramref name="position"/> is the index into <see cref="Channels"/>, not the AI channel number.</summary>
    public string ChannelPath(int position) => Path.Combine(TempDir, $"ai{_channels[position]}.raw");

    /// <summary>
    /// Append <paramref name="nSamples"/> samples per channel. <paramref name="data"/> is
    /// channel-major with a row stride of <paramref name="nSamples"/>, in the same channel
    /// order as <see cref="Channels"/>.
    /// </summary>
    public void Write(ReadOnlySpan<double> data, int nSamples)
    {
        if (nSamples <= 0) return;
        ObjectDisposedException.ThrowIf(_filesClosed, this);
        if (data.Length < _channels.Length * nSamples)
            throw new ArgumentException("Source span is smaller than Channels.Count * nSamples.", nameof(data));

        for (int c = 0; c < _channels.Length; c++)
            _files[c].Write(MemoryMarshal.AsBytes(data.Slice(c * nSamples, nSamples)));

        TotalSamplesWritten += nSamples;
        _samplesSinceFlush += nSamples;

        // Push the OS write-behind cache along on the same cadence the Python version
        // used for its 10-second bulk writes, so a crash loses at most that much data.
        if (_samplesSinceFlush >= AppConfig.FlushSamples)
        {
            FlushToDisk();
            _samplesSinceFlush = 0;
        }
    }

    public void FlushToDisk()
    {
        if (_filesClosed) return;
        foreach (FileStream f in _files)
            f.Flush(flushToDisk: false);
    }

    public void CloseFiles()
    {
        if (_filesClosed) return;
        _filesClosed = true;
        foreach (FileStream? f in _files)
        {
            try { f?.Dispose(); }
            catch (IOException) { /* nothing useful to do while tearing down */ }
        }
    }

    /// <summary>
    /// Delete the raw files and the temp directory. Failures are swallowed: leftover temp
    /// files are harmless and can be removed manually, and cleanup must never break the
    /// main acquisition flow.
    /// </summary>
    public void RemoveTempDir()
    {
        try
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                string p = ChannelPath(i);
                if (File.Exists(p)) File.Delete(p);
            }

            if (Directory.Exists(TempDir)) Directory.Delete(TempDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // leave for manual cleanup
        }
    }

    public void Dispose() => CloseFiles();
}
