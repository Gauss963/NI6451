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

    /// <summary>Live-plot styling for channels that are turned off (not being acquired).</summary>
    public const string ChannelOnColor = "#1F77B4";

    public const string ChannelOffColor = "#B0B0B0";
    public const string ChannelOffFaceColor = "#E8E8E8";

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
