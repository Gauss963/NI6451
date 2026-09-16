namespace Ni6451.Core;

/// <summary>
/// All tunable parameters live here. Adjust sample rate / chunk size / flush
/// interval / display settings by editing this file only -- no need to touch
/// any logic code.
///
/// This is the C# port of the Python <c>config.py</c> on the <c>main</c> branch.
/// Values are kept numerically identical so that recordings produced by either
/// implementation are interchangeable.
/// </summary>
public static class AppConfig
{
    public const int NChannels = 16;

    /// <summary>Samples/s per channel (max rate for 16 single-ended channels on this hardware).</summary>
    public const int Rate = 500_000;

    /// <summary>Samples per DAQmx "every N samples acquired" callback.</summary>
    public const int Chunk = 5_000;

    public const int FlushIntervalSec = 10;

    /// <summary>Samples/channel accumulated before the spool files are flushed to the OS.</summary>
    public const long FlushSamples = (long)Rate * FlushIntervalSec;

    /// <summary>
    /// How often the spool files are additionally fsync'd all the way to the drive.
    ///
    /// The two cadences protect against different failures. The 10 s flush pushes data out of
    /// the process into the OS page cache, which is what survives an application crash -- the
    /// likely case, and the one the Python version could lose up to 10 s of data to because it
    /// held that much in RAM. The fsync here is what additionally survives a power cut, and it
    /// is deliberately slower: forcing ~2 GB of dirty pages to the drive can stall for longer
    /// than the writer queue can absorb, which would push back on the acquisition itself.
    /// </summary>
    public const int DurableFlushIntervalSec = 30;

    public const long DurableFlushSamples = (long)Rate * DurableFlushIntervalSec;

    /// <summary>
    /// Chunks the spool writer may fall behind by before the DAQ callback is made to wait.
    /// At 16 channels this is about 2 s of acquisition, or 128 MB of pooled buffers.
    /// </summary>
    public const int WriteQueueCapacity = 200;

    /// <summary>Channel count at or above which per-channel spool writes are fanned out across threads.</summary>
    public const int ParallelWriteThreshold = 8;

    /// <summary>UI redraw interval in ms (20 fps), decoupled from the DAQ callback rate.</summary>
    public const int PlotRefreshMs = 50;

    /// <summary>Decimated sample rate kept in the live-plot rolling buffer.</summary>
    public const int DisplayRateHz = 2000;

    /// <summary>
    /// Hard cap on points actually rendered per channel per frame, regardless of the
    /// selected time window. This is what keeps redraw cost bounded even at a 30 s window.
    /// </summary>
    public const int MaxPlotPoints = 2000;

    public const int MinWindowSec = 1;
    public const int MaxWindowSec = 30;
    public const int DefaultWindowSec = 5;

    public const double MinYRange = 0.1;
    public const double MaxYRange = 10.0;
    public const double DefaultYRange = 10.0;

    // ---- live-plot palette ----
    // Two sets, because the window follows the Windows light/dark setting. The light trace
    // colour is the original Matplotlib C0 blue; the dark one is lifted to keep the same
    // contrast ratio against a dark plot face.

    public const string ChannelOnColor = "#1F77B4";
    public const string ChannelOffColor = "#B0B0B0";
    public const string ChannelOffFaceColor = "#ECECEC";
    public const string ChannelFaceColor = "#FFFFFF";
    public const string ChannelFrameColor = "#C8C8C8";
    public const string ChannelZeroLineColor = "#E4E4E4";
    public const string ChannelLabelColor = "#6E6E6E";

    public const string ChannelOnColorDark = "#4DA6E8";
    public const string ChannelOffColorDark = "#5A5A5A";
    public const string ChannelOffFaceColorDark = "#232323";
    public const string ChannelFaceColorDark = "#1B1B1B";
    public const string ChannelFrameColorDark = "#3A3A3A";
    public const string ChannelZeroLineColorDark = "#2E2E2E";
    public const string ChannelLabelColorDark = "#9A9A9A";

    /// <summary>External TTL trigger capture (e.g. from another DAQ's Trigger Out). PFI0.</summary>
    public const string DefaultTriggerLine = "port0/line0";

    public const bool DefaultCaptureTrigger = true;

    /// <summary>Keep every Nth full-rate sample for the live display.</summary>
    public static int DecimationStride => Math.Max(1, Rate / DisplayRateHz);

    /// <summary>Rolling-buffer capacity, in decimated samples per channel.</summary>
    public static int BufferCapacity => MaxWindowSec * DisplayRateHz;

    /// <summary>Driver-side DAQmx buffer, ~5 seconds worth of headroom.</summary>
    public static ulong DriverBufferSamples => (ulong)Rate * 5;
}
