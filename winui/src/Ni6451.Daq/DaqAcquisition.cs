using System.Buffers;
using System.Collections.Concurrent;
using Ni6451.Core;

namespace Ni6451.Daq;

/// <summary>What a finished acquisition leaves behind for <see cref="FinalizeJob"/> to merge.</summary>
public sealed record AcquisitionResult(
    string TempDir,
    long SamplesPerChannel,
    IReadOnlyList<int> Channels,
    long? TriggerSampleIndex);

/// <summary>
/// Creates and controls the DAQmx task(s), using only the channels the user selected in
/// the UI. Optionally also opens a second DI task, sample-clock-synced to the AI task, to
/// record which AI sample index lines up with the first rising edge of an external TTL
/// trigger (e.g. another DAQ's Trigger Out).
///
/// This is the C# port of the Python <c>daq_worker.py</c>, with one deliberate change:
/// instead of accumulating ten seconds of data in RAM and writing it in one burst (which
/// peaked around 640 MB for 16 channels), every chunk is handed to a background writer
/// thread through a bounded queue and spooled to disk immediately. The queue provides the
/// same decoupling the old design got from buffering, without the memory spike, and keeps
/// the driver's callback thread from ever blocking on disk I/O.
///
/// Notes:
///   - AI channels use RSE (single-ended) mode with a +/-10 V range.
///   - The device name should match what NI MAX reports for your hardware.
///   - Only channels the user actually connected should be selected -- leaving unused
///     channels in the scan list exposes them to multiplexer ghosting from neighbouring
///     active channels.
///   - Trigger capture: the DI task's sample clock and start trigger are both locked to
///     the AI task's ("/{device}/ai/SampleClock", "/{device}/ai/StartTrigger"), so DI
///     sample N and AI sample N were taken at the same instant. Because many external
///     trigger sources latch high after firing rather than pulsing per event, only the
///     first rising edge is recorded.
/// </summary>
public sealed class DaqAcquisition : IDisposable
{
    /// <summary>Roughly two seconds of chunks; provides backpressure if the disk falls behind.</summary>
    private const int WriteQueueCapacity = 200;

    private readonly object _latestLock = new();

    private nint _aiTask;
    private nint _diTask;

    // The driver keeps a raw function pointer to this delegate, so the managed instance
    // must stay reachable for as long as the task exists or the GC will collect it and
    // the next callback will jump into freed memory.
    private NiDaqmx.EveryNSamplesEventCallback? _callback;

    private ChannelSpool? _spool;
    private int[] _channels = [];

    private BlockingCollection<PendingChunk>? _writeQueue;
    private Thread? _writerThread;
    private volatile string? _writerError;

    private byte[] _diBuffer = [];
    private double[] _latestVoltages = [];

    // Touched only from the DAQmx callback thread.
    private long _sampleCounter;
    private bool _triggerLastValue;

    private long _triggerSampleIndex = -1;

    /// <summary>
    /// Raised on the DAQmx callback thread for every acquired chunk, with a channel-major
    /// buffer (row stride = the sample count) that is only valid for the duration of the
    /// call. Handlers must copy anything they need and must not block.
    /// </summary>
    public event Action<ReadOnlyMemory<double>, int>? ChunkReady;

    /// <summary>Raised when the driver or the spool writer fails. May fire on a background thread.</summary>
    public event Action<string>? Error;

    public bool IsRunning => _aiTask != 0;

    /// <summary>Active AI channel numbers for the current or most recent run.</summary>
    public IReadOnlyList<int> Channels => _channels;

    /// <summary>Whether trigger capture was requested for the current run.</summary>
    public bool CaptureTrigger { get; private set; }

    /// <summary>
    /// AI sample index of the first trigger rising edge, or null if trigger capture is off
    /// or no edge has been seen yet.
    /// </summary>
    public long? TriggerSampleIndex
    {
        get
        {
            long v = Interlocked.Read(ref _triggerSampleIndex);
            return v < 0 ? null : v;
        }
    }

    /// <summary>Most recent sample for <paramref name="channel"/>, for the live sensor readout.</summary>
    public bool TryGetLatestVoltage(int channel, out double voltage)
    {
        lock (_latestLock)
        {
            int pos = Array.IndexOf(_channels, channel);
            if (pos < 0 || pos >= _latestVoltages.Length)
            {
                voltage = 0;
                return false;
            }

            voltage = _latestVoltages[pos];
            return true;
        }
    }

    // ---------- acquisition control ----------

    /// <summary>
    /// Start acquiring. On failure the <see cref="Error"/> event fires, everything opened so
    /// far is torn down, and <see cref="IsRunning"/> stays false.
    /// </summary>
    public void Start(
        string device,
        string saveDir,
        IReadOnlyList<int> channels,
        bool captureTrigger = false,
        string triggerLine = AppConfig.DefaultTriggerLine)
    {
        if (_aiTask != 0)
        {
            Error?.Invoke("A previous acquisition task is still active. Stop it before starting a new one.");
            return;
        }

        _channels = channels.ToArray();
        int nActive = _channels.Length;
        if (nActive == 0)
        {
            Error?.Invoke("Select at least one channel to acquire.");
            return;
        }

        CaptureTrigger = captureTrigger;
        Interlocked.Exchange(ref _triggerSampleIndex, -1);
        _triggerLastValue = false;
        _sampleCounter = 0;
        _writerError = null;
        _diBuffer = new byte[AppConfig.Chunk];

        lock (_latestLock)
            _latestVoltages = new double[nActive];

        try
        {
            _spool = new ChannelSpool(saveDir, _channels);

            _writeQueue = new BlockingCollection<PendingChunk>(WriteQueueCapacity);
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "ni6451-spool-writer",
                Priority = ThreadPriority.AboveNormal,
            };
            _writerThread.Start();

            NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out _aiTask));
            foreach (int ch in _channels)
            {
                NiDaqmx.Check(NiDaqmx.DAQmxCreateAIVoltageChan(
                    _aiTask, $"{device}/ai{ch}", null,
                    NiDaqmx.Val_RSE, -10.0, 10.0, NiDaqmx.Val_Volts, null));
            }

            NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                _aiTask, null, AppConfig.Rate, NiDaqmx.Val_Rising,
                NiDaqmx.Val_ContSamps, AppConfig.DriverBufferSamples));

            if (captureTrigger)
            {
                NiDaqmx.Check(NiDaqmx.DAQmxCreateTask(string.Empty, out _diTask));
                NiDaqmx.Check(NiDaqmx.DAQmxCreateDIChan(
                    _diTask, $"{device}/{triggerLine}", null, NiDaqmx.Val_ChanPerLine));

                // Lock the DI sample clock and start to the AI task's, so DI sample N and
                // AI sample N are taken at the same instant.
                NiDaqmx.Check(NiDaqmx.DAQmxCfgSampClkTiming(
                    _diTask, $"/{device}/ai/SampleClock", AppConfig.Rate, NiDaqmx.Val_Rising,
                    NiDaqmx.Val_ContSamps, AppConfig.DriverBufferSamples));

                NiDaqmx.Check(NiDaqmx.DAQmxCfgDigEdgeStartTrig(
                    _diTask, $"/{device}/ai/StartTrigger", NiDaqmx.Val_Rising));
            }

            _callback = OnEveryNSamples;
            NiDaqmx.Check(NiDaqmx.DAQmxRegisterEveryNSamplesEvent(
                _aiTask, NiDaqmx.Val_Acquired_Into_Buffer, AppConfig.Chunk, 0, _callback, 0));

            if (captureTrigger)
                NiDaqmx.Check(NiDaqmx.DAQmxStartTask(_diTask));   // arms, waits for the AI start trigger

            NiDaqmx.Check(NiDaqmx.DAQmxStartTask(_aiTask));       // fires the shared start trigger
        }
        catch (Exception e) when (e is DaqmxException or DllNotFoundException or IOException or UnauthorizedAccessException)
        {
            Error?.Invoke(e.Message);
            AbortAfterFailedStart();
        }
    }

    /// <summary>
    /// Stop the DAQmx task(s) and flush any remaining buffered data to disk. This is fast
    /// (no large I/O) -- merging the spool files into a single <c>.npz</c> is handled
    /// separately by <see cref="FinalizeJob"/> so it can run in the background without
    /// blocking the UI. Returns null if nothing was recorded.
    /// </summary>
    public AcquisitionResult? StopAcquisition()
    {
        // Order matters: stop the AI task first so no further callbacks are queued, then
        // clear it (which waits for any in-flight callback to return -- the writer thread
        // is still draining at this point, so a callback blocked on a full queue can
        // always make progress), and only then shut the writer down.
        StopAndClear(ref _aiTask);
        ShutdownWriter();
        StopAndClear(ref _diTask);

        GC.KeepAlive(_callback);
        _callback = null;

        ChannelSpool? spool = _spool;
        _spool = null;
        if (spool is null) return null;

        spool.FlushToDisk();
        spool.CloseFiles();

        string? writerError = _writerError;
        if (writerError is not null)
            Error?.Invoke($"Spool write error: {writerError}");

        if (spool.TotalSamplesWritten == 0)
        {
            spool.RemoveTempDir();
            return null;
        }

        return new AcquisitionResult(
            spool.TempDir, spool.TotalSamplesWritten, _channels.ToArray(), TriggerSampleIndex);
    }

    // ---------- DAQmx callback ----------

    private int OnEveryNSamples(nint taskHandle, int eventType, uint nSamples, nint callbackData)
    {
        int n = (int)nSamples;
        int nActive = _channels.Length;
        double[] buffer = ArrayPool<double>.Shared.Rent(nActive * n);
        bool handedOff = false;

        try
        {
            NiDaqmx.Check(NiDaqmx.DAQmxReadAnalogF64(
                _aiTask, n, 10.0, NiDaqmx.Val_GroupByChannel,
                buffer, (uint)(nActive * n), out int samplesRead, 0));

            // The read is blocking and asks for exactly n samples/channel, so a short read
            // means the task was torn down underneath us. The row stride DAQmx used is n
            // regardless, so a partial buffer cannot be interpreted safely -- drop it.
            if (samplesRead != n) return 0;

            lock (_latestLock)
            {
                for (int c = 0; c < nActive && c < _latestVoltages.Length; c++)
                    _latestVoltages[c] = buffer[c * n + n - 1];
            }

            ChunkReady?.Invoke(new ReadOnlyMemory<double>(buffer, 0, nActive * n), n);

            BlockingCollection<PendingChunk>? queue = _writeQueue;
            if (queue is not null)
            {
                queue.Add(new PendingChunk(buffer, n, nActive));
                handedOff = true;
            }

            if (CaptureTrigger && _diTask != 0)
                ReadTriggerChunk(n);

            _sampleCounter += n;
        }
        catch (Exception e) when (e is DaqmxException or InvalidOperationException or ObjectDisposedException)
        {
            // InvalidOperationException/ObjectDisposedException come from Add() racing a
            // shutdown; those are expected during teardown and not worth surfacing.
            if (e is DaqmxException) Error?.Invoke(e.Message);
        }
        finally
        {
            if (!handedOff) ArrayPool<double>.Shared.Return(buffer);
        }

        return 0;   // DAQmx requires the callback to return a status
    }

    /// <summary>
    /// Reads (drains) the DI task every callback so its buffer never overflows. Only
    /// bothers looking for the rising edge until the first one is found -- most external
    /// trigger sources latch high after firing rather than pulsing per event, so there is
    /// nothing more to find after that.
    /// </summary>
    private void ReadTriggerChunk(int nSamples)
    {
        if (_diBuffer.Length < nSamples) _diBuffer = new byte[nSamples];

        int read;
        try
        {
            NiDaqmx.Check(NiDaqmx.DAQmxReadDigitalLines(
                _diTask, nSamples, 10.0, NiDaqmx.Val_GroupByChannel,
                _diBuffer, (uint)_diBuffer.Length, out read, out _, 0));
        }
        catch (DaqmxException e)
        {
            Error?.Invoke($"Trigger input read error: {e.Message}");
            return;
        }

        if (read <= 0) return;
        if (Interlocked.Read(ref _triggerSampleIndex) >= 0)
        {
            _triggerLastValue = _diBuffer[read - 1] != 0;
            return;
        }

        // Detect rising edges, including one that straddles the previous chunk boundary.
        bool previous = _triggerLastValue;
        for (int i = 0; i < read; i++)
        {
            bool current = _diBuffer[i] != 0;
            if (!previous && current)
            {
                Interlocked.Exchange(ref _triggerSampleIndex, _sampleCounter + i);
                break;
            }

            previous = current;
        }

        _triggerLastValue = _diBuffer[read - 1] != 0;
    }

    // ---------- spool writer ----------

    private void WriterLoop()
    {
        BlockingCollection<PendingChunk>? queue = _writeQueue;
        ChannelSpool? spool = _spool;
        if (queue is null || spool is null) return;

        try
        {
            foreach (PendingChunk chunk in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (_writerError is null)
                        spool.Write(chunk.Buffer.AsSpan(0, chunk.ChannelCount * chunk.SampleCount), chunk.SampleCount);
                }
                finally
                {
                    ArrayPool<double>.Shared.Return(chunk.Buffer);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Record and keep draining the queue, so the DAQ callback never deadlocks on a
            // full queue after the writer has given up.
            _writerError = e.Message;
        }
    }

    private void ShutdownWriter()
    {
        BlockingCollection<PendingChunk>? queue = _writeQueue;
        Thread? thread = _writerThread;
        _writeQueue = null;
        _writerThread = null;

        if (queue is null) return;

        queue.CompleteAdding();
        thread?.Join(TimeSpan.FromSeconds(30));

        // Anything still queued after the join deadline would otherwise leak pooled arrays.
        while (queue.TryTake(out PendingChunk leftover))
            ArrayPool<double>.Shared.Return(leftover.Buffer);

        queue.Dispose();
    }

    private void AbortAfterFailedStart()
    {
        StopAndClear(ref _aiTask);
        StopAndClear(ref _diTask);
        ShutdownWriter();
        GC.KeepAlive(_callback);
        _callback = null;

        ChannelSpool? spool = _spool;
        _spool = null;
        spool?.CloseFiles();
        spool?.RemoveTempDir();
    }

    private static void StopAndClear(ref nint task)
    {
        nint handle = task;
        if (handle == 0) return;
        task = 0;

        try { NiDaqmx.DAQmxStopTask(handle); } catch (DllNotFoundException) { }
        try { NiDaqmx.DAQmxClearTask(handle); } catch (DllNotFoundException) { }
    }

    public void Dispose()
    {
        if (IsRunning) StopAcquisition();
        _spool?.Dispose();
    }

    private readonly record struct PendingChunk(double[] Buffer, int SampleCount, int ChannelCount);
}
