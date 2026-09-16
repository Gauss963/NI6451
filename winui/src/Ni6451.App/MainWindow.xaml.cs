using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ni6451.Core;
using Ni6451.Daq;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Ni6451.App;

/// <summary>
/// Assembles the UI (device selection, output folder, Start/Stop, live trace display with
/// inline channel checkboxes, live sensor readout) and wires user actions to
/// <see cref="DaqAcquisition"/> and <see cref="FinalizeJob"/>.
///
/// Port of the Python <c>main_window.py</c>.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>10 fps -- deliberately slower than the plot's own refresh rate.</summary>
    private const int ReadoutIntervalMs = 100;

    // Fixed sensor-to-channel wiring for the live readout.
    private const int ChNormalStress = 0;   // ai0
    private const int ChShearStress = 1;    // ai1
    private const int ChLvdt = 2;           // ai2

    // ai0 normal-stress setup: fault type -> available thicknesses, in (label, metres) pairs.
    // 2D is fixed at 50 cm inside UnitConversion itself, so the value passed for it doesn't matter.
    private static readonly (string Label, double Metres)[] Thickness1DOptions =
        [("5 cm", 0.05), ("10 cm", 0.10)];

    private static readonly (string Label, double Metres)[] Thickness2DOptions =
        [("50 cm (fixed, 9 pistons)", 0.50)];

    private static readonly SolidColorBrush TriggerNoBrush = new(Microsoft.UI.Colors.Red);
    private static readonly SolidColorBrush TriggerYesBrush = new(Color.FromArgb(255, 0, 90, 220));
    private static readonly SolidColorBrush ReadoutBrush = new(Color.FromArgb(255, 0, 90, 220));

    private readonly DaqAcquisition _daq = new();
    private readonly DispatcherTimer _readoutTimer = new();

    private string? _saveDir;
    private bool _errorDialogOpen;
    private bool _isFinalizing;
    private bool _isClosing;

    /// <summary>The off-thread merge itself, so shutdown can wait on it without needing the UI thread.</summary>
    private Task _finalizeWork = Task.CompletedTask;

    private string _currentSh = "0000";
    private int _currentRn;

    public MainWindow()
    {
        InitializeComponent();

        Title = "USB-6451 Continuous Acquisition";
        AppWindow.Resize(new SizeInt32(1180, 940));

        FixedParamsText.Text =
            $"Fixed: {AppConfig.Rate:N0} S/s/ch, chunk {AppConfig.Chunk:N0} samples, "
            + $"spooled to disk continuously (flushed every {AppConfig.FlushIntervalSec}s)";

        CaptureTriggerCheck.IsChecked = AppConfig.DefaultCaptureTrigger;
        TriggerLineBox.Text = AppConfig.DefaultTriggerLine;

        NormalStressText.Foreground = ReadoutBrush;
        ShearStressText.Foreground = ReadoutBrush;
        LvdtText.Foreground = ReadoutBrush;
        SetTriggerStatus(triggered: false);

        FaultTypeCombo.Items.Add("1D");
        FaultTypeCombo.Items.Add("2D");
        FaultTypeCombo.SelectedIndex = 0;   // also populates the thickness list

        ShBox.Text = "0207";
        RnBox.Minimum = 0;
        RnBox.Maximum = 999_999;
        RnBox.Value = 1;
        UpdateFileNamePreview();

        _daq.ChunkReady += OnChunkReady;
        _daq.Error += OnDaqError;

        _readoutTimer.Interval = TimeSpan.FromMilliseconds(ReadoutIntervalMs);
        _readoutTimer.Tick += OnReadoutTick;

        Closed += OnClosed;

        RefreshDevices();
    }

    private FrameworkElement Root => (FrameworkElement)Content;

    // ---------- device list ----------

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        string current = DeviceCombo.Text;
        IReadOnlyList<string> devices;
        try
        {
            devices = DeviceEnumerator.ListDevices();
        }
        catch (Exception ex) when (ex is DaqmxException or DllNotFoundException or BadImageFormatException)
        {
            StatusText.Text = $"Status: could not query NI-DAQmx devices ({ex.Message})";
            return;
        }

        DeviceCombo.Items.Clear();
        if (devices.Count == 0)
        {
            StatusText.Text = "Status: no NI-DAQmx devices found (check driver install / USB connection)";
            return;
        }

        foreach (string d in devices) DeviceCombo.Items.Add(d);
        DeviceCombo.SelectedItem = devices.Contains(current) ? current : devices[0];
        DeviceCombo.Text = DeviceCombo.SelectedItem as string ?? string.Empty;
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _saveDir = folder.Path;
        OutputFolderText.Text = folder.Path;
        StartButton.IsEnabled = !_isFinalizing && !_daq.IsRunning;
    }

    // ---------- fault setup / naming ----------

    private void OnFaultTypeChanged(object sender, SelectionChangedEventArgs e) => PopulateThicknessOptions();

    private void PopulateThicknessOptions()
    {
        if (FaultThicknessCombo is null) return;

        bool isOneD = CurrentFaultType == FaultType.OneD;
        (string Label, double Metres)[] options = isOneD ? Thickness1DOptions : Thickness2DOptions;

        FaultThicknessCombo.Items.Clear();
        foreach ((string label, _) in options) FaultThicknessCombo.Items.Add(label);
        FaultThicknessCombo.SelectedIndex = 0;
        FaultThicknessCombo.IsEnabled = isOneD && !_daq.IsRunning && !_isFinalizing;
    }

    private FaultType CurrentFaultType
        => (FaultTypeCombo.SelectedItem as string) == "2D" ? FaultType.TwoD : FaultType.OneD;

    private double CurrentFaultThicknessM
    {
        get
        {
            (string Label, double Metres)[] options =
                CurrentFaultType == FaultType.OneD ? Thickness1DOptions : Thickness2DOptions;
            int index = Math.Clamp(FaultThicknessCombo.SelectedIndex, 0, options.Length - 1);
            return options[index].Metres;
        }
    }

    private void OnNamingChanged(object sender, TextChangedEventArgs e)
    {
        // Stand-in for the Qt QIntValidator(0, 9999): keep the field digits-only.
        string digits = new(ShBox.Text.Where(char.IsAsciiDigit).ToArray());
        if (digits != ShBox.Text)
        {
            int caret = Math.Max(0, ShBox.SelectionStart - (ShBox.Text.Length - digits.Length));
            ShBox.Text = digits;
            ShBox.SelectionStart = Math.Min(caret, digits.Length);
            return;   // the assignment re-enters this handler
        }

        UpdateFileNamePreview();
    }

    private void OnRunNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = 0;
        UpdateFileNamePreview();
    }

    private string CurrentShPadded => ShBox.Text.Trim().PadLeft(4, '0');

    private int CurrentRn => double.IsNaN(RnBox.Value) ? 0 : (int)RnBox.Value;

    private void UpdateFileNamePreview()
    {
        if (FileNamePreviewText is null) return;
        FileNamePreviewText.Text = $"Will save as: T{CurrentShPadded}-raw-run{CurrentRn}-<timestamp>.npz";
    }

    // ---------- acquisition control ----------

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        string device = DeviceCombo.Text.Trim();
        if (string.IsNullOrEmpty(device))
        {
            await ShowMessageAsync("Notice", "Please select or enter a device name.");
            return;
        }

        if (_saveDir is null)
        {
            await ShowMessageAsync("Notice", "Please choose an output folder.");
            return;
        }

        int[] enabled = TraceView.EnabledChannels();
        if (enabled.Length == 0)
        {
            await ShowMessageAsync("Notice", "Select at least one channel to acquire.");
            return;
        }

        SetControlsLocked(true);

        // Capture the naming/fault settings now, so later edits don't retroactively affect
        // the file this run is about to produce.
        _currentSh = CurrentShPadded;
        _currentRn = CurrentRn;

        SetTriggerStatus(triggered: false);
        NormalStressText.Text = "Normal: -- MPa";
        ShearStressText.Text = "Shear: -- MPa";
        LvdtText.Text = "LVDT: -- mm";

        _daq.Start(
            device,
            _saveDir,
            enabled,
            CaptureTriggerCheck.IsChecked == true,
            TriggerLineBox.Text.Trim());

        if (_daq.IsRunning)
        {
            StopButton.IsEnabled = true;
            StatusText.Text = $"Status: acquiring ({AppConfig.Rate:N0} S/s x {enabled.Length}ch)";
            TraceView.Start(enabled);
            _readoutTimer.Start();
        }
        else
        {
            SetControlsLocked(false);
        }
    }

    private async void OnStop(object sender, RoutedEventArgs e) => await StopAcquisitionAsync();

    private async Task StopAcquisitionAsync()
    {
        StopButton.IsEnabled = false;
        TraceView.Stop();
        _readoutTimer.Stop();
        SetTriggerStatus(triggered: false);

        AcquisitionResult? result = _daq.StopAcquisition();
        if (result is null)
        {
            StatusText.Text = "Status: idle";
            SetControlsLocked(false);
            await ShowMessageAsync("Notice", "No data was acquired.");
            return;
        }

        StatusText.Text =
            $"Status: saving {result.SamplesPerChannel:N0} samples/channel in the background, please wait...";

        // Controls stay locked until the background save finishes, so a new acquisition
        // can't be started while the previous one is still being written.
        _isFinalizing = true;
        var request = new FinalizeRequest(
            result.TempDir, result.SamplesPerChannel, _saveDir!, result.Channels,
            result.TriggerSampleIndex, _currentSh, _currentRn);

        // Deliberately not awaited: Stop returns immediately and the status label is updated
        // when the merge lands, which is how the Qt version behaved with its FinalizeWorker
        // thread. Awaiting here would stall an error dialog behind a multi-minute save.
        _ = FinalizeAsync(request, result.TriggerSampleIndex, result.SamplesPerChannel);
    }

    private async Task FinalizeAsync(FinalizeRequest request, long? triggerSampleIndex, long nSamples)
    {
        try
        {
            Task<string> work = Task.Run(() => FinalizeJob.Run(request));
            _finalizeWork = work;
            string outPath = await work;

            string triggerText = triggerSampleIndex is { } idx ? $", trigger at sample {idx:N0}" : string.Empty;
            StatusText.Text = $"Status: saved {outPath} ({nSamples:N0} samples/channel{triggerText})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = "Status: error while saving";
            await ShowMessageAsync("Save Error", ex.Message);
        }
        finally
        {
            _isFinalizing = false;
            if (!_isClosing) SetControlsLocked(false);
        }
    }

    private void SetControlsLocked(bool locked)
    {
        ChooseFolderButton.IsEnabled = !locked;
        StartButton.IsEnabled = !locked && _saveDir is not null;
        DeviceCombo.IsEnabled = !locked;
        RefreshButton.IsEnabled = !locked;
        CaptureTriggerCheck.IsEnabled = !locked;
        TriggerLineBox.IsEnabled = !locked;
        FaultTypeCombo.IsEnabled = !locked;
        FaultThicknessCombo.IsEnabled = !locked && CurrentFaultType == FaultType.OneD;
        ShBox.IsEnabled = !locked;
        RnBox.IsEnabled = !locked;
        TraceView.SetLocked(locked);
        if (!locked) StopButton.IsEnabled = false;
    }

    // ---------- live data ----------

    /// <summary>Runs on the DAQmx callback thread; must not touch XAML.</summary>
    private void OnChunkReady(ReadOnlyMemory<double> chunk, int nSamples)
        => TraceView.PushChunk(chunk.Span, nSamples);

    private void OnReadoutTick(object? sender, object e)
    {
        SetTriggerStatus(_daq.TriggerSampleIndex is not null);

        // Read the fault setup once per tick: the Python version only evaluated it inside
        // the ai0 branch, so a run with ai0 deselected but ai1 selected raised NameError
        // when it came to format the shear stress.
        FaultType faultType = CurrentFaultType;
        double thicknessM = CurrentFaultThicknessM;

        NormalStressText.Text = _daq.TryGetLatestVoltage(ChNormalStress, out double v0)
            ? $"Normal: {UnitConversion.GetNormalStress(v0, faultType, thicknessM) / 1e6:F1} MPa"
            : "Normal: -- MPa";

        ShearStressText.Text = _daq.TryGetLatestVoltage(ChShearStress, out double v1)
            ? $"Shear: {UnitConversion.GetShearStress(v1, faultType, thicknessM) / 1e6:F1} MPa"
            : "Shear: -- MPa";

        LvdtText.Text = _daq.TryGetLatestVoltage(ChLvdt, out double v2)
            ? $"LVDT: {UnitConversion.GetLvdtDisplacement(v2) * 1000:F1} mm"
            : "LVDT: -- mm";
    }

    private void SetTriggerStatus(bool triggered)
    {
        TriggerStatusText.Text = triggered ? "Trigger: Yes" : "Trigger: No";
        TriggerStatusText.Foreground = triggered ? TriggerYesBrush : TriggerNoBrush;
    }

    // ---------- errors ----------

    /// <summary>May be raised on a background thread, so it hops to the UI thread first.</summary>
    private void OnDaqError(string message)
    {
        if (DispatcherQueue.HasThreadAccess) _ = HandleDaqErrorAsync(message);
        else DispatcherQueue.TryEnqueue(() => _ = HandleDaqErrorAsync(message));
    }

    private async Task HandleDaqErrorAsync(string message)
    {
        // Treat a hardware error the same as pressing Stop: properly close the task(s) and
        // try to salvage whatever was already captured, instead of leaving an orphaned task
        // running in the background with the Stop button disabled.
        if (_daq.IsRunning)
        {
            await StopAcquisitionAsync();
        }
        else
        {
            TraceView.Stop();
            _readoutTimer.Stop();
            if (!_isFinalizing) SetControlsLocked(false);
        }

        await ShowMessageAsync("DAQ Error", message);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        if (_errorDialogOpen || _isClosing) return;   // a dialog for a previous, likely related, error is showing

        _errorDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Root.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // The window is going away underneath the dialog; the message is not worth crashing over.
        }
        finally
        {
            _errorDialogOpen = false;
        }
    }

    // ---------- shutdown ----------

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _readoutTimer.Stop();
        TraceView.Stop();

        // Stop the hardware first so nothing keeps writing, then let any in-flight merge
        // finish -- abandoning it would leave a half-written .npz next to spool files.
        if (_daq.IsRunning) _daq.StopAcquisition();
        _daq.Dispose();

        // _finalizeWork is the Task.Run body, which needs no UI thread, so blocking the
        // closing UI thread on it cannot deadlock the way waiting on the async wrapper would.
        try { _finalizeWork.Wait(TimeSpan.FromMinutes(10)); }
        catch (AggregateException) { /* already surfaced through FinalizeAsync */ }

        TraceView.Dispose();
    }
}
