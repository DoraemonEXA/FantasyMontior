using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FantasyMontior.Core;
using FantasyMontior.ViewModels;

namespace FantasyMontior;

/// <summary>A bounded vector chart. It has no timer, animation clock, or hardware access.</summary>
public sealed class SensorTrendChart : FrameworkElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(TrendChartData),
        typeof(SensorTrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, InvalidateChart));
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool),
        typeof(SensorTrendChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, InvalidateChart));
    public TrendChartData? Data { get => (TrendChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }
    private DrawingGroup? _drawing;
    private Rect _plot;
    private double _minimum, _maximum;
    private HistoryMinute? _hover;
    private readonly ToolTip _tooltip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, StaysOpen = true };
    private static readonly Brush TextBrush = Brush("#566273"), GridBrush = Brush("#E8EDF0");
    private static readonly Typeface Typeface = new("Segoe UI, Microsoft YaHei");
    internal int DrawingBuildCount { get; private set; }
    internal Rect PlotBounds => _plot;
    internal (double Minimum, double Maximum) AxisRange => (_minimum, _maximum);

    public SensorTrendChart()
    {
        UseLayoutRounding = true;
        ClipToBounds = true;
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseHover(); };
        Unloaded += (_, _) => CloseHover();
    }
    private static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
    private static void InvalidateChart(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var chart = (SensorTrendChart)target;
        chart._drawing = null;
        chart.CloseHover();
    }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        _drawing = null;
        base.OnRenderSizeChanged(sizeInfo);
    }
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 90 || ActualHeight < 60) return;
        _drawing ??= BuildDrawing();
        drawingContext.DrawDrawing(_drawing);
        if (_hover is { } hover)
        {
            var x = X(Middle(hover));
            drawingContext.PushClip(new RectangleGeometry(_plot));
            drawingContext.DrawLine(new Pen(TextBrush, 1), new(x, _plot.Top), new(x, _plot.Bottom));
            drawingContext.DrawEllipse(LineBrush(), new Pen(Brushes.White, 1.5), new(x, Y(hover.Average)), 4, 4);
            drawingContext.Pop();
        }
    }

    private DrawingGroup BuildDrawing()
    {
        DrawingBuildCount++;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new(0, 0, ActualWidth, ActualHeight));
            _plot = new(IsCompact ? 43 : 62, 18, Math.Max(1, ActualWidth - (IsCompact ? 57 : 80)), Math.Max(1, ActualHeight - 51));
            var minutes = Data?.Series?.Minutes ?? [];
            var visible = minutes.Where(m => m.Last > (Data?.Now ?? DateTimeOffset.UtcNow) - SensorHistoryService.Retention).ToArray();
            if (Data?.Unit == "%") { _minimum = 0; _maximum = 100; }
            else if (visible.Length > 0)
            {
                _minimum = visible.Min(m => m.Minimum);
                _maximum = visible.Max(m => m.Maximum);
                var padding = Math.Max((_maximum - _minimum) * .12, Math.Max(Math.Abs(_maximum) * .02, .1));
                _minimum -= padding;
                _maximum += padding;
            }
            else { _minimum = 0; _maximum = 1; }
            var horizontalTicks = IsCompact ? 2 : 4;
            for (var i = 0; i <= horizontalTicks; i++)
            {
                var y = _plot.Top + _plot.Height * i / horizontalTicks;
                dc.DrawLine(new Pen(GridBrush, 1), new(_plot.Left, y), new(_plot.Right, y));
                var value = _maximum - (_maximum - _minimum) * i / horizontalTicks;
                var label = Number(value);
                DrawText(dc, label, _plot.Left - 7, y - 7, right: true);
            }
            DrawText(dc, Data?.Unit ?? "", _plot.Left, 0);
            if (Data is { } data)
            {
                var divisions = IsCompact ? 2 : 4;
                for (var i = 0; i <= divisions; i++)
                {
                    var time = (data.Now - SensorHistoryService.Retention).AddHours(24d * i / divisions).ToLocalTime();
                    var x = _plot.Left + _plot.Width * i / divisions;
                    dc.DrawLine(new Pen(GridBrush, 1), new(x, _plot.Top), new(x, _plot.Bottom));
                    DrawText(dc, time.ToString(IsCompact ? "HH:mm" : "MM-dd HH:mm"), x, _plot.Bottom + 9,
                        right: i == divisions, centered: i > 0 && i < divisions);
                }
            }
            dc.PushClip(new RectangleGeometry(_plot));
            foreach (var segment in visible.GroupBy(m => m.Segment)) DrawSegment(dc, segment.ToArray());
            dc.Pop();
            if (visible.Length == 0)
                DrawText(dc, Localization.T("No recorded readings yet"), _plot.Left + _plot.Width / 2, _plot.Top + _plot.Height / 2 - 7, centered: true);
        }
        group.Freeze();
        return group;
    }

    private void DrawSegment(DrawingContext dc, HistoryMinute[] minutes)
    {
        var line = LineBrush();
        var envelope = line.Clone();
        envelope.Opacity = .16;
        envelope.Freeze();
        var band = new StreamGeometry();
        using (var path = band.Open())
        {
            path.BeginFigure(new(X(Middle(minutes[0])), Y(minutes[0].Maximum)), true, true);
            foreach (var minute in minutes.Skip(1)) path.LineTo(new(X(Middle(minute)), Y(minute.Maximum)), true, false);
            foreach (var minute in minutes.Reverse()) path.LineTo(new(X(Middle(minute)), Y(minute.Minimum)), true, false);
        }
        band.Freeze();
        dc.DrawGeometry(envelope, null, band);
        var average = new StreamGeometry();
        using (var path = average.Open())
        {
            path.BeginFigure(new(X(Middle(minutes[0])), Y(minutes[0].Average)), false, false);
            foreach (var minute in minutes.Skip(1)) path.LineTo(new(X(Middle(minute)), Y(minute.Average)), true, false);
        }
        average.Freeze();
        dc.DrawGeometry(null, new Pen(line, IsCompact ? 1.5 : 2), average);
        if (minutes.Length == 1)
        {
            var minute = minutes[0];
            var x = X(Middle(minute));
            dc.DrawLine(new Pen(envelope, 3), new(x, Y(minute.Minimum)), new(x, Y(minute.Maximum)));
            dc.DrawEllipse(line, null, new(x, Y(minute.Average)), 2, 2);
        }
    }

    private Brush LineBrush() => Data?.Unit == "°C" ? Brush("#B87718") : Data?.Unit == "%" ? Brush("#348258") : Brush("#327CA0");
    private static DateTimeOffset Middle(HistoryMinute minute) => minute.First + (minute.Last - minute.First) / 2;
    private double X(DateTimeOffset time) => _plot.Left + (time - (Data!.Now - SensorHistoryService.Retention)).TotalHours / 24 * _plot.Width;
    private double Y(double value) => _plot.Bottom - (value - _minimum) / (_maximum - _minimum) * _plot.Height;
    private static string Number(double value) => Math.Abs(value) >= 100000 || Math.Abs(value) is > 0 and < .01
        ? value.ToString("0.#E+0", CultureInfo.CurrentCulture) : value.ToString("0.##", CultureInfo.CurrentCulture);
    private void DrawText(DrawingContext dc, string text, double x, double y, bool right = false, bool centered = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface,
            IsCompact ? 10 : 11, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new(x - (right ? formatted.Width : centered ? formatted.Width / 2 : 0), y));
    }

    internal HistoryMinute? HitTestMinute(Point point)
    {
        if (!_plot.Contains(point) || Data?.Series is not { } series) return null;
        var time = Data.Now - SensorHistoryService.Retention + TimeSpan.FromHours(24 * (point.X - _plot.Left) / _plot.Width);
        var minuteTicks = time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMinute;
        return series.Minutes.Where(m => m.Minute.UtcTicks == minuteTicks)
            .OrderBy(m => Math.Abs((Middle(m) - time).Ticks)).Select(m => (HistoryMinute?)m).FirstOrDefault();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var minute = HitTestMinute(e.GetPosition(this));
        if (_hover == minute) return;
        _hover = minute;
        if (minute is { } value)
        {
            var unit = Data?.Unit;
            _tooltip.Content = $"{value.Minute.ToLocalTime():yyyy-MM-dd HH:mm}\n" +
                Localization.F("Average: {0} · Min: {1} · Max: {2}", $"{value.Average:0.##} {unit}", $"{value.Minimum:0.##} {unit}", $"{value.Maximum:0.##} {unit}") +
                "\n" + Localization.F("{0} readings · {1:HH:mm:ss}–{2:HH:mm:ss}", value.Count, value.First.ToLocalTime(), value.Last.ToLocalTime());
            _tooltip.PlacementTarget = this;
            _tooltip.IsOpen = true;
        }
        else _tooltip.IsOpen = false;
        InvalidateVisual();
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); CloseHover(); }
    private void CloseHover() { _hover = null; _tooltip.IsOpen = false; InvalidateVisual(); }
}
