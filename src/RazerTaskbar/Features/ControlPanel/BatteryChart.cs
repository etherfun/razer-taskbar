// Port of viewer.rs draw_chart as a WinUI Shapes canvas: dotted grid at
// 0/50/100, off/charging band rectangles, area fill under connected runs,
// per-mode polyline segments (broken across off/gap stretches), orange
// battery-swap dots, and five local-time axis ticks. Theme-reactive:
// re-renders on ActualThemeChanged with a light/dark palette.
//
// P2 additions: an optional second (compare) series drawn in accent on the
// same absolute-time axis (shutdown stretches bridged by a dim dashed
// connector), and a hover readout (vertical line + nearest-point
// dot + stamp/level label) over the main series.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RazerTaskbar.Core;
using Windows.UI;
using Windows.UI.Text;

namespace RazerTaskbar.Features.ControlPanel;

public sealed class BatteryChart : Canvas
{
    private IReadOnlyList<Sample> _main = Array.Empty<Sample>();
    private IReadOnlyList<Sample> _compare = Array.Empty<Sample>();
    private string? _compareName;
    private bool _showOffBands = true;

    // Plot geometry cached for hover math. The axis is a piecewise-linear
    // ts→x breakpoint list built from the main series: absolute time by
    // default; ACTIVE time only when off bands are hidden (off stretches
    // collapse to zero width — the pure-line look).
    private readonly List<(long Ts, double X)> _axis = new();
    private double _x0;
    private double _y0;
    private double _plotW;
    private double _plotH;

    // — Hover readout (main series) — persistent elements, created once and
    // repositioned per PointerMoved: removing and re-adding three elements
    // (plus a full Measure) on every move made the readout stutter on
    // dense ranges. _hoverAttached guards the case where Render's
    // Children.Clear() took them out of the tree.
    private Line? _hoverLine;
    private Ellipse? _hoverDot;
    private Border? _hoverBorder;
    private TextBlock? _hoverLabel;
    private bool _hoverAttached;
    private bool _hoverDark = true;
    private double _hoverBorderW;
    private string _hoverStamp = "";

    // Coalesced re-render for resize: a window-resize drag fires SizeChanged
    // per pixel step and each Render rebuilds the whole shape tree; the timer
    // renders once, 40ms after the last change.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _renderDelay;
    // Span ≤ 36h → axis ticks read "HH:mm" (date prefix is noise) and the
    // hover readout drops the date too; longer spans use "MM-dd" ticks and a
    // full "MM-dd HH:mm" hover. Shared by the render path and the hover.
    private bool _shortSpan;

    public BatteryChart()
    {
        // Transparent (not null!) background: a null brush makes the canvas
        // invisible to hit testing, so PointerMoved fired only over drawn
        // children — the hover readout came alive over the big off bands but
        // not over the plain line. Transparent paints nothing yet is
        // hit-testable, so the whole plot surface tracks the pointer.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ActualThemeChanged += (_, _) => Render(_main, _compare, _compareName, _showOffBands);
        _renderDelay = DispatcherQueue.CreateTimer();
        _renderDelay.IsRepeating = false;
        _renderDelay.Interval = TimeSpan.FromMilliseconds(40);
        _renderDelay.Tick += (_, _) => Render(_main, _compare, _compareName, _showOffBands);
        SizeChanged += (_, _) =>
        {
            _renderDelay.Stop();
            _renderDelay.Start();
        };
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => ClearHover();
    }

    /// <summary>Render the main series (full treatment) plus an optional
    /// compare series (accent lines only) on the same absolute-time axis.
    /// `showOffBands=false` skips the gray off/unknown bands — the level
    /// line still breaks across those stretches, the chart just stops
    /// shading them ("hide shutdown time").</summary>
    public void Render(IReadOnlyList<Sample> main, IReadOnlyList<Sample>? compare, string? compareName,
        bool showOffBands = true)
    {
        _main = main;
        _compare = compare ?? Array.Empty<Sample>();
        _compareName = compareName;
        _showOffBands = showOffBands;
        ClearHover();
        Children.Clear();
        _hoverAttached = false;
        if (ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        bool dark = ActualTheme == ElementTheme.Dark;
        var lineBrush = Solid(dark ? 0xE8E8E8 : 0x1B1B1B);
        var greenBrush = Solid(dark ? 0x6CCB5F : 0x3A8A32);
        var greenBandBrush = Solid(dark ? 0x2A4026 : 0xE2F2DE, 0x99);
        var offBandBrush = Solid(dark ? 0x252525 : 0xF2F2F2, 0x99);
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
        _shortSpan = main[main.Count - 1].Ts - main[0].Ts <= 36 * 3600;
        double pad = 12, left = 26, bottom = 24, top = 8;
        _x0 = left;
        _y0 = top;
        _plotW = Math.Max(ActualWidth - left - pad, 10);
        _plotH = Math.Max(ActualHeight - top - bottom, 10);

        // Axis mapping: absolute time by default; with off bands hidden, x
        // runs on ACTIVE time only (off/gap stretches collapse to zero
        // width), so the level line reads as one continuous curve.
        BuildAxis(main, showOffBands);
        double X(long ts) => XOf(ts);
        double Y(int level) => LevelY(level);

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
            Canvas.SetLeft(tb, _x0 - MeasuredWidth(tb) - 4);
            Canvas.SetTop(tb, gy - (tb.DesiredSize.Height > 0 ? tb.DesiredSize.Height / 2 : 7));
        }

        // Bands: off/unknown (disconnected or > GAP_BREAK_SECS) → off band;
        // charging → green band; discharging → none. Adjacent same-kind
        // intervals merge into ONE Rectangle — a long history is thousands
        // of intervals but only a handful of contiguous streaks, and a XAML
        // UIElement per interval made big ranges freeze the UI thread.
        var runBrush = (Brush?)null;
        double runX = 0, runEndX = 0;
        void FlushBand()
        {
            if (runBrush is null)
            {
                return;
            }
            var rect = new Rectangle { Width = Math.Max(runEndX - runX, 1), Height = _plotH, Fill = runBrush };
            Children.Add(rect);
            Canvas.SetLeft(rect, runX);
            Canvas.SetTop(rect, _y0);
        }
        for (int i = 1; i < main.Count; i++)
        {
            var prev = main[i - 1];
            var cur = main[i];
            long gap = cur.Ts - prev.Ts;
            bool active = cur.Connected && gap <= HistoryService.GapBreakSecs;
            Brush? band = active ? (cur.Charging ? greenBandBrush : null)
                : showOffBands ? offBandBrush
                : null;
            double bx = X(prev.Ts);
            double bEnd = Math.Max(X(cur.Ts), bx + 1);
            if (band is null)
            {
                FlushBand();
                runBrush = null;
                continue;
            }
            if (ReferenceEquals(band, runBrush) && bx <= runEndX + 0.5)
            {
                runEndX = bEnd;
                continue;
            }
            FlushBand();
            runBrush = band;
            runX = bx;
            runEndX = bEnd;
        }
        FlushBand();

        // Area fill under contiguous connected runs, then the level line in
        // per-mode segments (broken across off/gap stretches). Points are
        // decimated at flush: sub-pixel wiggles are invisible but a long
        // history put tens of thousands of points into XAML collections.
        var areaPoints = new List<Windows.Foundation.Point>();
        var discharge = new List<Windows.Foundation.Point>();
        var charge = new List<Windows.Foundation.Point>();
        // Mode of the previous active interval: a charge/discharge flip must
        // split the polylines, or the old run's tail connects straight to the
        // next run's head — a chord across the whole other-color phase (a
        // white line crossing the green charge hump).
        bool? lastCharging = null;

        void FlushArea()
        {
            if (areaPoints.Count >= 2)
            {
                Children.Add(new Polygon
                {
                    Fill = areaBrush,
                    Points = PointCollectionOf(Decimate(areaPoints)),
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
                    Points = PointCollectionOf(Decimate(discharge)),
                });
            }
            if (charge.Count >= 2)
            {
                Children.Add(new Polyline
                {
                    Stroke = greenBrush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                    Points = PointCollectionOf(Decimate(charge)),
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
                lastCharging = null;
                continue;
            }
            if (lastCharging is { } prevMode && prevMode != cur.Charging)
            {
                FlushLine();
            }
            lastCharging = cur.Charging;
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

        // Compare series: accent lines over the same absolute-time axis, with
        // shutdown/off stretches bridged by a dashed connector so the series
        // stays traceable across its gaps.
        if (_compare.Count >= 2)
        {
            var run = new List<Windows.Foundation.Point>();
            var runs = new List<(Windows.Foundation.Point First, Windows.Foundation.Point Last)>();
            void FlushCompare()
            {
                if (run.Count >= 2)
                {
                    Children.Add(new Polyline
                    {
                        Stroke = accentBrush,
                        StrokeThickness = 1.5,
                        StrokeLineJoin = PenLineJoin.Round,
                        Points = PointCollectionOf(Decimate(run)),
                    });
                }
                if (run.Count > 0)
                {
                    runs.Add((run[0], run[^1]));
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

            // Dashed bridges across shutdown stretches: half-transparent so
            // they read as "unmeasured" rather than real samples. Zero-length
            // when the gap also collapses on the axis — harmless.
            if (runs.Count >= 2)
            {
                var accentDim = Solid(dark ? 0x60CDFF : 0x0078D4, 0x77);
                for (int k = 1; k < runs.Count; k++)
                {
                    var a = runs[k - 1].Last;
                    var b = runs[k].First;
                    Children.Add(new Line
                    {
                        X1 = a.X,
                        Y1 = a.Y,
                        X2 = b.X,
                        Y2 = b.Y,
                        Stroke = accentDim,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 3, 3 },
                    });
                }
            }
        }

        // Time axis: 5 evenly spaced local-time ticks (first left-aligned,
        // last right-aligned, rest centered). Ticks read the MAPPED axis, so
        // compressed mode labels the collapsed positions with the timestamp
        // each x position actually corresponds to. Granularity follows the
        // span (FormatTick): a 24h chart labels "HH:mm" — the "MM-dd" prefix
        // is noise at that scale — and day-plus spans label "MM-dd", which
        // also keeps the labels narrow enough for the 5-tick row. Skipped
        // entirely on the compressed axis: with off stretches collapsed, the
        // ticks no longer measure real elapsed time and only mislead — the
        // pure-line chart carries no time labels.
        if (showOffBands)
        {
            for (int k = 0; k <= 4; k++)
            {
                double x = _x0 + (_plotW * k / 4);
                var tb = new TextBlock
                {
                    Text = FormatTick(TsAtX(x)),
                    Foreground = labelBrush,
                    FontSize = 11,
                };
                Children.Add(tb);
                // Right-align the last tick (plus a reserve for the page's
                // overlay scrollbar), center the middle ones, and nudge the
                // first right so it clears the y-axis "0" label below-left.
                double tw = MeasuredWidth(tb);
                double tx = k switch
                {
                    0 => x + 8,
                    4 => x - tw - 14,
                    _ => x - (tw / 2),
                };
                Canvas.SetLeft(tb, Math.Clamp(tx, 0, Math.Max(ActualWidth - tw - 14, 0)));
                Canvas.SetTop(tb, _y0 + _plotH + 10);
            }
        }
    }

    /// <summary>Build the ts→x breakpoint list from the main series. With
    /// off bands hidden only ACTIVE intervals (connected, gap within the
    /// break threshold) advance x, collapsing everything else.</summary>
    private void BuildAxis(IReadOnlyList<Sample> main, bool showOffBands)
    {
        _axis.Clear();
        if (main.Count == 0)
        {
            return;
        }
        // Cumulative SECONDS per sample first, then scale the whole run to
        // the pixel width — the axis must be in pixels or TsAtX(X px)
        // lookups land seconds deep into the series instead of at the
        // intended fraction.
        var cum = new List<long> { 0 };
        long acc = 0;
        for (int i = 1; i < main.Count; i++)
        {
            long gap = main[i].Ts - main[i - 1].Ts;
            bool active = main[i].Connected && gap <= HistoryService.GapBreakSecs;
            if (showOffBands || active)
            {
                acc += gap;
            }
            cum.Add(acc);
        }
        double scale = acc > 0 ? _plotW / acc : 0;
        for (int i = 0; i < main.Count; i++)
        {
            _axis.Add((main[i].Ts, _x0 + (cum[i] * scale)));
        }
    }

    private double XOf(long ts)
    {
        // _axis is filled by BuildAxis with one breakpoint per sample and
        // every caller is gated on main.Count >= 2, so it is never empty
        // here. Binary search: ascending in Ts, and this runs per sample on
        // render plus per pointer move on the hover readout.
        int count = _axis.Count;
        if (ts <= _axis[0].Ts)
        {
            return _axis[0].X;
        }
        if (ts >= _axis[count - 1].Ts)
        {
            return _axis[count - 1].X;
        }
        int lo = 0, hi = count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_axis[mid].Ts <= ts)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        var a = _axis[lo];
        var b = _axis[hi];
        double f = (ts - a.Ts) / (double)Math.Max(b.Ts - a.Ts, 1);
        return a.X + (f * (b.X - a.X));
    }

    /// <summary>Chart-y pixel for a battery level. Single source shared by
    /// the render path and the hover readout.</summary>
    private double LevelY(int level)
        => _y0 + _plotH - (Math.Clamp(level, 0, 100) / 100.0 * _plotH);

    /// <summary>Inverse of XOf for ticks and the hover readout (binary
    /// search over the X-ascending axis).</summary>
    private long TsAtX(double px)
    {
        int count = _axis.Count;
        if (px <= _axis[0].X)
        {
            return _axis[0].Ts;
        }
        if (px >= _axis[count - 1].X)
        {
            return _axis[count - 1].Ts;
        }
        int lo = 0, hi = count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (_axis[mid].X <= px)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }
        var a = _axis[lo];
        var b = _axis[hi];
        double f = (px - a.X) / Math.Max(b.X - a.X, 0.001);
        return a.Ts + (long)(f * (b.Ts - a.Ts));
    }

    /// <summary>WinUI Measure on an element outside the tree can report 0 —
    /// right/center alignment then collapses at the plot edge and labels get
    /// cut. Fall back to a per-character estimate (FontSize 11 UI font ≈
    /// 6.5px/char).</summary>
    private static double MeasuredWidth(TextBlock tb)
        => tb.DesiredSize.Width > 0 ? tb.DesiredSize.Width : tb.Text.Length * 6.5;

    // — Hover readout (main series) —

    /// <summary>Create the three hover elements once, with brushes matching
    /// the current theme, and (re)attach them above everything Render drew.
    /// Re-created on theme flip so a flip mid-hover doesn't leave stale
    /// colors until the next Render.</summary>
    private void EnsureHoverElements(bool dark)
    {
        if (_hoverLine is null || _hoverDark != dark)
        {
            var accent = Solid(dark ? 0x60CDFF : 0x0078D4);
            _hoverLine = new Line
            {
                Stroke = accent,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
            };
            _hoverDot = new Ellipse { Width = 7, Height = 7, Fill = accent, IsHitTestVisible = false };
            _hoverLabel = new TextBlock { FontSize = 11, Foreground = Solid(dark ? 0xF3F3F3 : 0x1B1B1B) };
            _hoverBorder = new Border
            {
                Background = Solid(dark ? 0x2B2B2B : 0xF7F7F7),
                BorderBrush = accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Child = _hoverLabel,
                IsHitTestVisible = false,
            };
            _hoverDark = dark;
            _hoverStamp = "";
        }
        if (!_hoverAttached)
        {
            Children.Add(_hoverLine);
            Children.Add(_hoverDot);
            Children.Add(_hoverBorder);
            _hoverAttached = true;
        }
    }

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
        long ts = TsAtX(pt.X);
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
        double x = XOf(nearest.Ts);
        double y = LevelY(nearest.Level);

        EnsureHoverElements(ActualTheme == ElementTheme.Dark);
        var line = _hoverLine!;
        var dot = _hoverDot!;
        var border = _hoverBorder!;
        var label = _hoverLabel!;
        line.X1 = x;
        line.Y1 = _y0;
        line.X2 = x;
        line.Y2 = _y0 + _plotH;
        Canvas.SetLeft(dot, x - 3.5);
        Canvas.SetTop(dot, y - 3.5);
        // Keep the tooltip inside the chart: near the right edge it flips to
        // the LEFT of the cursor (the overlay scrollbar would otherwise clip
        // it), and x is clamped by the border's own measured width.
        string text = $"{FormatHoverStamp(nearest.Ts)}  {nearest.Level}%";
        if (text != _hoverStamp)
        {
            _hoverStamp = text;
            label.Text = text;
            border.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _hoverBorderW = border.DesiredSize.Width > 0
                ? border.DesiredSize.Width
                : text.Length * 6.2 + 16;
        }
        double bw = _hoverBorderW;
        double bx = x + 8;
        if (bx + bw > ActualWidth - 2)
        {
            bx = x - 8 - bw;
        }
        bx = Math.Clamp(bx, 2, Math.Max(ActualWidth - bw - 2, 2));
        double by = Math.Clamp(_y0 + 4, 0, Math.Max(ActualHeight - 28, 0));
        Canvas.SetLeft(border, bx);
        Canvas.SetTop(border, by);
        line.Visibility = Visibility.Visible;
        dot.Visibility = Visibility.Visible;
        border.Visibility = Visibility.Visible;
    }

    private void ClearHover()
    {
        if (_hoverAttached)
        {
            _hoverLine!.Visibility = Visibility.Collapsed;
            _hoverDot!.Visibility = Visibility.Collapsed;
            _hoverBorder!.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Local-time "MM-dd HH:mm" — the year is noise for a chart
    /// whose range is days to weeks and only makes labels wider (axis ticks
    /// and the hover readout share this).</summary>
    public static string FormatStamp(long ts)
        => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM-dd HH:mm");

    /// <summary>Span-aware stamp granularities, driven by _shortSpan set in
    /// Render: a ≤36h chart reads "HH:mm" on the axis (the date prefix is
    /// noise at that scale) and drops the date from the hover too; longer
    /// spans label the axis "MM-dd" only and keep the full stamp on the
    /// hover. The public FormatStamp stays full-grain for the cycle list.</summary>
    private string FormatTick(long ts)
        => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString(_shortSpan ? "HH:mm" : "MM-dd");

    private string FormatHoverStamp(long ts)
        => DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString(_shortSpan ? "HH:mm" : "MM-dd HH:mm");

    /// <summary>Drop run points that moved less than a pixel in BOTH axes
    /// from the last kept point: invisible detail, but a long history built
    /// XAML point collections with one entry per sample. Run endpoints always
    /// survive, so lines and areas still begin/end exactly at run bounds.
    /// Output capacity starts bounded: reserving pts.Count up front on a
    /// huge connected run allocates an LOH block (>85 KB) for an output that
    /// decimates down to a few hundred points.</summary>
    private static List<Windows.Foundation.Point> Decimate(List<Windows.Foundation.Point> pts)
    {
        if (pts.Count <= 2)
        {
            return pts;
        }
        var outPts = new List<Windows.Foundation.Point>(Math.Min(pts.Count, 1024)) { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var last = outPts[^1];
            var p = pts[i];
            if (Math.Abs(p.X - last.X) >= 1 || Math.Abs(p.Y - last.Y) >= 1)
            {
                outPts.Add(p);
            }
        }
        outPts.Add(pts[^1]);
        return outPts;
    }

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

    /// <summary>Solid color at the given alpha — for the off bands, which
    /// should tint rather than fully cover the acrylic behind them.</summary>
    private static SolidColorBrush Solid(long rgb, byte alpha)
    {
        var c = new Color { A = alpha, R = (byte)(rgb >> 16), G = (byte)(rgb >> 8), B = (byte)rgb };
        return new SolidColorBrush(c);
    }
}
