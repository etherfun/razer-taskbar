//! Port of src/taskbar.rs: taskbar discovery, Win10/Win11 placement, and
//! coexistence rules. See docs/agent-taskbar.md for the contracts:
//! - Win11 (`DesktopWindowContentBridge` present): anchor at
//!   `TrayNotifyWnd.left - width + 2`, vertically centered on `Start`.
//! - Classic/Win10: anchor relative to the `ReBarWindow32` band (fallback
//!   `WorkerW`, last resort the tray itself).
//! - No third-party avoidance by design: the widget sits at its anchor and
//!   draws above competing widgets (embed = band sibling #0, overlay = TOPMOST).

using System.Diagnostics;
using Microsoft.Win32;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Win32Consts;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;

namespace RazerTaskbar.Native;

public enum TaskbarKind
{
    Win11,
    Classic,
}

public readonly struct Placement
{
    /// <summary>Band the returned x/y are relative to (Shell_TrayWnd on Win11,
    /// the ReBarWindow32 band on Classic) — the caller converts to screen.</summary>
    public IntPtr Parent { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public TaskbarKind Kind { get; init; }
}

public static class TaskbarLocator
{
    public static IntPtr FindShellTray() => FindWindowW("Shell_TrayWnd", null);

    public static IntPtr FindChild(IntPtr parent, string className) =>
        FindWindowExW(parent, 0, className, null);

    public static RECT? WindowRect(IntPtr hwnd)
        => GetWindowRect(hwnd, out var rect) ? rect : null;

    /// <summary>Win11 detection: `DesktopWindowContentBridge` child exists.</summary>
    public static TaskbarKind DetectKind(IntPtr tray)
        => FindChild(tray, "Windows.UI.Composition.DesktopWindowContentBridge") != 0
            ? TaskbarKind.Win11
            : TaskbarKind.Classic;

    /// <summary>Compute widget placement inside the taskbar band. Right side
    /// (default): `x = notify.left - w + 2`; the widgets board reserve is a
    /// hard limit. Left side: anchored after `Start`, keeping the reserved
    /// icons zone.</summary>
    public static Placement? ComputePlacement(
        IntPtr tray,
        int w,
        int h,
        string side,
        int offsetLeft,
        int offsetTop,
        int leftSpaceWin11,
        int rightSpaceFallback,
        bool avoidWidgets)
    {
        var kind = DetectKind(tray);
        var bar = WindowRect(tray);
        if (bar is not { } barRect)
        {
            return null;
        }
        int barW = barRect.Right - barRect.Left;
        int barH = barRect.Bottom - barRect.Top;

        if (kind == TaskbarKind.Win11)
        {
            var notify = FindChild(tray, "TrayNotifyWnd") is { } nh && nh != 0 ? WindowRect(nh) : null;
            // The legacy `Start` HWND is hidden (zero-size or invisible) on
            // Win11 — ignore it and fall back to the taskbar band.
            RECT? start = null;
            var sh = FindChild(tray, "Start");
            if (sh != 0 && IsWindowVisible(sh))
            {
                start = WindowRect(sh) is { } sr && sr.Right - sr.Left > 0 && sr.Bottom - sr.Top > 0 ? sr : null;
            }
            int notifyLeft = notify is { } nr ? nr.Left - barRect.Left : barW - Math.Max(0, rightSpaceFallback);
            int startH = start is { } startRect ? startRect.Bottom - startRect.Top : barH;
            int y = (startH - h) / 2 + (barH - startH) + offsetTop;

            // The weather/widgets board is NOT an HWND — it is XAML content
            // inside the bridge. Its UIA rect is the ground truth; fall back
            // to the registry estimate only when UIA is unavailable. The
            // reserve is UNCONDITIONAL — the board is OS chrome.
            var board = WidgetsButtonRect();
            int? boardLeft = board is { } boardRect ? boardRect.Left - barRect.Left : null;
            int startLeft = start is { } s ? s.Left - barRect.Left : 0;
            int startW = start is { } s2 ? s2.Right - s2.Left : 0;
            int minX = startLeft + startW + 2 + WidgetsZoneWidth();
            if (avoidWidgets)
            {
                minX = Math.Max(minX, startLeft + startW + 2 + Math.Max(0, leftSpaceWin11));
            }
            // Right-side usable band ends where the board begins — but only
            // when the board is actually adjacent to the tray (right half).
            // On newer builds the widgets entry lives at the LEFT edge
            // (observed boardLeft=0 on 26340); capping the right anchor by it
            // would drag the widget to x=2, so the left min_x zone covers it.
            int maxRight = notifyLeft + 2;
            if (side != "left" && boardLeft is { } bl && bl > barW / 2)
            {
                maxRight = Math.Min(maxRight, bl - 2);
            }

            var diag = $"side={side} notifyLeft={notifyLeft} boardLeft={(boardLeft is { } blv ? blv.ToString() : "none")} " +
                     $"startL={startLeft} startW={startW} min_x={minX} max_right={maxRight} w={w}";
            if (diag != _lastPlacementDiag)
            {
                Log.Info("placement: " + diag);
                _lastPlacementDiag = diag;
            }
            int x = AnchorXWin11(side, maxRight - 2, minX, w) + offsetLeft;
            // The board reserve is a hard floor for LEFT-anchored widgets:
            // clamp() alone could push us back onto the weather board when
            // the band is crowded, so re-assert min_x afterwards.
            if (side == "left")
            {
                x = Math.Max(x, minX);
            }
            x = Math.Clamp(x, 2, Math.Max(barW - w - 2, 2));

            return new Placement { Parent = tray, X = x, Y = y, W = w, H = h, Kind = kind };
        }
        else
        {
            // Anchor on the ReBarWindow32 band (WorkerW fallback, tray last
            // resort); the returned x/y are relative to that band.
            var rebar = FindChild(tray, "ReBarWindow32");
            if (rebar == 0)
            {
                rebar = FindChild(tray, "WorkerW");
            }
            if (rebar == 0)
            {
                rebar = tray;
            }
            var band = WindowRect(rebar) ?? barRect;
            int bandW = band.Right - band.Left;
            int bandH = band.Bottom - band.Top;
            int x = AnchorXClassic(side, bandW, w) + offsetLeft;
            x = Math.Clamp(x, 2, Math.Max(bandW - w - 2, 2));
            int y = (bandH - h) / 2 + offsetTop;
            return new Placement { Parent = rebar, X = x, Y = y, W = w, H = h, Kind = kind };
        }
    }

    /// <summary>Ideal anchor for Win11 bands.</summary>
    private static int AnchorXWin11(string side, int notifyLeft, int minX, int w)
        => side == "left" ? minX : notifyLeft - w + 2;

    /// <summary>Ideal anchor for Classic bands.</summary>
    private static int AnchorXClassic(string side, int bandW, int w)
        => side == "left" ? 2 : bandW - w - 2;

    // — Widgets board (weather) rect cache —

    private sealed class WidgetsRectCache
    {
        public RECT? Rect;
        /// <summary>The board sits just left of TrayNotifyWnd: a changed notify
        /// edge means the board moved, so the cache must not survive it.</summary>
        public int NotifyLeft;
        public Stopwatch Seen = Stopwatch.StartNew();
    }

    /// <summary>How long the widgets board UIA rect stays valid before re-querying.</summary>
    private static readonly TimeSpan WidgetsCacheTtl = TimeSpan.FromSeconds(30);

    private static readonly object WidgetsCacheLock = new();
    private static string? _lastPlacementDiag;
    private static WidgetsRectCache? _widgetsCache;

    /// <summary>Drop the cached widgets board rect (taskbar rebuilt → TaskbarCreated).</summary>
    public static void InvalidateWidgetsCache()
    {
        lock (WidgetsCacheLock)
        {
            _widgetsCache = null;
        }
    }

    /// <summary>Screen rect of the Win11 widgets board (weather) button, via
    /// UIA. The board is NOT an HWND occupant; UIA is the only reliable source.
    /// Cached for WidgetsCacheTtl or until the notify edge shifts. When the
    /// board is disabled (registry TaskbarDa=0) the search is skipped.</summary>
    public static RECT? WidgetsButtonRect()
    {
        // Registry first: with the board off, UIA would report nothing — skip
        // the search (registry flips are picked up live on the next placement).
        if (!WidgetsShown())
        {
            return null;
        }
        int notifyLeft = 0;
        var tray = FindShellTray();
        if (tray != 0)
        {
            var nh = FindChild(tray, "TrayNotifyWnd");
            if (nh != 0 && WindowRect(nh) is { } nr)
            {
                notifyLeft = nr.Left;
            }
        }
        lock (WidgetsCacheLock)
        {
            if (_widgetsCache is { } c && c.NotifyLeft == notifyLeft && c.Seen.Elapsed < WidgetsCacheTtl)
            {
                return c.Rect;
            }
        }
        var rect = WidgetsButtonRectUia();
        lock (WidgetsCacheLock)
        {
            _widgetsCache = new WidgetsRectCache { Rect = rect, NotifyLeft = notifyLeft };
        }
        return rect;
    }

    /// <summary>Uncached UIA search for the `WidgetsButton` rect. Engine-side
    /// descendant search: FindFirst pushes the traversal into the provider,
    /// which keeps working when the walker view does not (26340 observation).</summary>
    private static RECT? WidgetsButtonRectUia()
    {
        try
        {
            var uia = Interop.UiaInterop.Create();
            if (uia is null)
            {
                return null;
            }
            var tray = FindShellTray();
            if (tray == 0)
            {
                return null;
            }
            uia.ElementFromHandle(tray, out var root);
            uia.CreatePropertyCondition(
                Win32Consts.UIA_AUTOMATION_ID_PROPERTY_ID,
                "WidgetsButton",
                out var condition);
            root.FindFirst(Win32Consts.TREE_SCOPE_DESCENDANTS, condition, out var el);
            el.GetCurrentBoundingRectangle(out var r);
            var rect = new RECT
            {
                Left = r.left,
                Top = r.top,
                Right = r.right,
                Bottom = r.bottom,
            };
            el.GetCurrentName(out var name);
            Log.Info($"WidgetsButton found: name=\"{name}\" rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            if (rect.Right - rect.Left <= 0 || rect.Bottom - rect.Top <= 0)
            {
                DumpTaskbarTree(uia, root);
            }
            return rect;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool _treeDumped;

    /// <summary>One-shot: log every taskbar descendant with a non-empty
    /// bounding rect that intersects the tray band, so the real widgets entry
    /// can be identified when the AutomationId lookup returns a degenerate
    /// rect (new builds may rename it).</summary>
    private static void DumpTaskbarTree(Interop.IUIAutomation uia, Interop.IUIAutomationElement root)
    {
        if (_treeDumped)
        {
            return;
        }
        _treeDumped = true;
        try
        {
            root.GetCurrentBoundingRectangle(out var rr);
            Log.Info($"tray root rect=({rr.left},{rr.top})-({rr.right},{rr.bottom})");
            uia.GetControlViewWalker(out var walker);
            var stack = new List<Interop.IUIAutomationElement> { root };
            int guard = 0, logged = 0;
            while (stack.Count > 0 && guard < 300 && logged < 40)
            {
                var el = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                guard++;
                el.GetCurrentBoundingRectangle(out var r);
                if (r.right - r.left > 0 && r.bottom - r.top > 0)
                {
                    el.GetCurrentAutomationId(out var aid);
                    el.GetCurrentName(out var n);
                    el.GetCurrentClassName(out var cn);
                    Log.Info($"  el aid=\"{aid}\" name=\"{n}\" class=\"{cn}\" rect=({r.left},{r.top})-({r.right},{r.bottom})");
                    logged++;
                }
                try
                {
                    walker.GetFirstChildElement(el, out var child);
                    int n2 = 0;
                    while (n2 < 30)
                    {
                        stack.Add(child);
                        walker.GetNextSiblingElement(child, out child);
                        n2 += 1;
                    }
                }
                catch (Exception)
                {
                    // Leaf/no children — fine.
                }
            }
            Log.Info($"tree dump done: guard={guard} logged={logged}");
        }
        catch (Exception e)
        {
            Log.Error("tree dump failed", e);
        }
    }

    /// <summary>Width of the Win11 widgets board (weather) zone on the left
    /// edge (~150px at 96 DPI for icon + temperature).</summary>
    private static int WidgetsZoneWidth() => WidgetsShown() ? 160 : 0;

    public static bool WidgetsShown()
        => (ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa") ?? 1) != 0;

    private static uint? ReadDword(string subkey, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subkey);
            if (key is null)
            {
                return null;
            }
            var data = key.GetValue(value);
            return data switch
            {
                int i => unchecked((uint)i),
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>`true` when `tray` sits ABOVE `widget` in z-order — the
    /// taskbar visually covers the widget. Bounded GW_HWNDPREV walk: a few
    /// syscalls, cheap enough for the 1s tick.</summary>
    public static bool TrayAbove(IntPtr widget, IntPtr tray)
    {
        var cur = GetWindow(widget, GW_HWNDPREV);
        int hops = 0;
        while (cur != 0 && hops < 16)
        {
            if (cur == tray)
            {
                return true;
            }
            cur = GetWindow(cur, GW_HWNDPREV);
            hops += 1;
        }
        return false;
    }

    /// <summary>Position a top-level overlay above the taskbar
    /// (Taskbar-Lyrics style): HWND_TOPMOST + SWP_NOACTIVATE; SWP_NOOWNERZORDER
    /// | SWP_NOSENDCHANGING mirror Taskbar-Lyrics.</summary>
    public static void MoveOverlay(IntPtr widget, int x, int y, int w, int h)
    {
        SetWindowPos(widget, HwndTopmost, x, y, w, h,
            SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_NOSENDCHANGING | SWP_SHOWWINDOW);
    }

    /// <summary>Experimental embed mode (Lyricify's taskbar lyrics): reparent
    /// `widget` into the taskbar band `parent` as a plain WS_CHILD and park it
    /// at sibling index 0. Unsupported and update-fragile; the caller falls
    /// back to the overlay when this returns false.</summary>
    public static bool SetTaskbarChild(IntPtr widget, IntPtr parent, bool embed)
    {
        ShowWindow(widget, SW_HIDE);
        if (embed)
        {
            var ex = GetWindowLongPtrW(widget, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtrW(widget, GWL_EXSTYLE, (IntPtr)(ex & ~((long)WS_EX_TOPMOST)));
            if (SetParent(widget, parent) == 0)
            {
                // Restore exactly what was there and let the caller stay in
                // overlay mode.
                SetWindowLongPtrW(widget, GWL_EXSTYLE, (IntPtr)ex);
                ShowWindow(widget, SW_SHOWNA);
                return false;
            }
            var style = GetWindowLongPtrW(widget, GWL_STYLE).ToInt64();
            SetWindowLongPtrW(widget, GWL_STYLE, (IntPtr)((style & ~(long)WS_POPUP) | WS_CHILD));
            // Sibling #0 — above DesktopWindowContentBridge.
            SetWindowPos(widget, HwndTop, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            var style = GetWindowLongPtrW(widget, GWL_STYLE).ToInt64();
            SetWindowLongPtrW(widget, GWL_STYLE, (IntPtr)((style & ~(long)WS_CHILD) | WS_POPUP));
            var ex = GetWindowLongPtrW(widget, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtrW(widget, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOPMOST));
            SetParent(widget, 0);
        }
        return true;
    }

    /// <summary>`true` while `widget` is parented into `parent` (embed mode's
    /// live check — explorer restarts re-create the band).</summary>
    public static bool IsChildOf(IntPtr widget, IntPtr parent)
        => GetAncestor(widget, GA_PARENT) == parent;

    /// <summary>Origin of `parent`'s client area in screen coordinates.</summary>
    public static (int X, int Y) ClientOrigin(IntPtr parent)
    {
        var pt = new POINT(0, 0);
        if (ClientToScreen(parent, ref pt))
        {
            return (pt.X, pt.Y);
        }
        return WindowRect(parent) is { } r ? (r.Left, r.Top) : (0, 0);
    }

    /// <summary>Re-assert sibling #0 for an embedded widget.</summary>
    public static void ReassertChildTop(IntPtr widget)
    {
        SetWindowPos(widget, HwndTop, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndTop = new(0);
}
