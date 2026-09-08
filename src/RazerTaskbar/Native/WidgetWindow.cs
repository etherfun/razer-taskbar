//! Port of src/window.rs: overlay widget floating above the taskbar
//! (Taskbar-Lyrics style), native GDI battery UI.
//!
//! - Class `RazerTaskbarWidget`, top-level `WS_POPUP | WS_EX_LAYERED`,
//!   `HWND_TOPMOST`. WM_PAINT draws the Win11-style two-row widget — battery
//!   glyph + percentage on top, predicted time below — into a 32bpp DIB that
//!   UpdateLayeredWindow presents with per-pixel alpha (AA edges blend with
//!   the real backdrop, like DirectWrite taskbar text).
//! - Embed mode (`embed_into_taskbar`): the display window is DESTROYED and
//!   recreated as a direct `WS_CHILD` of Shell_TrayWnd. Presentation is the
//!   same ULW pipeline as the overlay — the band ignores SetParent-migrated
//!   windows and freezes ULW frames between band rebuilds, so AlphaPresent
//!   pokes a rebuild (throwaway 1x1 band child) after every frame
//!   (docs/agent-embed.md).
//! - A never-shown anchor window (same class) owns every thread-lifetime
//!   binding: timers, WmInvoke, UIA layout events and the TaskbarCreated
//!   broadcast. The display window is disposable — as a band child it dies
//!   together with the taskbar on explorer restarts, and the anchor detects
//!   that and recreates the widget.
//! - Right-click menu lives on the tray icon (overlay is click-through).
//! - Layout is event-driven: TaskbarCreated broadcast (explorer restart) and
//!   UIA structure-change events both trigger an immediately coalesced
//!   placement pass; the 1s timer stays as a fallback poll that also drives
//!   the tray refresh. Settings/history open the WinUI3 window on the main
//!   thread.

using System.Diagnostics;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.Gdi32;
using RazerTaskbar.Native.Interop;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

public static class WidgetWindow
{
    private const string ClassName = "RazerTaskbarWidget";
    private const string WindowTitle = "RazerTaskbarWidget";
    private const IntPtr TimerId = 1;
    /// <summary>One-shot coalescing timer for UIA structure-change bursts.</summary>
    private const IntPtr TimerLayout = 2;
    /// <summary>Fast hover-poll timer driving the device-list popover.</summary>
    private const IntPtr TimerHover = 3;
    /// <summary>Fast re-assert burst after the taskbar covered the widget.</summary>
    private const IntPtr TimerZBurst = 4;
    /// <summary>Cross-fade animation ticks while a device switch blends in.</summary>
    private const IntPtr TimerFade = 5;
    private const uint ZBurstTickMs = 150;
    private const uint ZBurstTicks = 12;
    /// <summary>Cross-fade duration and tick for device-switch transitions.</summary>
    private const int FadeMs = 300;
    private const uint FadeTickMs = 16;
    /// <summary>Log a "covered" event at most this often.</summary>
    private static readonly TimeSpan CoveredLogEvery = TimeSpan.FromSeconds(30);
    private const uint HoverPollMs = 120;
    private const uint LayoutDebounceMs = 250;
    private static readonly TimeSpan LayoutDebounce = TimeSpan.FromMilliseconds(LayoutDebounceMs);

    /// <summary>Posted by uia_events on every taskbar structure change.</summary>
    private const uint WmAppLayout = WM_APP + 2;

    private const ushort IdExit = 1001;
    private const ushort IdHistoryView = 1010;
    private const ushort IdSettings = 1011;
    private const ushort IdDeviceBase = 2000;

    /// <summary>Everything drawn depends on this tuple; when unchanged the
    /// per-tick repaint is skipped entirely (layered window content persists
    /// across moves, so only content/size changes need a real repaint).</summary>
    private sealed record PaintSig(
        string Top,
        string Bottom,
        int Level,
        bool Charging,
        bool Saver,
        bool Connected,
        int W,
        int H);

    /// <summary>Widget-thread state (Rust STATE). Only touched on that thread.</summary>
    private sealed class WidgetState
    {
        public IntPtr Tray;
        /// <summary>The never-shown anchor window: owns timers, WmInvoke,
        /// UIA layout events and TaskbarCreated. Outlives the display window.</summary>
        public IntPtr MsgHwnd;
        public IntPtr Hwnd;
        public int WidgetW;
        public int WidgetH;
        /// <summary>Offscreen render surface: a 32bpp top-down DIB section.
        /// Everything paints here (GDI leaves the alpha byte zero), then
        /// AlphaPresent turns coverage into premultiplied alpha and hands the
        /// frame to DWM via UpdateLayeredWindow — per-pixel transparency like
        /// the native widgets, no black color key. Both modes present through
        /// this surface (embed pokes a band rebuild after each frame).</summary>
        public IntPtr MemDc;
        public IntPtr MemBmp;
        public IntPtr MemBits;
        public int MemW;
        public int MemH;
        public bool UlwFailedLogged;
        public (int X, int Y, int W, int H)? LastLayout;
        public string? LastLog;
        public PaintSig? PaintedSig;
        public Stopwatch LastLayoutPass = Stopwatch.StartNew();
        public bool LayoutPending;
        public uint ZBurstLeft;
        public Stopwatch? LastCoveredLog;
        public bool Embedded;
        public bool EmbedFailed;
        // — Cross-fade transition (overlay/ULW presentation only) —
        /// <summary>Premultiplied previous / new frame; both null while idle.</summary>
        public byte[]? FadePrev;
        public byte[]? FadeNew;
        public long FadeStart;
        /// <summary>Device handle whose frame is fully on screen (null until
        /// the first paint) — a change arms the cross-fade.</summary>
        public string? OnScreenHandle;
        /// <summary>Set right before the final DestroyWindow on exit: makes
        /// WM_DESTROY run the full teardown + quit instead of the lightweight
        /// recreate path.</summary>
        public bool Teardown;
    }

    private static WidgetState _state = null!;
    private static IntPtr _hinstance;
    private static uint _taskbarCreatedMsg;
    /// <summary>TaskbarCreated reaches both the anchor and (in overlay mode)
    /// the display window — handle it once per broadcast.</summary>
    private static readonly Stopwatch LastTaskbarRebind = Stopwatch.StartNew();

    private static readonly WndProc WndProcDelegate = WndProcImpl;

    /// <summary>Current display window (0 while recreating). The anchor window
    /// is WidgetThread.Hwnd; the tray icon, hover panel and painting bind to
    /// this one.</summary>
    public static IntPtr DisplayHwnd => _state?.Hwnd ?? 0;

    /// <summary>Invalidate the display window (content changed without a
    /// move — the paint path dedupes by signature).</summary>
    public static void InvalidateDisplay()
    {
        var st = _state;
        if (st != null && st.Hwnd != 0 && IsWindow(st.Hwnd))
        {
            InvalidateRect(st.Hwnd, 0, true);
        }
    }

    /// <summary>Tear the widget down (tray menu / app exit): leave the
    /// ghost-free invisible frame when embedded, then destroy the anchor —
    /// its WM_DESTROY runs the full teardown and quits the widget thread.</summary>
    public static void ExitWidget()
    {
        var st = _state;
        if (st is null)
        {
            return;
        }
        st.Teardown = true;
        if (st.Embedded)
        {
            SilentFrame(st);
        }
        var hwnd = st.MsgHwnd != 0 && IsWindow(st.MsgHwnd) ? st.MsgHwnd : st.Hwnd;
        if (hwnd != 0 && IsWindow(hwnd))
        {
            DestroyWindow(hwnd);
        }
    }

    /// <summary>Port of run_message_loop. Runs on the dedicated STA widget
    /// thread; never returns until quit.</summary>
    public static void Run()
    {
        var instance = GetModuleHandleW(null);
        _hinstance = instance;
        var st = new WidgetState();
        var config = AppState.Instance.ConfigSnapshot();

        var tray = TaskbarLocator.FindShellTray();
        if (tray == 0)
        {
            Console.Error.WriteLine("razer-taskbar: Shell_TrayWnd not found");
            App.RequestExit();
            return;
        }
        st.Tray = tray;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        var wc = new WNDCLASSW
        {
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = WndProcDelegate,
            hInstance = instance,
            lpszClassName = ClassName,
            hCursor = LoadCursorW(0, (IntPtr)IdcArrow),
        };
        ushort atom = RegisterClassW(ref wc);
        if (atom == 0)
        {
            Console.Error.WriteLine($"razer-taskbar: RegisterClassW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        // The anchor window is never shown; it only hosts the message
        // bindings that must survive display-window recreation (timers,
        // WmInvoke, UIA layout events, TaskbarCreated — a band child dies
        // together with the taskbar on explorer restarts).
        st.MsgHwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName, WindowTitle,
            WS_POPUP,
            0, 0, 0, 0,
            0, 0, instance, 0);
        if (st.MsgHwnd == 0)
        {
            Console.Error.WriteLine($"razer-taskbar: anchor CreateWindowExW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            App.RequestExit();
            return;
        }

        // 144x48: full taskbar height, wide enough for icon + "100%" text.
        // Embed mode births the window straight into the band (a migrated
        // window would never composite — docs/agent-embed.md).
        bool embed = config.EmbedIntoTaskbar && TaskbarLocator.DetectKind(tray) == TaskbarKind.Win11;
        var hwnd = CreateWidgetHwnd(embed, embed ? tray : 0);
        if (hwnd == 0)
        {
            Console.Error.WriteLine($"razer-taskbar: CreateWindowExW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            App.RequestExit();
            return;
        }
        st.Hwnd = hwnd;
        st.Embedded = embed;
        st.WidgetW = 144;
        st.WidgetH = 48;
        _state = st;
        WidgetThread.Init(st.MsgHwnd);

        // Per-pixel-alpha layered presentation (UpdateLayeredWindow): text AA
        // blends against the REAL taskbar like the native widgets' DirectWrite
        // text. A layered window paints nothing until its first ULW call —
        // push one fully transparent frame now (AlphaPresent pokes the band
        // to capture it in embed mode).
        EnsureMemSurface(st, hwnd, st.WidgetW, st.WidgetH);
        if (st.MemDc != 0)
        {
            AlphaPresent(st, hwnd);
        }

        // First call anchors directly: the window still sits at its 0,0
        // creation rect, which is not a position worth defending.
        PlaceWidget();
        if (config.ShowTrayIcon)
        {
            TrayIcon.EnsureCreated(hwnd);
        }
        SetTimer(st.MsgHwnd, TimerId, 1000, 0);
        SetTimer(st.MsgHwnd, TimerHover, HoverPollMs, 0);

        // Event-driven re-layout (Taskbar-Lyrics port).
        UiaEvents.Spawn(st.MsgHwnd, WmAppLayout);

        var msg = new MSG();
        while (GetMessageW(out msg, 0, 0, 0) != 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    /// <summary>Create the display window. Overlay: hidden top-level
    /// `WS_POPUP`, shown by the first placement pass (SWP_SHOWWINDOW).
    /// Embedded: hidden `WS_CHILD` of the taskbar band. Both use ULW
    /// presentation; the caller pushes an initial transparent frame.
    /// Caller assigns st.Hwnd/st.Embedded.</summary>
    private static IntPtr CreateWidgetHwnd(bool embedded, IntPtr parent)
    {
        uint ex = WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
        uint style = embedded ? (WS_CHILD | WS_CLIPSIBLINGS) : (WS_POPUP | WS_CLIPSIBLINGS);
        return CreateWindowExW(
            ex,
            ClassName, WindowTitle,
            style,
            0, 0, 144, 48,
            embedded ? parent : 0, 0, _hinstance, 0);
    }

    /// <summary>The band snapshots ULW children only on band rebuilds (a band
    /// child being created/destroyed); between rebuilds their frames are
    /// frozen and never reach the screen. After each presented frame, poke a
    /// rebuild with a throwaway 1x1 helper child so the fresh frame — and
    /// every other child's — is re-captured. Paints are rare (signature
    /// dedupe), so the churn is negligible (docs/agent-embed.md).</summary>
    private static void PokeBandRebuild()
    {
        var st = _state;
        if (st.Tray == 0)
        {
            return;
        }
        var helper = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            ClassName, WindowTitle,
            WS_CHILD,
            0, 0, 1, 1,
            st.Tray, 0, _hinstance, 0);
        if (helper != 0)
        {
            // Keep the leaked 1x1 destroy-snapshot invisible.
            SetLayeredWindowAttributes(helper, 0, 0, LWA_ALPHA);
            DestroyWindow(helper);
        }
    }

    /// <summary>Switch overlay↔band-child by DESTROYING and recreating the
    /// display window — the band ignores SetParent-migrated windows and ULW
    /// presentation, so the widget must be born into the band
    /// (docs/agent-embed.md). Runs on the widget thread; retargets the tray
    /// icon and resets paint/layout dedupe state. The anchor window and its
    /// timers/messages are untouched.</summary>
    private static bool RecreateWindow(bool embedded, IntPtr parent)
    {
        var st = _state;
        var old = st.Hwnd;
        if (old != 0 && IsWindow(old))
        {
            if (st.Embedded)
            {
                // Ghost protocol: the band keeps a snapshot of a destroyed
                // child's last frame forever — leave a fully key-colored
                // (invisible) frame behind instead of the widget.
                SilentFrame(st);
            }
            DestroyWindow(old);
        }
        var hwnd = CreateWidgetHwnd(embedded, parent);
        if (hwnd == 0)
        {
            Console.Error.WriteLine($"razer-taskbar: widget CreateWindowExW failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            st.Hwnd = 0;
            return false;
        }
        st.Hwnd = hwnd;
        st.Embedded = embedded;
        st.PaintedSig = null;
        st.LastLayout = null;
        st.LastLog = null;
        st.UlwFailedLogged = false;
        EndFade(st);
        st.OnScreenHandle = null;
        st.WidgetW = 144;
        st.WidgetH = 48;
        // Transparent pre-show frame (ULW windows paint nothing otherwise);
        // in embed mode AlphaPresent pokes the band to capture it.
        EnsureMemSurface(st, hwnd, st.WidgetW, st.WidgetH);
        if (st.MemDc != 0)
        {
            AlphaPresent(st, hwnd);
        }
        if (AppState.Instance.ConfigSnapshot().ShowTrayIcon)
        {
            TrayIcon.EnsureCreated(hwnd);
        }
        return true;
    }

    /// <summary>Paint one invisible frame on the embedded band child and poke
    /// a band rebuild: the destroy-snapshot then leaks nothing visible.</summary>
    private static void SilentFrame(WidgetState st)
    {
        if (st.Hwnd == 0 || !IsWindow(st.Hwnd) || st.MemDc == 0)
        {
            return; // never presented — no snapshot to silence
        }
        EndFade(st);
        PatBlt(st.MemDc, 0, 0, st.MemW, st.MemH, BLACKNESS);
        // AlphaPresent pokes the band so the invisible frame is what gets
        // snapshotted, then give DWM a beat to present it before the window
        // (and its surface) disappears.
        AlphaPresent(st, st.Hwnd);
        System.Threading.Thread.Sleep(120);
    }

    private static float DpiScale(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0f : dpi / 96.0f;
    }

    private static (int W, int H) WidgetSize(IntPtr hwnd)
    {
        float s = DpiScale(hwnd);
        // 144px at 96 DPI fits the widest row; height 40 (taskbar is 48).
        return (
            Math.Max((int)MathF.Round(144.0f * s), 96),
            Math.Max((int)MathF.Round(40.0f * s), 32));
    }

    /// <summary>Recompute and apply the placement. Runs on the 1s fallback
    /// poll, after every coalesced UIA structure event, and on first anchor —
    /// move/invalidate and log are deduped against the previous pass.</summary>
    internal static void PlaceWidget() => PlaceWidgetCore(true);

    private static void PlaceWidgetCore(bool allowRetry)
    {
        var st = _state;
        if (st.MsgHwnd == 0)
        {
            return;
        }
        var hwnd = st.Hwnd;
        if (hwnd != 0 && IsWindow(hwnd))
        {
            (st.WidgetW, st.WidgetH) = WidgetSize(hwnd);
        }
        var config = AppState.Instance.ConfigSnapshot();

        var pl = TaskbarLocator.ComputePlacement(
            st.Tray, st.WidgetW, st.WidgetH,
            config.WidgetSide,
            config.WindowOffsetLeft,
            config.WindowOffsetTop,
            config.TaskbarLeftSpaceWin11,
            config.TaskbarRightSpaceWin11,
            config.AvoidOverlapWithWidgets,
            config.EmbedIntoWidgetsSpace);
        if (pl is not { } placement)
        {
            return;
        }

        // Embed state machine (v2): the display window is DESTROYED and
        // recreated — born into the band as WS_CHILD — instead of being
        // SetParent-migrated, because the band ignores migrated windows and
        // ULW presentation entirely (docs/agent-embed.md). A dead window (the
        // band child died together with the taskbar on an explorer restart)
        // recreates too.
        bool wantEmbed = config.EmbedIntoTaskbar && !st.EmbedFailed
            && placement.Kind == TaskbarKind.Win11;
        bool needSwitch = st.Hwnd == 0 || !IsWindow(st.Hwnd) || st.Embedded != wantEmbed;
        if (needSwitch)
        {
            if (!RecreateWindow(wantEmbed, wantEmbed ? placement.Parent : 0))
            {
                if (!wantEmbed)
                {
                    return; // nothing drawable; retry on the next tick
                }
                // Band-child creation refused: stick to the overlay for this
                // session and retry the switch once, as an overlay.
                st.EmbedFailed = true;
                Console.Error.WriteLine("razer-taskbar: embed rejected, staying as overlay");
                if (!allowRetry || !RecreateWindow(false, 0))
                {
                    return;
                }
            }
            else
            {
                Console.Error.WriteLine($"razer-taskbar: embed={wantEmbed} applied");
            }
            st.LastLayout = null;
            if (allowRetry)
            {
                // Re-run with the fresh window's real DPI size.
                PlaceWidgetCore(false);
                return;
            }
        }
        else if (st.Embedded && !TaskbarLocator.IsChildOf(st.Hwnd, placement.Parent))
        {
            // Alive but disowned (the band was rebuilt without destroying us):
            // same remedy — recreate into the new band.
            if (RecreateWindow(true, placement.Parent))
            {
                st.LastLayout = null;
                Console.Error.WriteLine("razer-taskbar: re-embedded after parent change");
                if (allowRetry)
                {
                    PlaceWidgetCore(false);
                    return;
                }
            }
            else
            {
                st.EmbedFailed = true;
                Console.Error.WriteLine("razer-taskbar: re-embed failed, falling back to overlay");
                if (!RecreateWindow(false, 0) || !allowRetry)
                {
                    return;
                }
                st.LastLayout = null;
                PlaceWidgetCore(false);
                return;
            }
        }

        hwnd = st.Hwnd;
        if (hwnd == 0 || !IsWindow(hwnd))
        {
            return;
        }

        // Convert to screen coords relative to the placement's own parent:
        // Win11 anchors on Shell_TrayWnd, Classic on the ReBarWindow32 band.
        int px = 0, py = 0;
        if (TaskbarLocator.WindowRect(placement.Parent) is { } pr)
        {
            px = pr.Left;
            py = pr.Top;
        }
        int sx = px + placement.X;
        int sy = py + placement.Y;

        // One log line per distinct state; unchanged states stay silent.
        string line = $"overlay kind={placement.Kind} pos=({sx},{sy}) size={st.WidgetW}x{st.WidgetH} embed={st.Embedded}";
        if (st.LastLog != line)
        {
            Console.Error.WriteLine($"razer-taskbar: {line}");
            st.LastLog = line;
        }

        // Apply. MoveOverlay also re-asserts HWND_TOPMOST every pass; the
        // embedded branch re-asserts sibling #0 (HwndTop). A pure move needs
        // no repaint — invalidate only when the rect changed.
        int w = st.WidgetW;
        int h = st.WidgetH;
        bool changed = st.LastLayout != (sx, sy, w, h);
        if (st.Embedded)
        {
            // Children position in the band's CLIENT coordinates.
            var (cox, coy) = TaskbarLocator.ClientOrigin(placement.Parent);
            SetWindowPos(hwnd, HwndTop, sx - cox, sy - coy, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            TaskbarLocator.MoveOverlay(hwnd, sx, sy, w, h);
        }
        if (changed)
        {
            st.LastLayout = (sx, sy, w, h);
            InvalidateRect(hwnd, 0, true);
        }
    }

    private static readonly IntPtr HwndTop = new(0);
    private static readonly IntPtr HwndTopmost = new(-1);
    private const long IdcArrow = 32512;

    private static IntPtr WndProcImpl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // A .NET exception escaping the WndProc delegate kills the process;
        // the Rust wnd_proc could only abort via explicit panic. Log + stay.
        try
        {
            return WndProcDispatch(hwnd, msg, wParam, lParam);
        }
        catch (Exception e)
        {
            Log.Error($"WndProc exception (msg=0x{msg:X})", e);
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static IntPtr WndProcDispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var st = _state;
        if (st is null)
        {
            // Messages during CreateWindowExW (WM_NCCREATE/WM_CREATE) arrive
            // before the state exists — Rust's wnd_proc tolerated that via
            // Option; mirror it with DefWindowProc.
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        if (msg == WM_TIMER)
        {
            var timer = wParam;
            if (timer == TimerId)
            {
                var wh = st.Hwnd;
                bool alive = wh != 0 && IsWindow(wh);
                if (st.Embedded)
                {
                    // Children have no TOPMOST band: re-assert sibling order.
                    if (alive)
                    {
                        TaskbarLocator.ReassertChildTop(wh);
                    }
                }
                else if (alive && ZCovered(wh))
                {
                    // Detect "the taskbar raised itself above us" BEFORE the
                    // re-asserting placement pass, and arm a fast repair burst.
                    ArmZBurst();
                }
                PlaceWidget();
                TrayIcon.Refresh();
                // Battery changes never move the rect; invalidate each second
                // (paint() dedupes by signature).
                if (alive)
                {
                    InvalidateRect(wh, 0, true);
                }
                // A layout request coalesced right before the poll tick
                // should not wait for its own timer — flush it now.
                FlushPendingLayout();
            }
            else if (timer == TimerLayout)
            {
                KillTimer(st.MsgHwnd, TimerLayout);
                FlushPendingLayout();
            }
            else if (timer == TimerHover)
            {
                // Piggyback the z-order check on the 120ms tick (recover from
                // taskbar raises within ~120ms).
                var wh = st.Hwnd;
                if (!st.Embedded && wh != 0 && IsWindow(wh) && ZCovered(wh))
                {
                    ArmZBurst();
                }
                bool enabled = AppState.Instance.ConfigSnapshot().HoverDevices;
                if (wh != 0 && IsWindow(wh))
                {
                    HoverPanel.Track(wh, enabled);
                }
            }
            else if (timer == TimerZBurst)
            {
                st.ZBurstLeft = st.ZBurstLeft == 0 ? 0 : st.ZBurstLeft - 1;
                if (st.ZBurstLeft == 0)
                {
                    KillTimer(st.MsgHwnd, TimerZBurst);
                }
                else
                {
                    // PlaceWidget ends in MoveOverlay → re-asserts TOPMOST.
                    PlaceWidget();
                }
            }
            else if (timer == TimerFade)
            {
                FadeTick();
            }
            return 0;
        }
        if (msg == WmAppLayout)
        {
            // UIA structure change on the taskbar (coalesced).
            RequestLayout();
            return 0;
        }
        if (msg == WidgetThread.WmInvoke)
        {
            WidgetThread.HandleInvoke(lParam);
            return 0;
        }
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            HandleTaskbarCreated();
            return 0;
        }
        if (msg == WM_PAINT)
        {
            Paint(hwnd);
            return 0;
        }
        if (msg == WM_CLOSE)
        {
            // Graceful shutdown (an external WM_CLOSE used to fall through to
            // DefWindowProc's DestroyWindow, which now would only dispose the
            // display window and orphan the thread). Run the full protocol:
            // ghost-free last frame + teardown + quit.
            ExitWidget();
            return 0;
        }
        // Taskbar-Lyrics answers WM_NCHITTEST with HTTRANSPARENT so the
        // overlay never steals taskbar clicks; the tray icon carries the menu.
        if (msg == WM_NCHITTEST)
        {
            return (IntPtr)HTTRANSPARENT;
        }
        if (msg == WM_DESTROY)
        {
            if (hwnd == st.MsgHwnd)
            {
                // The anchor is going away: full thread teardown + quit.
                HoverPanel.Destroy();
                TrayIcon.Destroy();
                DestroyMemSurface(st);
                HistoryService.Close();
                PostQuitMessage(0);
                App.RequestExit();
                return 0;
            }
            if (hwnd == st.Hwnd && st.Teardown)
            {
                // Exit via the tray menu: final teardown; the anchor posts
                // quit as well when the process tears it down.
                HoverPanel.Destroy();
                TrayIcon.Destroy();
                DestroyMemSurface(st);
                HistoryService.Close();
                PostQuitMessage(0);
                App.RequestExit();
                return 0;
            }
            // Display window destroyed by a recreate (embed switch / band
            // swap): thread and anchor stay alive, state is carried over.
            return 0;
        }
        if (msg == WM_COMMAND)
        {
            HandleCommand(hwnd, unchecked((ushort)(long)wParam));
            return 0;
        }
        if (msg == TrayIcon.WmTray)
        {
            // Tray callbacks arrive as (msg=WM_TRAY, wparam=uid, lparam=event).
            // Right-click (up or down, some shells only send down) opens the
            // menu; double-click opens the battery history window (the shell
            // synthesizes the dblclk event itself — no CS_DBLCLKS needed).
            var evt = (uint)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_RBUTTONUP || evt == WM_RBUTTONDOWN)
            {
                ShowMenu(hwnd);
            }
            else if (evt == WM_LBUTTONDBLCLK)
            {
                App.ShowMainWindow(selectSettings: false);
            }
            return 0;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>`true` while Shell_TrayWnd sits ABOVE our overlay — the
    /// taskbar visually covers the widget (both are TOPMOST; later raise wins).</summary>
    private static bool ZCovered(IntPtr hwnd) => TaskbarLocator.TrayAbove(hwnd, _state.Tray);

    /// <summary>Arm the fast re-assert burst. Only start when not already
    /// running — a repeated SetTimer would RESET the countdown, and the 120ms
    /// hover check firing while covered would keep postponing the 150ms burst
    /// forever (burst starvation, seen in testing).</summary>
    private static void ArmZBurst()
    {
        var st = _state;
        if (st.LastCoveredLog is not { } last || last.Elapsed >= CoveredLogEvery)
        {
            Console.Error.WriteLine("razer-taskbar: taskbar covers the widget — re-asserting topmost");
            st.LastCoveredLog = Stopwatch.StartNew();
        }
        if (st.ZBurstLeft == 0)
        {
            st.ZBurstLeft = ZBurstTicks;
            SetTimer(st.MsgHwnd, TimerZBurst, ZBurstTickMs, 0);
        }
    }

    /// <summary>Coalesce UIA structure-change bursts: at most one placement
    /// pass per LAYOUT_DEBOUNCE, with a trailing pass on a one-shot timer so
    /// the last request always lands.</summary>
    private static void RequestLayout()
    {
        bool due;
        var st = _state;
        if (st.LastLayoutPass.Elapsed >= LayoutDebounce)
        {
            st.LastLayoutPass.Restart();
            due = true;
        }
        else
        {
            st.LayoutPending = true;
            due = false;
        }
        if (due)
        {
            PlaceWidget();
        }
        else
        {
            SetTimer(st.MsgHwnd, TimerLayout, LayoutDebounceMs, 0);
        }
    }

    private static void FlushPendingLayout()
    {
        var st = _state;
        bool run = st.LayoutPending;
        st.LayoutPending = false;
        if (run)
        {
            st.LastLayoutPass.Restart();
            PlaceWidget();
        }
    }

    /// <summary>Explorer (re)started: the cached taskbar HWND, UIA caches and
    /// the notification-area icon are all stale — rebind everything. In embed
    /// mode the display window was a band child and died together with the
    /// previous taskbar; recreate it on the new band.</summary>
    private static void HandleTaskbarCreated()
    {
        // Both the anchor and (in overlay mode) the display window receive
        // the broadcast — handle it once per broadcast.
        if (LastTaskbarRebind.ElapsedMilliseconds < 200)
        {
            return;
        }
        LastTaskbarRebind.Restart();
        Console.Error.WriteLine("razer-taskbar: TaskbarCreated — re-binding to taskbar");
        var st = _state;
        var tray = TaskbarLocator.FindShellTray();
        if (tray != 0)
        {
            st.Tray = tray;
        }
        TaskbarLocator.InvalidateWidgetsCache();
        st.LastLayout = null;
        if (st.Embedded && (st.Hwnd == 0 || !IsWindow(st.Hwnd)) && tray != 0)
        {
            if (RecreateWindow(true, tray))
            {
                Console.Error.WriteLine("razer-taskbar: band child re-created");
            }
        }
        if (AppState.Instance.ConfigSnapshot().ShowTrayIcon && st.Hwnd != 0 && IsWindow(st.Hwnd))
        {
            // A fresh taskbar wiped all tray icons: re-register (same
            // hWnd/uID → replace, never duplicate).
            TrayIcon.EnsureCreated(st.Hwnd);
        }
        UiaEvents.Rebind();
        PlaceWidget();
    }

    /// <summary>Two-row battery UI: row 1 = battery glyph + percentage, row 2
    /// = status icon (E823 clock / F607 bolt) + predicted time. The
    /// device-type icon stands alone, vertically centered across the widget.
    /// The frame is drawn over black and presented with per-pixel alpha.</summary>
    private static void Paint(IntPtr hwnd)
    {
        var st = _state;
        var cfg = AppState.Instance.ConfigSnapshot();
        var device = DisplayModeResolver.Pick(
            AppState.Instance.Devices.Snapshot(), cfg, AppState.Instance.ModeState,
            Environment.TickCount64);
        float scale = DpiScale(hwnd);
        int w = st.WidgetW;
        int h = st.WidgetH;
        int level = device?.BatteryPercentage ?? 0;
        bool charging = device?.IsCharging ?? false;
        bool saver = device?.BatterySaver ?? false;
        bool connected = device?.IsConnected ?? false;
        // Row 1: percentage (dim "--" with no device). Row 2: the bare
        // duration (the row's status icon conveys charging).
        string topLabel = connected ? $"{device!.BatteryPercentage}%" : "--";
        string? bottomLabel = null;
        if (cfg.ShowEstimatedTime && connected)
        {
            if (HistoryService.EstimateFor(device!.Handle) is { } est)
            {
                bottomLabel = HistoryService.FormatEstimatePlain(est);
            }
        }
        var sig = new PaintSig(topLabel, bottomLabel ?? "", level, charging, saver, connected, w, h);
        if (st.PaintedSig == sig)
        {
            // Nothing visually different; still validate the update region so
            // WM_PAINT does not loop.
            var hdc0 = BeginPaint(hwnd, out var ps0);
            if (hdc0 != 0)
            {
                EndPaint(hwnd, ref ps0);
            }
            return;
        }
        if (st.PaintedSig is null)
        {
            Log.Info("first paint: device=" + (device is null
                ? "none"
                : $"{device.Name} {device.BatteryPercentage}% connected={device.IsConnected}")
                + $", store={AppState.Instance.Devices.Snapshot().Count}");
        }
        var hdc = BeginPaint(hwnd, out var ps);
        if (hdc == 0)
        {
            return;
        }
        try
        {
            var handle = device?.Handle ?? "";
            // Offscreen 32bpp DIB render, then premultiplied-alpha upload.
            // Falls back to direct window drawing if the surface failed. The
            // embedded band child presents through the same ULW pipeline —
            // AlphaPresent pokes a band rebuild so the frame goes live.
            EnsureMemSurface(st, hwnd, w, h);
            // Cross-fade on a displayed-device change: capture the
            // on-screen (already premultiplied) frame BEFORE PaintBody
            // overwrites it. Starting a new fade mid-animation blends
            // from the current visual — MemBits holds the last blend.
            bool fade = cfg.FadeTransition
                && st.OnScreenHandle is not null
                && handle != st.OnScreenHandle
                && st.MemDc != 0 && st.MemW == w && st.MemH == h;
            if (fade)
            {
                st.FadePrev = SnapshotFrame(st);
            }
            var target = st.MemDc != 0 ? st.MemDc : hdc;
            PaintBody(hwnd, st, target, sig, topLabel, device, scale, w, h, connected, level, charging, saver, bottomLabel);
            if (st.MemDc != 0)
            {
                if (fade)
                {
                    Premultiply(st);
                    st.FadeNew = SnapshotFrame(st);
                    st.FadeStart = Environment.TickCount64;
                    SetTimer(st.MsgHwnd, TimerFade, FadeTickMs, 0);
                }
                else
                {
                    AlphaPresent(st, hwnd);
                }
            }
            st.OnScreenHandle = handle;
        }
        catch (Exception e)
        {
            Log.Error("paint failed", e);
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    /// <summary>Offscreen 32bpp top-down DIB matching the widget size;
    /// recreated only when the size changes. Widget-thread only.</summary>
    private static void EnsureMemSurface(WidgetState st, IntPtr hwnd, int w, int h)
    {
        if (st.MemDc != 0 && st.MemW == w && st.MemH == h)
        {
            return;
        }
        DestroyMemSurface(st);
        var wdc = GetDC(hwnd);
        if (wdc == 0)
        {
            return;
        }
        st.MemDc = CreateCompatibleDC(wdc);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        st.MemBmp = CreateDIBSection(wdc, ref bmi, DIB_RGB_COLORS, out st.MemBits, 0, 0);
        ReleaseDC(hwnd, wdc);
        if (st.MemDc == 0 || st.MemBmp == 0 || st.MemBits == 0)
        {
            DestroyMemSurface(st);
            return;
        }
        SelectObject(st.MemDc, st.MemBmp);
        st.MemW = w;
        st.MemH = h;
    }

    private static void DestroyMemSurface(WidgetState st)
    {
        if (st.MemDc != 0)
        {
            DeleteDC(st.MemDc);
            st.MemDc = 0;
        }
        if (st.MemBmp != 0)
        {
            DeleteObject(st.MemBmp);
            st.MemBmp = 0;
        }
        st.MemBits = 0;
        st.MemW = 0;
        st.MemH = 0;
    }

    /// <summary>Convert the GDI render into premultiplied ARGB (in place):
    /// the frame is drawn with solid colors over black, so each stored pixel
    /// is color*coverage and the coverage (== premultiplied alpha) is
    /// max(r,g,b). White text, gray icons and the tinted battery glyph all
    /// then blend against the REAL backdrop instead of the old black key;
    /// untouched black pixels become fully transparent.</summary>
    private static void Premultiply(WidgetState st)
    {
        int bytes = st.MemW * st.MemH * 4;
        var buf = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(st.MemBits, buf, 0, bytes);
        for (int i = 0; i + 3 < bytes; i += 4)
        {
            byte b = buf[i], g = buf[i + 1], r = buf[i + 2];
            buf[i + 3] = Math.Max(b, Math.Max(g, r));
        }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, st.MemBits, bytes);
    }

    private static byte[] SnapshotFrame(WidgetState st)
    {
        int bytes = st.MemW * st.MemH * 4;
        var buf = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(st.MemBits, buf, 0, bytes);
        return buf;
    }

    private static void WriteFrame(WidgetState st, byte[] frame)
        => System.Runtime.InteropServices.Marshal.Copy(frame, 0, st.MemBits, frame.Length);

    /// <summary>Linear per-byte blend of two premultiplied frames into the
    /// DIB — the mathematically correct cross-fade for this representation
    /// (lerp of premultiplied values == premultiplied lerp). AlphaPresent
    /// re-derives the alpha channel from the blended RGB.</summary>
    private static void WriteBlend(WidgetState st, byte[] from, byte[] to, double t)
    {
        int bytes = st.MemW * st.MemH * 4;
        var buf = new byte[bytes];
        System.Runtime.InteropServices.Marshal.Copy(st.MemBits, buf, 0, bytes);
        for (int i = 0; i < bytes; i++)
        {
            buf[i] = (byte)Math.Round(from[i] + (to[i] - from[i]) * t);
        }
        System.Runtime.InteropServices.Marshal.Copy(buf, 0, st.MemBits, bytes);
    }

    /// <summary>Animation tick: ease-blend prev→new and re-present. Widget
    /// thread only; a mid-fade device change simply re-arms from the current
    /// blend (Paint snapshots MemBits as the new FadePrev).</summary>
    private static void FadeTick()
    {
        var st = _state;
        if (st.FadePrev is null || st.FadeNew is null
            || st.MemBits == 0 || st.MemW == 0
            || st.Hwnd == 0 || !IsWindow(st.Hwnd))
        {
            EndFade(st);
            return;
        }
        double t = (Environment.TickCount64 - st.FadeStart) / (double)FadeMs;
        if (t >= 1.0)
        {
            WriteFrame(st, st.FadeNew);
            EndFade(st);
        }
        else
        {
            t = t * t * (3.0 - 2.0 * t); // smoothstep ease-in-out
            WriteBlend(st, st.FadePrev, st.FadeNew, t);
        }
        AlphaPresent(st, st.Hwnd);
    }

    private static void EndFade(WidgetState st)
    {
        st.FadePrev = null;
        st.FadeNew = null;
        st.FadeStart = 0;
        if (st.MsgHwnd != 0)
        {
            KillTimer(st.MsgHwnd, TimerFade);
        }
    }

    /// <summary>Stop any running cross-fade (transition toggled off) keeping
    /// the currently presented frame. Widget thread via WidgetThread.Post.</summary>
    public static void CancelFade()
    {
        EndFade(_state);
    }

    /// <summary>Hand the current DIB frame to DWM via UpdateLayeredWindow.</summary>
    private static void AlphaPresent(WidgetState st, IntPtr hwnd)
    {
        Premultiply(st);

        var dst = new POINT();
        if (TaskbarLocator.WindowRect(hwnd) is { } wr)
        {
            dst.X = wr.Left;
            dst.Y = wr.Top;
        }
        var size = new SIZE(st.MemW, st.MemH);
        var src = new POINT();
        var blend = new BLENDFUNCTION
        {
            BlendOp = 0, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        if (!UpdateLayeredWindow(hwnd, 0, ref dst, ref size, st.MemDc, ref src, 0, ref blend, ULW_ALPHA))
        {
            // Log at most the first failure; a silent blank widget is worse.
            if (!st.UlwFailedLogged)
            {
                st.UlwFailedLogged = true;
                Console.Error.WriteLine($"razer-taskbar: UpdateLayeredWindow failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            }
            return;
        }
        if (st.Embedded)
        {
            // Band children present through the same ULW pipeline as the
            // overlay, but the band only re-snapshots them on band rebuilds —
            // poke one so the fresh frame actually shows up.
            PokeBandRebuild();
        }
    }

    private static void PaintBody(IntPtr hwnd, WidgetState st, IntPtr hdc, PaintSig sig, string topLabel,
        RazerDevice? device, float scale, int w, int h,
        bool connected, int level, bool charging, bool saver, string? bottomLabel)
    {
        try
        {
            // The color key is gone: the frame is drawn over black and its
            // coverage becomes per-pixel alpha in AlphaPresent, so this fill
            // only clears the previous frame.
            var keyBrush = CreateSolidBrush(0x00000000);
            var keyRect = new RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            FillRect(hdc, ref keyRect, keyBrush);
            DeleteObject(keyBrush);
            SetBkMode(hdc, TRANSPARENT);

            // Palette: white glyph/text, dim gray secondary row.
            const uint fgColor = 0x00FF_FFFF;
            const uint dim = 0x00B0B0B0;

            // Native Win11 glyphs: Segoe Fluent Icons battery; icon fonts snap
            // to Microsoft's magic pixel sizes for crisp rendering.
            int iconH = DeviceIcons.SnapSize((int)MathF.Round(20.0f * scale));
            var hiconFont = DeviceIcons.IconFont(iconH);
            var hfont = GdiText.CachedTextFont((int)MathF.Round(12.0f * scale), FW_SEMIBOLD);
            SelectObject(hdc, hfont);

            // Rainbow option gates only the plain-discharge level gradient;
            // the charging/saver/disconnected state colors always show.
            bool colorize = AppState.Instance.ConfigSnapshot().ColorBatteryIcon;
            var fill = BatteryColors.LevelFillColor(level, connected, saver, charging, colorize);
            uint textColor = connected ? fgColor : dim;
            var wide = topLabel.ToCharArray();
            // Measure text first so the glyph+text group can be centered.
            var measure = new RECT();
            DrawTextW(hdc, wide, wide.Length, ref measure, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
            int textW = Math.Max(measure.Right - measure.Left, 1);
            // Two-layer battery icon: layer 1 is the ACTIVE series' level
            // glyph (charging bolt EBAB- / saver leaf EBB6-) tinted with
            // the state color; layer 2 — the same series' 0% glyph drawn on
            // top in the default color — masks the tinted outline and
            // symbol, so only the fill is colored. Layer 1 must be the same
            // series as the overlay: the status glyphs cut their outline
            // where the bolt/leaf crosses it, and a normal-series outline
            // underneath would peek through those gaps. Every connected
            // device renders two-layer (plain discharge overlays the plain
            // EBA0 outline, or a high charge would read as a solid colored
            // blob); disconnected draws the gray level glyph only.
            var iconCh = new[] { BatteryGlyphs.LevelGlyph(level, connected, saver, charging) };
            var iconMeasure = new RECT();
            SelectObject(hdc, hiconFont);
            DrawTextW(hdc, iconCh, iconCh.Length, ref iconMeasure, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
            int iconW = Math.Max(iconMeasure.Right - iconMeasure.Left, 1);
            SelectObject(hdc, hfont);
            // Row split: two stacked half-height rows while a prediction
            // shows, otherwise row 1 spans the full height.
            bool twoRows = bottomLabel is not null;
            var topRect = new RECT { Left = 0, Top = 0, Right = w, Bottom = twoRows ? h / 2 : h };

            // Row-2 content, measured up front: [status icon] [gap] [time].
            var estCh = Array.Empty<char>();
            var estWide = Array.Empty<char>();
            int estIconW = 0;
            int estW = 0;
            var estIconFont = DeviceIcons.IconFont(DeviceIcons.SnapSize((int)MathF.Round(14.0f * scale)));
            var estFont = GdiText.CachedTextFont((int)MathF.Round(12.0f * scale), FW_NORMAL);
            if (bottomLabel is { } estText)
            {
                estCh = new[] { charging ? '\uF607' : '\uE823' };
                estWide = estText.ToCharArray();
                SelectObject(hdc, estIconFont);
                var m = new RECT();
                DrawTextW(hdc, estCh, estCh.Length, ref m, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
                estIconW = Math.Max(m.Right - m.Left, 1);
                SelectObject(hdc, estFont);
                var m2 = new RECT();
                DrawTextW(hdc, estWide, estWide.Length, ref m2, DT_SINGLELINE | DT_CALCRECT | DT_LEFT);
                estW = Math.Max(m2.Right - m2.Left, 1);
                SelectObject(hdc, hfont);
            }

            // Geometry: two rows share an icon column (battery glyph and the
            // smaller status icon center on one vertical axis, both texts
            // start at the same x). The type icon stands alone on the left,
            // vertically centered across the FULL height. Omitted while no
            // device is shown ("--").
            int gap = (int)MathF.Round(5.0f * scale);
            var kind = device?.Kind;
            int kindH = (int)MathF.Round(14.0f * scale);
            int kindW = kind is { } k ? DeviceIcons.WidthFor(hdc, kindH, k) : 0;
            int kindGap = kind is not null ? gap : 0;
            int groupX, iconX, textX, estIconX, estTextX;
            if (twoRows)
            {
                int iconColW = Math.Max(iconW, estIconW);
                int row1W = iconColW + gap + textW;
                int row2W = iconColW + gap + estW;
                int groupW = kindW + kindGap + Math.Max(row1W, row2W);
                int gx = (w - groupW) / 2;
                int cx = gx + kindW + kindGap;
                int tx = cx + iconColW + gap;
                groupX = gx;
                iconX = cx + ((iconColW - iconW) / 2);
                textX = tx;
                estIconX = cx + ((iconColW - estIconW) / 2);
                estTextX = tx;
            }
            else
            {
                int groupW = kindW + kindGap + iconW + gap + textW;
                int gx = (w - groupW) / 2;
                int ix = gx + kindW + kindGap;
                groupX = gx;
                iconX = ix;
                textX = ix + iconW + gap;
                estIconX = 0;
                estTextX = 0;
            }
            if (kind is { } kindValue)
            {
                var kc = connected ? ((byte)0xFF, (byte)0xFF, (byte)0xFF) : ((byte)0x80, (byte)0x80, (byte)0x80);
                int kindY = (h - kindH) / 2;
                DeviceIcons.Draw(hdc, groupX, kindY, kindH, kindValue, kc);
            }
            // Battery glyph, two layers: the level glyph,
            // then the status overlay in the default (foreground) color on
            // top. Both share the LEVEL glyph's ink centering — the
            // bolt/leaf glyph's own ink box is taller (the symbol pokes
            // above the outline), and self-centering each layer would
            // visibly misalign the coinciding outlines. Layer 1 takes the
            // state/level color — the charging/saver/disconnected state
            // colors always, the plain-discharge level gradient only when
            // Settings → Colored battery icon is on (off: default white
            // glyph; see LevelFillColor).
            SelectObject(hdc, hiconFont);
            int iconDy = GdiText.InkCenterDelta(hdc, iconCh);
            uint iconColor = GdiText.ColorRef(fill.R, fill.G, fill.B);
            GdiText.DrawInkText(hdc, hiconFont, iconCh, iconX, topRect, iconColor, iconDy);
            if (BatteryGlyphs.StatusOverlayGlyph(connected, saver, charging) is { } statusGlyph)
            {
                GdiText.DrawInkText(hdc, hiconFont, new[] { statusGlyph }, iconX, topRect, fgColor, iconDy);
            }
            GdiText.DrawInkText(hdc, hfont, wide, textX, topRect, textColor);

            // Row 2: the prediction with its status icon, dimmed while
            // discharging (white while charging).
            if (twoRows)
            {
                var estRect = new RECT { Left = 0, Top = h / 2, Right = w, Bottom = h };
                uint estColor = charging ? fgColor : dim;
                GdiText.DrawInkText(hdc, estIconFont, estCh, estIconX, estRect, estColor);
                GdiText.DrawInkText(hdc, estFont, estWide, estTextX, estRect, estColor);
            }
            st.PaintedSig = sig;
        }
        catch (Exception e)
        {
            Log.Error("paint body failed", e);
        }
    }

    private static void AppendItem(IntPtr menu, uint flags, ushort id, string text)
    {
        AppendMenuW(menu, flags, id, I18n.Tr(text));
    }

    private static void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        // Device radio items: the only thing worth keeping in the menu
        // (quick switching); every other option lives in the Settings page.
        var devices = AppState.Instance.Devices.Snapshot();
        var connected = devices.Values.Where(d => d.IsConnected).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var connectedNames = connected.Select(d => d.Name).ToList();
        uint allChecked = AppState.Instance.ConfigSnapshot().ShownDeviceHandle.Length == 0 ? MF_CHECKED : 0;
        AppendItem(menu, MF_STRING | allChecked, IdDeviceBase, "Lowest battery device");
        for (int i = 0; i < connected.Count; i++)
        {
            var d = connected[i];
            uint checkedFlag = AppState.Instance.ConfigSnapshot().ShownDeviceHandle == d.Handle ? MF_CHECKED : 0;
            string label = $"{DeviceLabels.Label(d.Name, d.Handle, connectedNames)} — {d.BatteryPercentage}%{(d.IsCharging ? " ⚡" : "")}";
            AppendMenuW(menu, MF_STRING | checkedFlag, (IntPtr)(IdDeviceBase + 1 + i), label);
        }
        AppendMenuW(menu, MF_SEPARATOR, 0, null);

        uint uiFlags = App.XamlAvailable ? MF_STRING : MF_STRING | MF_GRAYED;
        AppendItem(menu, uiFlags, IdSettings, "Settings");
        AppendItem(menu, uiFlags, IdHistoryView, "Battery history");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendItem(menu, MF_STRING, IdExit, "Exit");

        GetCursorPos(out var cursor);
        SetForegroundWindow(hwnd);
        TrackPopupMenu(menu, TPM_LEFTALIGN | TPM_RIGHTBUTTON, cursor.X, cursor.Y, 0, hwnd, 0);
        DestroyMenu(menu);
    }

    private static void HandleCommand(IntPtr hwnd, ushort id)
    {
        var st = _state;
        switch (id)
        {
            case IdExit:
                // Real exit: leave an invisible last frame on the band (the
                // destroy-snapshot would otherwise burn the widget in) and
                // mark teardown so WM_DESTROY quits instead of waiting for a
                // recreate.
                if (st.Embedded)
                {
                    SilentFrame(st);
                }
                st.Teardown = true;
                DestroyWindow(hwnd);
                break;
            case IdSettings:
                App.ShowMainWindow(selectSettings: true);
                break;
            case IdHistoryView:
                App.ShowMainWindow(selectSettings: false);
                break;
            default:
                if (id >= IdDeviceBase)
                {
                    int idx = id - IdDeviceBase;
                    string handle = "";
                    if (idx != 0)
                    {
                        var devices = AppState.Instance.Devices.Snapshot();
                        var connected = devices.Values
                            .Where(d => d.IsConnected)
                            .OrderBy(d => d.Name, StringComparer.Ordinal)
                            .ToList();
                        handle = idx - 1 < connected.Count ? connected[idx - 1].Handle : "";
                    }
                    AppState.Instance.SetShownDevice(handle);
                }
                break;
        }
    }
}
