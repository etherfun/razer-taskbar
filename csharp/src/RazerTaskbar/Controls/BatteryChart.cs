// Port of viewer.rs draw_chart as a WinUI Shapes canvas: dotted grid at
// 0/50/100, off/charging band rectangles, area fill under connected runs,
// per-mode polyline segments (broken across off/gap stretches), orange
// battery-swap dots, and five local-time axis ticks. Theme-reactive:
// re-renders on ActualThemeChanged with a light/dark palette.
//
// P2 additions: an optional second (compare) series drawn in accent on the
// same absolute-time axis, and a hover readout (vertical line + nearest-point
// dot + stamp/level label) over the main series.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RazerTaskbar.Core;
using Windows.UI;
using Windows.UI.Text;

namespace RazerTaskbar.Controls;

public sealed class BatteryChart : Canvas
{
    private IReadOnlyList<Sample> _main = Array.Empty<Sample>();
    private IReadOnlyList<Sample> _compare = Array.Empty<Sample>();
    private string? _compareName;

    // Plot geometry cached for hover math.
    private long _t0;
    private long _t1;
    private double _x0;
    private double _y0;
    private double _plotW;
    private double _plotH;

    private readonly List<UIElement> _hoverElements = new();

    public BatteryChart()
    {
        ActualThemeChanged += (_, _) => Render(_main, _compare, _compareName);
        SizeChanged += (_, _) => Render(_main, _compare, _compareName);
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => ClearHover();
    }

    public void Render(IReadOnlyList<Sample> samples)
        => Render(samples, null, null);

    /// <summary>Render the main series (full treatment) plus an optional
    /// compare series (accent lines only) on the same absolute-time axis.</summary>
    public void Render(IReadOnlyList<Sample> main, IReadOnlyList<Sample>? compare, string? compareName)
    {
        _main = main;
        _compare = compare ?? Array.Empty<Sample>();
        _compareName = compareName;
        ClearHover();
        Children.Clear();
        if (ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        bool dark = ActualTheme == ElementTheme.Dark;
        var lineBrush = Solid(dark ? 0xE8E8E8 : 0x1B1B1B);
        var greenBrush = Solid(dark ? 0x6CCB5F : 0x3A8A32);
        var greenBandBrush = Solid(dark ? 0x2A4026 : 0xE2F2DE);
        var offBandBrush = Solid(dark ? 0x252525 : 0xF2F2F2);
        var areaBrush = Solid(dark ? 0x323232 : 0xECECEC);
        var gridBrush = Solid(dark ? 0x333333 : 0xDDDDDD);
        var swapBrush = Solid(dark ? 0xFFB900 : 0xC77800);
        var labelBrush = Solid(dark ? 0x7A7A7A : 0x9A9A9A);
        var accentBrush = Solid(dark ? 0x60CDFF : 0x0078D4);

        if (main.Count < 2)
        {
            var empty = new TextBlock
            {
                Text = I18n.Tr("No data yet — recording starts when a device connects."),
                Foreground = labelBrush,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Children.Add(empty);
            Canvas.SetLeft(empty, (ActualWidth - 420) / 2 < 8 ? 8 : (ActualWidth - 420) / 2);
            Canvas.SetTop(empty, ActualHeight / 2 - 10);
            return;
        }

        // Plot rect: extra left room for the y labels, bottom for the time
        // axis (pad 12 / y-axis 26 / bottom 24 in the GDI original).
        double pad = 12, left = 26, bottom = 24, top = 8;
        _x0 = left;
        _y0 = top;
        _plotW = Math.Max(ActualWidth - left - pad, 10);
        _plotH = Math.Max(ActualHeight - top - bottom, 10);

        // Absolute-time axis spans BOTH series so compare lines align.
        _t0 = main[0].Ts;
        _t1 = main[^1].Ts;
        if (_compare.Count >= 2)
        {
            _t0 = Math.Min(_t0, _compare[0].Ts);
            _t1 = Math.Max(_t1, _compare[^1].Ts);
        }
        long span = Math.Max(_t1 - _t0, 1);
        double X(long ts) => _x0 + ((ts - _t0) / (double)span * _plotW);
        double Y(int level) => _y0 + _plotH - (Math.Clamp(level, 0, 100) / 100.0 * _plotH);

        // Grid: dotted horizontal lines at 0/50/100 with right-aligned labels.
        foreach (var (lvl, label) in new[] { (100, "100"), (50, "50"), (0, "0") })
        {
            double gy = Y(lvl);
            Children.Add(new Line
            {
                X1 = _x0,
                Y1 = gy,
                X2 = _x0 + _plotW,
                Y2 = gy,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            });
            var tb = new TextBlock { Text = label, Foreground = labelBrush, FontSize = 11 };
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Children.Add(tb);
            Canvas.SetLeft(tb, _x0 - tb.DesiredSize.Width - 4);
            Canvas.SetTop(tb, gy - tb.DesiredSize.Height / 2);
        }

        // Bands per interval: off/unknown (disconnected or > GAP_BREAK_SECS)
        // → off band; charging → green band; discharging → none.
        for (int i = 1; i < main.Count; i++)
        {
            var prev = main[i - 1];
            var cur = main[i];
            long gap = cur.Ts - prev.Ts;
            bool active = cur.Connected && gap <= HistoryService.GapBreakSecs;
            Brush? band = active ? (cur.Charging ? greenBandBrush : null) : offBandBrush;
            if (band is null)
            {
                continue;
            }
            double bx = X(prev.Ts);
            double bw = Math.Max(X(cur.Ts) - bx, 1);
            var rect = new Rectangle { Width = bw, Height = _plotH, Fill = band };
            Children.Add(rect);
            Canvas.SetLeft(rect, bx);
            Canvas.SetTop(rect, _y0);
        }

        // Area fill under contiguous connected runs, then the level line in
        // per-mode segments (broken across off/gap stretches).
        var areaPoints = new List<Windows.Foundation.Point>();
        var discharge = new List<Windows.Foundation.Point>();
        var charge = new List<Windows.Foundation.Point>();

        void FlushArea()
        {
            if (areaPoints.Count >= 2)
            {
                Children.Add(new Polygon
                {
                    Fill = areaBrush,
                    Points = PointCollectionOf(areaPoints),
                });
            }
            areaPoints.Clear();
        }

        void FlushLine()
        {
            if (discharge.Count >= 2)
            {
                Children.Add(new Polyline
                {
                    Stroke = lineBrush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = PointCollectionOf(discharge),
                });
            }
            if (charge.Count >= 2)
            {
                Children.Add(new Polyline
                {
                    Stroke = greenBrush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = PointCollectionOf(charge),
                });
            }
            discharge.Clear();
            charge.Clear();
        }

        if (main[0].Connected)
        {
            var p0 = new Windows.Foundation.Point(X(main[0].Ts), Y(main[0].Level));
            areaPoints.Add(p0);
            (main[0].Charging ? charge : discharge).Add(p0);
        }
        for (int i = 1; i < main.Count; i++)
        {
            var prev = main[i - 1];
            var cur = main[i];
            long gap = cur.Ts - prev.Ts;
            bool active = cur.Connected && gap <= HistoryService.GapBreakSecs;
            if (!active)
            {
                FlushArea();
                FlushLine();
                continue;
            }
            var pc = new Windows.Foundation.Point(X(cur.Ts), Y(cur.Level));
            var pp = new Windows.Foundation.Point(X(prev.Ts), Y(prev.Level));
            if (areaPoints.Count == 0 || areaPoints[^1] != pp)
            {
                areaPoints.Add(pp);
            }
            areaPoints.Add(pc);
            (cur.Charging ? charge : discharge).Add(pp);
            (cur.Charging ? charge : discharge).Add(pc);
        }
        FlushArea();
        FlushLine();

        // Swap markers: orange dot at any discharging rise >= 30 pct.
        const int SwapJumpPct = 30;
        for (int i = 1; i < main.Count; i++)
        {
            var prev = main[i - 1];
            var cur = main[i];
            if (!prev.Charging && !cur.Charging && cur.Level - prev.Level >= SwapJumpPct)
            {
                double cx = X(cur.Ts);
                double cy = Y(cur.Level);
                const double r = 4;
                var dot = new Ellipse { Width = r * 2, Height = r * 2, Fill = swapBrush };
                Children.Add(dot);
                Canvas.SetLeft(dot, cx - r);
                Canvas.SetTop(dot, cy - r);
            }
        }

        // Compare series: accent lines over the same absolute-time axis.
        if (_compare.Count >= 2)
        {
            var run = new List<Windows.Foundation.Point>();
            void FlushCompare()
            {
                if (run.Count >= 2)
                {
                    Children.Add(new Polyline
                    {
                        Stroke = accentBrush,
                        StrokeThickness = 1.5,
                        StrokeLineJoin = PenLineJoin.Round,
                        Points = PointCollectionOf(run),
                    });
                }
                run.Clear();
            }
            if (_compare[0].Connected)
            {
                run.Add(new Windows.Foundation.Point(X(_compare[0].Ts), Y(_compare[0].Level)));
            }
            for (int i = 1; i < _compare.Count; i++)
            {
                var prev = _compare[i - 1];
                var cur = _compare[i];
                long gap = cur.Ts - prev.Ts;
                bool active = cur.Connected && gap <= HistoryService.GapBreakSecs;
                if (!active)
                {
                    FlushCompare();
                    continue;
                }
                var pp = new Windows.Foundation.Point(X(prev.Ts), Y(prev.Level));
                if (run.Count == 0 || run[^1] != pp)
                {
                    run.Add(pp);
                }
                run.Add(new Windows.Foundation.Point(X(cur.Ts), Y(cur.Level)));
            }
            FlushCompare();
        }

        // Time axis: 5 evenly spaced local-time ticks (first left-aligned,
        // last right-aligned, rest centered).
        for (int k = 0; k <= 4; k++)
        {
            long ts = _t0 + (_t1 - _t0) * k / 4;
            var tb = new TextBlock
            {
                Text = FormatStamp(ts),
                Foreground = labelBrush,
                FontSize = 11,
            };
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Children.Add(tb);
            double x = _x0 + (_plotW * k / 4);
            double tx = k switch
            {
                0 => x,
                4 => x - tb.DesiredSize.Width,
                _ => x - (tb.DesiredSize.Width / 2),
            };
            Canvas.SetLeft(tb, Math.Clamp(tx, 0, ActualWidth - tb.DesiredSize.Width));
            Canvas.SetTop(tb, _y0 + _plotH + 6);
        }
    }

    // — Hover readout (main series) —

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_main.Count < 2 || _plotW <= 0)
        {
            return;
        }
        var pt = e.GetCurrentPoint(this).Position;
        if (pt.X < _x0 || pt.X > _x0 + _plotW || pt.Y < 0 || pt.Y > _y0 + _plotH + 20)
        {
            ClearHover();
            return;
        }
        long span = Math.Max(_t1 - _t0, 1);
        long ts = _t0 + (long)(((pt.X - _x0) / _plotW) * span);
        // Nearest main-series sample (series is time-ascending).
        int lo = 0, hi = _main.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_main[mid].Ts < ts)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo > 0 && Math.Abs(_main[lo - 1].Ts - ts) < Math.Abs(_main[lo].Ts - ts))
        {
            lo -= 1;
        }
        var nearest = _main[lo];
        long nSpan = Math.Max(_t1 - _t0, 1);
        double x = _x0 + ((nearest.Ts - _t0) / (double)nSpan * _plotW);
        double y = _y0 + _plotH - (Math.Clamp(nearest.Level, 0, 100) / 100.0 * _plotH);

        bool dark = ActualTheme == ElementTheme.Dark;
        var accent = Solid(dark ? 0x60CDFF : 0x0078D4);
        var labelBg = Solid(dark ? 0x2B2B2B : 0xF7F7F7);
        var labelFg = Solid(dark ? 0xF3F3F3 : 0x1B1B1B);

        ClearHover();
        var vline = new Line
        {
            X1 = x,
            Y1 = _y0,
            X2 = x,
            Y2 = _y0 + _plotH,
            Stroke = accent,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            IsHitTestVisible = false,
        };
        Children.Add(vline);
        _hoverElements.Add(vline);
        var dot = new Ellipse { Width = 7, Height = 7, Fill = accent, IsHitTestVisible = false };
        Children.Add(dot);
        Canvas.SetLeft(dot, x - 3.5);
        Canvas.SetTop(dot, y - 3.5);
        _hoverElements.Add(dot);
        var label = new TextBlock
        {
            Text = $"{FormatStamp(nearest.Ts)}  {nearest.Level}%",
            FontSize = 11,
            Foreground = labelFg,
            IsHitTestVisible = false,
        };
        label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var border = new Border
        {
            Background = labelBg,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = label,
            IsHitTestVisible = false,
        };
        Children.Add(border);
        double bx = Math.Clamp(x + 8, 0, Math.Max(ActualWidth - label.DesiredSize.Width - 16, 0));
        double by = Math.Clamp(_y0 + 4, 0, Math.Max(ActualHeight - 28, 0));
        Canvas.SetLeft(border, bx);
        Canvas.SetTop(border, by);
        _hoverElements.Add(border);
    }

    private void ClearHover()
    {
        foreach (var el in _hoverElements)
        {
            Children.Remove(el);
        }
        _hoverElements.Clear();
    }

    /// <summary>Local-time "yyyy-MM-dd HH:mm" (viewer.rs fmt_stamp parity).</summary>
    public static string FormatStamp(long ts)
        => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private static PointCollection PointCollectionOf(List<Windows.Foundation.Point> points)
    {
        var pc = new PointCollection();
        foreach (var pt in points)
        {
            pc.Add(pt);
        }
        return pc;
    }

    private static SolidColorBrush Solid(long rgb)
    {
        var c = new Color { A = 0xFF, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb };
        return new SolidColorBrush(c);
    }
}
