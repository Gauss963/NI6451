using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ni6451.Core;
using Windows.UI;

namespace Ni6451.App.Controls;

/// <summary>
/// One row combining a checkbox (channel on/off) with a small live line plot for that
/// channel. Toggling the checkbox immediately restyles the plot (grey = off) regardless of
/// whether an acquisition is currently running -- this is purely a UI state, decided before
/// Start is pressed.
///
/// Port of the Python <c>channel_plot.py</c>. The embedded Matplotlib figure is replaced by
/// a Win2D <see cref="CanvasControl"/>: the trace is drawn straight onto a Direct2D surface
/// instead of going through a chart library, which is what keeps 16 of these repainting at
/// 20 fps affordable.
/// </summary>
public sealed partial class ChannelPlot : UserControl, IDisposable
{
    private static readonly Color OnColor = ParseHex(AppConfig.ChannelOnColor);
    private static readonly Color OffColor = ParseHex(AppConfig.ChannelOffColor);
    private static readonly Color OffFaceColor = ParseHex(AppConfig.ChannelOffFaceColor);
    private static readonly Color OnFaceColor = Microsoft.UI.Colors.White;
    private static readonly Color FrameColor = Color.FromArgb(255, 160, 160, 160);
    private static readonly Color ZeroLineColor = Color.FromArgb(255, 220, 220, 220);
    private static readonly Color LabelColor = Color.FromArgb(255, 110, 110, 110);

    /// <summary>Width reserved on the left of the canvas for the y tick labels.</summary>
    private const float LabelGutter = 30f;

    private readonly float[] _samples = new float[AppConfig.MaxPlotPoints];
    private readonly CanvasTextFormat _tickFormat = new() { FontSize = 9 };

    private int _sampleCount;
    private double _yRange = AppConfig.DefaultYRange;
    private bool _disposed;

    public ChannelPlot()
    {
        InitializeComponent();
    }

    public ChannelPlot(int channel) : this()
    {
        Channel = channel;
        EnableCheckBox.Content = $"ai{channel}";
    }

    /// <summary>AI channel number this row represents.</summary>
    public int Channel { get; private init; }

    /// <summary>Whether the channel is selected for acquisition.</summary>
    public bool IsChannelEnabled => EnableCheckBox.IsChecked == true;

    /// <summary>Raised when the user checks or unchecks this channel.</summary>
    public event EventHandler<bool>? EnabledChanged;

    /// <summary>Disable the checkbox while an acquisition is running.</summary>
    public void SetLocked(bool locked) => EnableCheckBox.IsEnabled = !locked;

    public void SetChecked(bool value) => EnableCheckBox.IsChecked = value;

    public void SetYRange(double yRange)
    {
        _yRange = yRange <= 0 ? AppConfig.DefaultYRange : yRange;
        PlotCanvas.Invalidate();
    }

    /// <summary>
    /// Hand this row the samples to show. Only redraws if the channel is currently enabled,
    /// matching the Qt behaviour of skipping updates for greyed-out rows.
    /// </summary>
    public void UpdateData(ReadOnlySpan<double> values)
    {
        if (!IsChannelEnabled) return;

        int n = Math.Min(values.Length, _samples.Length);
        for (int i = 0; i < n; i++) _samples[i] = (float)values[i];
        _sampleCount = n;
        PlotCanvas.Invalidate();
    }

    public void Clear()
    {
        _sampleCount = 0;
        PlotCanvas.Invalidate();
    }

    // ---------- internal ----------

    private void OnCheckToggled(object sender, RoutedEventArgs e)
    {
        bool enabled = IsChannelEnabled;
        if (!enabled) _sampleCount = 0;
        PlotCanvas.Invalidate();
        EnabledChanged?.Invoke(this, enabled);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        bool enabled = IsChannelEnabled;

        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= LabelGutter + 2 || height <= 4) return;

        ds.Clear(enabled ? OnFaceColor : OffFaceColor);

        float plotLeft = LabelGutter;
        float plotWidth = width - LabelGutter;
        float midY = height / 2f;

        ds.DrawLine(plotLeft, midY, width, midY, ZeroLineColor, 1f);
        ds.DrawRectangle(plotLeft + 0.5f, 0.5f, plotWidth - 1f, height - 1f, FrameColor, 1f);

        string top = FormatTick(_yRange);
        ds.DrawText(top, 2, 1, LabelColor, _tickFormat);
        ds.DrawText("0", 2, midY - 7, LabelColor, _tickFormat);
        ds.DrawText("-" + top, 2, height - 15, LabelColor, _tickFormat);

        if (_sampleCount < 2) return;

        // x maps sample 0 .. n-1 across the plot area, y maps [-yRange, +yRange] to the
        // full height; the same "window worth of data, fixed symmetric y limits" framing
        // the Matplotlib version used.
        float xScale = (plotWidth - 2f) / (_sampleCount - 1);
        float yScale = (float)(midY / _yRange);

        using var builder = new CanvasPathBuilder(sender);
        builder.BeginFigure(plotLeft + 1f, ClampY(midY - _samples[0] * yScale, height));
        for (int i = 1; i < _sampleCount; i++)
            builder.AddLine(plotLeft + 1f + i * xScale, ClampY(midY - _samples[i] * yScale, height));
        builder.EndFigure(CanvasFigureLoop.Open);

        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, enabled ? OnColor : OffColor, 1f);
    }

    /// <summary>Keep out-of-range samples on the canvas instead of letting Direct2D draw far off-surface.</summary>
    private static float ClampY(float y, float height) => Math.Clamp(y, -1f, height + 1f);

    private static string FormatTick(double value)
        => value >= 1 ? value.ToString("0.#") : value.ToString("0.##");

    private static Color ParseHex(string hex)
    {
        ReadOnlySpan<char> s = hex.AsSpan(hex.StartsWith('#') ? 1 : 0);
        return Color.FromArgb(
            255,
            byte.Parse(s[..2], System.Globalization.NumberStyles.HexNumber),
            byte.Parse(s.Slice(2, 2), System.Globalization.NumberStyles.HexNumber),
            byte.Parse(s.Slice(4, 2), System.Globalization.NumberStyles.HexNumber));
    }

    /// <summary>
    /// Win2D holds unmanaged Direct2D resources that are not released by the XAML teardown,
    /// so the control has to be taken out of the tree explicitly when the window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tickFormat.Dispose();
        PlotCanvas.RemoveFromVisualTree();
    }
}
