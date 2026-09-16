using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ni6451.Core;

namespace Ni6451.App.Controls;

/// <summary>
/// Real-time display of the 16 AI channels, each as its own row (checkbox + small line
/// plot), arranged in a left column (ai0-ai7) and a right column (ai8-ai15). Port of the
/// Python <c>plot_widget.py</c>, including the channel selector that <c>channel_select.py</c>
/// provided separately -- the checkbox lives next to its own plot.
///
/// Design notes:
///   - The DAQ callback fires roughly every 10 ms, far faster than any GUI can usefully
///     redraw, so the data arriving at <see cref="PushChunk"/> has already been decimated to
///     <see cref="AppConfig.DisplayRateHz"/> by the acquisition's monitor stage. It is stored
///     in a <see cref="RollingBuffer"/>, and that buffer is what gets plotted. Redraw cost is
///     therefore independent of the acquisition rate, and an arbitrary time window costs no
///     extra memory.
///   - Decimation only affects what is shown on screen. The full-rate data spooled to disk is
///     untouched.
///   - <see cref="PushChunk"/> runs on the acquisition's monitor thread while
///     <see cref="OnRedraw"/> runs on the UI thread, so every access to the rolling buffer is
///     taken under <c>_bufferLock</c>. Crucially the monitor thread is not the hardware
///     callback thread: if the UI holds this lock while copying out a 30 s frame, the delay
///     lands on the plot, never on the recording.
/// </summary>
public sealed partial class LiveTraceView : UserControl, IDisposable
{
    private readonly ChannelPlot[] _channelPlots = new ChannelPlot[AppConfig.NChannels];
    private readonly DispatcherTimer _timer = new();
    private readonly object _bufferLock = new();

    /// <summary>Scratch space for one channel's strided points, reused across channels and frames.</summary>
    private readonly double[] _renderScratch = new double[AppConfig.MaxPlotPoints];

    private RollingBuffer? _buffer;
    private int[] _activeChannels = [];

    /// <summary>Scratch space for the samples pulled out of the rolling buffer each frame.</summary>
    private double[] _frameScratch = [];

    private int _windowSec = AppConfig.DefaultWindowSec;
    private bool _disposed;

    public LiveTraceView()
    {
        InitializeComponent();

        WindowBox.Minimum = AppConfig.MinWindowSec;
        WindowBox.Maximum = AppConfig.MaxWindowSec;
        WindowBox.Value = AppConfig.DefaultWindowSec;

        YRangeBox.Minimum = AppConfig.MinYRange;
        YRangeBox.Maximum = AppConfig.MaxYRange;
        YRangeBox.Value = AppConfig.DefaultYRange;

        int half = AppConfig.NChannels / 2;
        for (int row = 0; row < half; row++)
            ChannelGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (int ch = 0; ch < AppConfig.NChannels; ch++)
        {
            var plot = new ChannelPlot(ch);
            plot.EnabledChanged += OnChannelToggled;
            Grid.SetRow(plot, ch % half);
            Grid.SetColumn(plot, ch < half ? 0 : 1);
            ChannelGrid.Children.Add(plot);
            _channelPlots[ch] = plot;
        }

        UpdateSelectionSummary();

        _timer.Interval = TimeSpan.FromMilliseconds(AppConfig.PlotRefreshMs);
        _timer.Tick += OnRedraw;
    }

    /// <summary>Raised whenever the set of checked channels changes.</summary>
    public event EventHandler? ChannelSelectionChanged;

    /// <summary>AI channel numbers currently being acquired, ascending.</summary>
    public IReadOnlyList<int> ActiveChannels => _activeChannels;

    // ---------- public API ----------

    /// <summary>Currently checked channel numbers, in ascending order.</summary>
    public int[] EnabledChannels()
        => _channelPlots.Where(p => p.IsChannelEnabled).Select(p => p.Channel).ToArray();

    /// <summary>Disable the checkboxes while an acquisition is running.</summary>
    public void SetLocked(bool locked)
    {
        foreach (ChannelPlot p in _channelPlots) p.SetLocked(locked);
        SelectAllButton.IsEnabled = !locked;
        SelectNoneButton.IsEnabled = !locked;
    }

    public void Start(IReadOnlyList<int> enabledChannels)
    {
        _activeChannels = enabledChannels.ToArray();

        lock (_bufferLock)
        {
            _buffer = new RollingBuffer(_activeChannels.Length, AppConfig.BufferCapacity);
            _frameScratch = new double[_activeChannels.Length * AppConfig.BufferCapacity];
        }

        foreach (ChannelPlot p in _channelPlots) p.Clear();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();

        // Drop the buffer too: chunks can still arrive between the next Start() on the DAQ
        // side and Start() here, and pushing them into a buffer sized for the previous run's
        // channel count would plot nonsense for those few milliseconds.
        lock (_bufferLock) _buffer = null;
    }

    /// <summary>
    /// Append already-decimated, channel-major data (row stride = <paramref name="nSamples"/>)
    /// in the same channel order as <see cref="ActiveChannels"/>. Called from the acquisition's
    /// monitor thread.
    /// </summary>
    public void PushChunk(ReadOnlySpan<double> chunk, int nSamples)
    {
        lock (_bufferLock)
        {
            if (_buffer is null || nSamples <= 0) return;
            if (chunk.Length < _buffer.ChannelCount * nSamples) return;

            _buffer.Push(chunk, nSamples);
        }
    }

    // ---------- internal ----------

    private void OnChannelToggled(object? sender, bool enabled)
    {
        UpdateSelectionSummary();
        ChannelSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectionSummary()
    {
        if (SelectionSummary is null) return;
        int n = _channelPlots.Count(p => p is not null && p.IsChannelEnabled);
        SelectionSummary.Text = $"{n} of {AppConfig.NChannels} channels";
    }

    private void OnWindowChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = _windowSec;
            return;
        }

        _windowSec = (int)Math.Clamp(args.NewValue, AppConfig.MinWindowSec, AppConfig.MaxWindowSec);
    }

    private void OnYRangeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = AppConfig.DefaultYRange;
            return;
        }

        double range = Math.Clamp(args.NewValue, AppConfig.MinYRange, AppConfig.MaxYRange);
        foreach (ChannelPlot p in _channelPlots) p.SetYRange(range);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (ChannelPlot p in _channelPlots) p.SetChecked(true);
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (ChannelPlot p in _channelPlots) p.SetChecked(false);
    }

    private void OnRedraw(object? sender, object e)
    {
        int n;
        int nChannels;
        double[] frame;

        lock (_bufferLock)
        {
            if (_buffer is null || _activeChannels.Length == 0) return;

            nChannels = _buffer.ChannelCount;
            frame = _frameScratch;
            n = _buffer.GetLast(_windowSec * AppConfig.DisplayRateHz, frame);
        }

        if (n == 0) return;

        // Cap the number of points actually rendered, regardless of the selected time
        // window -- this is what keeps redraw cost bounded even at a 30 s window. The stride
        // rounds up (the Python version truncated), so the cap is a genuine upper bound
        // instead of one that could still emit ~2x MaxPlotPoints for some window sizes.
        int stride = Math.Max(1, (n + AppConfig.MaxPlotPoints - 1) / AppConfig.MaxPlotPoints);
        int rendered = Math.Min(_renderScratch.Length, (n + stride - 1) / stride);

        for (int c = 0; c < nChannels && c < _activeChannels.Length; c++)
        {
            int channel = _activeChannels[c];
            int srcBase = c * n;
            for (int i = 0, s = 0; i < rendered; i++, s += stride)
                _renderScratch[i] = frame[srcBase + s];

            _channelPlots[channel].UpdateData(_renderScratch.AsSpan(0, rendered));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        foreach (ChannelPlot p in _channelPlots) p.Dispose();
    }
}
