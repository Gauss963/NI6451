using System.Runtime.InteropServices;
using System.Text;

namespace Ni6451.Daq;

/// <summary>
/// Raw P/Invoke surface for the NI-DAQmx C API (<c>nicaiu.dll</c>), covering exactly the
/// calls this application needs. It replaces the <c>nidaqmx</c> Python package used on the
/// <c>main</c> branch; the same driver is doing the work either way, so a device that is
/// visible in NI MAX is visible here.
///
/// <c>nicaiu.dll</c> is installed by the NI-DAQmx driver into the system directory, so it
/// is resolved by name and is deliberately not redistributed with the application.
///
/// All entry points are <c>__stdcall</c> and take ANSI strings, matching NIDAQmx.h.
/// <c>TaskHandle</c> is <c>void*</c> in current driver versions, hence <see cref="nint"/>.
/// </summary>
internal static class NiDaqmx
{
    private const string Dll = "nicaiu.dll";
    private const CallingConvention Conv = CallingConvention.StdCall;

    // ---------- constants (NIDAQmx.h) ----------
    public const int Val_Cfg_Default = -1;
    public const int Val_RSE = 10083;
    public const int Val_NRSE = 10078;
    public const int Val_Diff = 10106;

    public const int Val_Volts = 10348;

    public const int Val_Rising = 10280;
    public const int Val_Falling = 10171;

    public const int Val_FiniteSamps = 10178;
    public const int Val_ContSamps = 10123;

    /// <summary>Non-interleaved: all samples of channel 0, then all of channel 1, ...</summary>
    public const int Val_GroupByChannel = 0;

    public const int Val_GroupByScanNumber = 1;

    public const int Val_ChanPerLine = 0;
    public const int Val_ChanForAllLines = 1;

    public const int Val_Acquired_Into_Buffer = 1;
    public const int Val_Transferred_From_Buffer = 2;

    /// <summary>Run the callback on the registering thread instead of a driver worker thread.</summary>
    public const uint Val_SynchronousEventCallbacks = 1u << 0;

    // ---------- callback ----------
    [UnmanagedFunctionPointer(Conv)]
    public delegate int EveryNSamplesEventCallback(
        nint taskHandle, int everyNsamplesEventType, uint nSamples, nint callbackData);

    // ---------- task lifecycle ----------
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    public static extern int DAQmxCreateTask(string taskName, out nint taskHandle);

    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxStartTask(nint taskHandle);

    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxStopTask(nint taskHandle);

    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxClearTask(nint taskHandle);

    // ---------- channel setup ----------
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    public static extern int DAQmxCreateAIVoltageChan(
        nint taskHandle,
        string physicalChannel,
        string? nameToAssignToChannel,
        int terminalConfig,
        double minVal,
        double maxVal,
        int units,
        string? customScaleName);

    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    public static extern int DAQmxCreateDIChan(
        nint taskHandle, string lines, string? nameToAssignToLines, int lineGrouping);

    // ---------- timing / triggering ----------
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    public static extern int DAQmxCfgSampClkTiming(
        nint taskHandle, string? source, double rate, int activeEdge, int sampleMode, ulong sampsPerChan);

    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    public static extern int DAQmxCfgDigEdgeStartTrig(
        nint taskHandle, string triggerSource, int triggerEdge);

    // ---------- events ----------
    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxRegisterEveryNSamplesEvent(
        nint taskHandle,
        int everyNsamplesEventType,
        uint nSamples,
        uint options,
        EveryNSamplesEventCallback? callbackFunction,
        nint callbackData);

    // ---------- reads ----------
    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxReadAnalogF64(
        nint taskHandle,
        int numSampsPerChan,
        double timeout,
        int fillMode,
        [Out] double[] readArray,
        uint arraySizeInSamps,
        out int sampsPerChanRead,
        nint reserved);

    [DllImport(Dll, CallingConvention = Conv)]
    public static extern int DAQmxReadDigitalLines(
        nint taskHandle,
        int numSampsPerChan,
        double timeout,
        int fillMode,
        [Out] byte[] readArray,
        uint arraySizeInBytes,
        out int sampsPerChanRead,
        out int numBytesPerSamp,
        nint reserved);

    // ---------- system / errors ----------
    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int DAQmxGetSysDevNames([Out] byte[] data, uint bufferSize);

    [DllImport(Dll, CallingConvention = Conv, CharSet = CharSet.Ansi)]
    private static extern int DAQmxGetExtendedErrorInfo([Out] byte[] errorString, uint bufferSize);

    // ---------- helpers ----------

    /// <summary>Comma-separated device names as the driver reports them, e.g. "Dev1, Dev2".</summary>
    public static string GetSystemDeviceNames()
    {
        var buffer = new byte[4096];
        Check(DAQmxGetSysDevNames(buffer, (uint)buffer.Length));
        return DecodeAnsi(buffer);
    }

    /// <summary>
    /// The driver's description of the most recent error on the calling thread. DAQmx status
    /// codes on their own are opaque, so this text is what actually gets shown to the user.
    /// </summary>
    public static string GetExtendedErrorInfo()
    {
        try
        {
            var buffer = new byte[8192];
            int status = DAQmxGetExtendedErrorInfo(buffer, (uint)buffer.Length);
            return status < 0 ? string.Empty : DecodeAnsi(buffer);
        }
        catch (DllNotFoundException)
        {
            return string.Empty;
        }
        catch (EntryPointNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Throw on a DAQmx failure. Positive status codes are warnings and are ignored, which
    /// matches the default behaviour of the Python bindings.
    /// </summary>
    public static void Check(int status)
    {
        if (status >= 0) return;
        throw new DaqmxException(status, GetExtendedErrorInfo());
    }

    private static string DecodeAnsi(byte[] buffer)
    {
        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0) len = buffer.Length;
        return Encoding.ASCII.GetString(buffer, 0, len).Trim();
    }
}
