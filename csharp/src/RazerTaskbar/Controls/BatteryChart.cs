//! Port of viewer.rs draw_chart as a WinUI Shapes canvas: dotted grid at
//! 0/50/100, off/charging band rectangles, area fill under connected runs,
//! per-mode polyline segments (broken across off/gap stretches), orange
//! battery-swap dots, and five local-time axis ticks. Theme-reactive:
//! re-renders on ActualThemeChanged with a light/dark palette.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RazerTaskbar.Core;
using Windows.UI;
using Windows.UI.Text;

namespace RazerTaskbar.Controls;

public sealed class BatteryChart : Canvas
{
    private IReadOnlyList<Sample> _samples = Array.Empty<Sample>();

    public BatteryChart()
    {
        ActualThemeChanged += (_, _) => Render(_samples);
        SizeChanged += (_, _) => Render(_samples);
    }

    /// <summary>Re-render with the given (ordered) samples.</summary>
    public void Render(IReadOnlyList<Sample> samples)
    {
        _samples = samples;
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

        if (samples.Count < 2)
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
        double x0 = left;
        double y0 = top;
        double plotW = Math.Max(ActualWidth - left - pad, 10);
        double plotH = Math.Max(ActualHeight - top - bottom, 10);

        long t0 = samples[0].Ts;
        long t1 = samples[^1].Ts;
        double Span(long ts) => t1 == t0 ? 0 : (ts - t0) / (double)(t1 - t0);
        double X(long ts) => x0 + (Span(ts) * plotW);
        double Y(int level) => y0 + plotH - (Math.Clamp(level, 0, 100) / 100.0 * plotH);

        // Grid: dotted horizontal lines at 0/50/100 with right-aligned labels.
        foreach (var (lvl, label) in new[] { (100, "100"), (50, "50"), (0, "0") })
        {
            double gy = Y(lvl);
            var gridLine = new Line
            {
                X1 = x0,
                Y1 = gy,
                X2 = x0 + plotW,
                Y2 = gy,
                Stroke = gridBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            };
            Children.Add(gridLine);
            var tb = new TextBlock
            {
                Text = label,
                Foreground = labelBrush,
                FontSize = 11,
            };
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Children.Add(tb);
            Canvas.SetLeft(tb, x0 - tb.DesiredSize.Width - 4);
            Canvas.SetTop(tb, gy - tb.DesiredSize.Height / 2);
        }

        // Bands per interval: off/unknown (disconnected or > GAP_BREAK_SECS)
        // → off band; charging → green band; discharging → none.
        for (int i = 1; i < samples.Count; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
            long gap = cur.Ts - prev.Ts;
            bool active = cur.Connected && gap <= HistoryService.GapBreakSecs;
            Brush? band = active ? (cur.Charging ? greenBandBrush : null) : offBandBrush;
            if (band is null)
            {
                continue;
            }
            double bx = X(prev.Ts);
            double bw = Math.Max(X(cur.Ts) - bx, 1);
            var rect = new Rectangle
            {
                Width = bw,
                Height = plotH,
                Fill = band,
            };
            Children.Add(rect);
            Canvas.SetLeft(rect, bx);
            Canvas.SetTop(rect, y0);
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
                var polygon = new Polygon
                {
                    Fill = areaBrush,
                    Points = PointCollectionOf(areaPoints),
                };
                Children.Add(polygon);
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

        // First sample seeds the runs when it is connected.
        if (samples[0].Connected)
        {
            var p0 = new Windows.Foundation.Point(X(samples[0].Ts), Y(samples[0].Level));
            areaPoints.Add(p0);
            (samples[0].Charging ? charge : discharge).Add(p0);
        }
        for (int i = 1; i < samples.Count; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
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
            // The area only keeps the fresh points of each run (the shared
            // boundary point stays once per run).
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
        for (int i = 1; i < samples.Count; i++)
        {
            var prev = samples[i - 1];
            var cur = samples[i];
            if (!prev.Charging && !cur.Charging && cur.Level - prev.Level >= SwapJumpPct)
            {
                double cx = X(cur.Ts);
                double cy = Y(cur.Level);
                const double r = 4;
                var dot = new Ellipse
                {
                    Width = r * 2,
                    Height = r * 2,
                    Fill = swapBrush,
                };
                Children.Add(dot);
                Canvas.SetLeft(dot, cx - r);
                Canvas.SetTop(dot, cy - r);
            }
        }

        // Time axis: 5 evenly spaced local-time ticks (first left-aligned,
        // last right-aligned, rest centered).
        for (int k = 0; k <= 4; k++)
        {
            long ts = t0 + (t1 - t0) * k / 4;
            var tb = new TextBlock
            {
                Text = FormatStamp(ts),
                Foreground = labelBrush,
                FontSize = 11,
            };
            tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            Children.Add(tb);
            double x = x0 + (plotW * k / 4);
            double tx = k switch
            {
                0 => x,
                4 => x - tb.DesiredSize.Width,
                _ => x - (tb.DesiredSize.Width / 2),
            };
            Canvas.SetLeft(tb, Math.Clamp(tx, 0, ActualWidth - tb.DesiredSize.Width));
            Canvas.SetTop(tb, y0 + plotH + 6);
        }
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
