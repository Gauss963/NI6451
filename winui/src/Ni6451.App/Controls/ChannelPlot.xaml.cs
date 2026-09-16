using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ni6451.Core;
using Windows.UI;

namespace Ni6451.App.Controls;

/// <summary>
/// One row combining a checkbox (channel on/off) with a small live line plot for that
/// channel. Toggling the checkbox immediately restyles the plot (greyed = off) regardless of
/// whether an acquisition is currently running -- this is purely a UI state, decided before
/// Start is pressed.
///
/// Port of the Python <c>channel_plot.py</c>. The embedded Matplotlib figure is replaced by
/// a Win2D <see cref="CanvasControl"/>: the trace is drawn straight onto a Direct2D surface
/// instead of going through a chart library, which is what keeps 16 of these repainting at
/// 20 fps affordable.
///
/// The palette follows the system light/dark setting, which a Matplotlib figure baked into a
/// Qt widget could not do -- a white plot face in a dark window is the single most obvious
/// way for an app to look unfinished on Windows 11.
/// </summary>
public sealed partial class ChannelPlot : UserControl, IDisposable
{
    /// <summary>Width reserved on the left of the canvas for the y tick labels.</summary>
    private const float LabelGutter = 30f;

    private readonly float[] _samples = new float[AppConfig.MaxPlotPoints];
    private readonly CanvasTextFormat _tickFormat = new() { FontSize = 9 };

    private Palette _palette = Palette.ForTheme(ElementTheme.Light);
    private int _sampleCount;
    private double _yRange = AppConfig.DefaultYRange;
    private bool _disposed;

    public ChannelPlot()
    {
        InitializeComponent();

        ActualThemeChanged += OnActualThemeChanged;
        Loaded += (_, _) => ApplyTheme();
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

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme()
    {
        _palette = Palette.ForTheme(ActualTheme);
        PlotCanvas.Invalidate();
    }

    private void OnCheckToggled(object sender, RoutedEventArgs e)
    {
        bool enabled = IsChannelEnabled;
        if (!enabled) _sampleCount = 0;
        EnableCheckBox.Opacity = enabled ? 1.0 : 0.55;
        PlotCanvas.Invalidate();
        EnabledChanged?.Invoke(this, enabled);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        Palette p = _palette;
        bool enabled = IsChannelEnabled;

        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= LabelGutter + 2 || height <= 4) return;

        ds.Clear(enabled ? p.Face : p.OffFace);

        float plotLeft = LabelGutter;
        float plotWidth = width - LabelGutter;
        float midY = height / 2f;

        ds.DrawLine(plotLeft, midY, width, midY, p.ZeroLine, 1f);

        string top = FormatTick(_yRange);
        ds.DrawText(top, 2, 1, p.Label, _tickFormat);
        ds.DrawText("0", 2, midY - 7, p.Label, _tickFormat);
        ds.DrawText("-" + top, 2, height - 15, p.Label, _tickFormat);

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
        ds.DrawGeometry(geometry, enabled ? p.Trace : p.OffTrace, 1.2f);
    }

    /// <summary>Keep out-of-range samples on the canvas instead of letting Direct2D draw far off-surface.</summary>
    private static float ClampY(float y, float height) => Math.Clamp(y, -1f, height + 1f);

    private static string FormatTick(double value)
        => value >= 1
            ? value.ToString("0.#", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Win2D draws with raw colours rather than XAML brushes, so the theme has to be resolved
    /// into concrete values here. Both sets live in <see cref="AppConfig"/> with the rest of
    /// the tunable parameters.
    /// </summary>
    private readonly record struct Palette(
        Color Face, Color OffFace, Color Trace, Color OffTrace, Color ZeroLine, Color Label)
    {
        private static readonly Palette Light = new(
            ParseHex(AppConfig.ChannelFaceColor),
            ParseHex(AppConfig.ChannelOffFaceColor),
            ParseHex(AppConfig.ChannelOnColor),
            ParseHex(AppConfig.ChannelOffColor),
            ParseHex(AppConfig.ChannelZeroLineColor),
            ParseHex(AppConfig.ChannelLabelColor));

        private static readonly Palette Dark = new(
            ParseHex(AppConfig.ChannelFaceColorDark),
            ParseHex(AppConfig.ChannelOffFaceColorDark),
            ParseHex(AppConfig.ChannelOnColorDark),
            ParseHex(AppConfig.ChannelOffColorDark),
            ParseHex(AppConfig.ChannelZeroLineColorDark),
            ParseHex(AppConfig.ChannelLabelColorDark));

        public static Palette ForTheme(ElementTheme theme) => theme == ElementTheme.Dark ? Dark : Light;

        private static Color ParseHex(string hex)
        {
            ReadOnlySpan<char> s = hex.AsSpan(hex.StartsWith('#') ? 1 : 0);
            return Color.FromArgb(
                255,
                byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Win2D holds unmanaged Direct2D resources that are not released by the XAML teardown,
    /// so the control has to be taken out of the tree explicitly when the window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        _tickFormat.Dispose();
        PlotCanvas.RemoveFromVisualTree();
    }
}
