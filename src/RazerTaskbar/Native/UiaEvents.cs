//! Port of src/uia_events.rs: UIA structure-change listener for event-driven
//! taskbar re-layout (Taskbar-Lyrics port). A background MTA thread registers
//! an IUIAutomationStructureChangedEventHandler on the Win11 taskbar's XAML
//! input site (`Windows.UI.Input.InputSite.WindowClass` under Shell_TrayWnd,
//! TreeScope_Descendants). The handler does nothing but PostMessage — no
//! locks, no shared state. The thread re-registers when the taskbar HWND
//! changes (explorer restart: signalled via the rebind signal, or
//! self-detected by the 30s idle check).

using System.Runtime.InteropServices;
using RazerTaskbar.Core;
using static RazerTaskbar.Native.Interop.User32;
using static RazerTaskbar.Native.Interop.Win32Consts;

namespace RazerTaskbar.Native;

public static class UiaEvents
{
    private static readonly SemaphoreSlim RebindSignal = new(0);

    /// <summary>Window that receives the layout notification. Mutable: the
    /// display window is destroyed and recreated on embed-mode switches, so
    /// Rebind() re-registers against the current value.</summary>
    private static IntPtr _target;
    private static uint _msg;

    /// <summary>Spawn the listener thread. `target` receives `msg` on every
    /// taskbar structure change. Signal Rebind() whenever TaskbarCreated is
    /// observed.</summary>
    public static void Spawn(IntPtr target, uint msg)
    {
        _target = target;
        _msg = msg;
        var thread = new Thread(() => RunLoop())
        {
            Name = "razer-uia-events",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    /// <summary>Retarget layout notifications to a new display window and
    /// re-register the handler (embed-mode window recreation).</summary>
    public static void Retarget(IntPtr target)
    {
        _target = target;
        Rebind();
    }

    /// <summary>Wake the listener so it re-checks the taskbar HWND and
    /// re-registers (called from the TaskbarCreated handler).</summary>
    public static void Rebind()
    {
        try
        {
            RebindSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; one wake is enough.
        }
    }

    /// <summary>One live registration. Keeping the UIA + element + handler
    /// references lets the next rebind release the old handler.</summary>
    private sealed class Registration
    {
        public required IntPtr Tray;
        public required IntPtr Target;
        public required Interop.IUIAutomation Uia;
        public required Interop.IUIAutomationElement Element;
        public required Interop.IUIAutomationStructureChangedEventHandler Handler;

        public void Teardown()
        {
            // A dead taskbar element makes removal fail — harmless, ignore it.
            try
            {
                Uia.RemoveStructureChangedEventHandler(Element, Handler);
            }
            catch (Exception)
            {
            }
        }
    }

    private static void RunLoop()
    {
        var hr = CoInitializeEx(0, CoinitMultithreaded);
        if (hr < 0)
        {
            // S_OK/S_FALSE (0/1) are both success: .NET's SetApartmentState
            // already initialized the MTA, which lands here as S_FALSE.
            Log.Error($"razer-taskbar: UIA listener CoInitializeEx failed: 0x{hr:X8}");
            return;
        }
        try
        {
            Registration? reg = null;
            while (true)
            {
                // Check/rebind FIRST, then wait: the first iteration registers
                // immediately instead of waiting out the 30s idle timeout.
                var tray = TaskbarLocator.FindShellTray();
                var target = _target;
                bool needRebind = (reg, tray) switch
                {
                    (null, 0) => false,
                    (null, _) => target != 0,
                    (_, 0) => false, // stale registration on a dead taskbar costs nothing
                    ({ } r, _) when r.Tray != tray || r.Target != target => true,
                    _ => false,
                };
                if (needRebind)
                {
                    reg?.Teardown();
                    reg = null;
                    if (tray != 0 && target != 0)
                    {
                        reg = Register(tray, target, _msg);
                    }
                }
                // Wake on rebind signal, or at least every 30s to notice the
                // taskbar HWND changing on its own.
                RebindSignal.Wait(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static Registration? Register(IntPtr tray, IntPtr target, uint msg)
    {
        try
        {
            var uia = Interop.UiaInterop.Create();
            if (uia is null)
            {
                Log.Error("razer-taskbar: UIA CoCreateInstance failed");
                return null;
            }
            uia.ElementFromHandle(tray, out var root);
            // Scope to the XAML input site when present (Win11): the taskbar's
            // live content lives under it. Fall back to the whole tray (Win10;
            // the 1s poll covers it there anyway). Bounded DFS with the
            // control-view walker, capped 200 nodes / 30 children.
            var element = FindInputSite(uia, root) ?? root;
            var handler = new StructureChangedHandler(target, msg);
            uia.AddStructureChangedEventHandler(element, TREE_SCOPE_DESCENDANTS, 0, handler);
            Log.Info("razer-taskbar: UIA structure listener registered");
            return new Registration { Tray = tray, Target = target, Uia = uia, Element = element, Handler = handler };
        }
        catch (Exception e)
        {
            Log.Error($"razer-taskbar: UIA registration failed: {e.Message}");
            return null;
        }
    }

    /// <summary>First `Windows.UI.Input.InputSite.WindowClass` descendant.</summary>
    private static Interop.IUIAutomationElement? FindInputSite(
        Interop.IUIAutomation uia,
        Interop.IUIAutomationElement root)
    {
        const string want = "Windows.UI.Input.InputSite.WindowClass";
        try
        {
            uia.GetControlViewWalker(out var walker);
            var stack = new List<Interop.IUIAutomationElement> { root };
            int guard = 0;
            while (stack.Count > 0)
            {
                var el = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                guard += 1;
                if (guard > 200)
                {
                    break;
                }
                try
                {
                    el.GetCurrentClassName(out var name);
                    if (name == want)
                    {
                        return el;
                    }
                    walker.GetFirstChildElement(el, out var child);
                    int n = 0;
                    while (n < 30)
                    {
                        stack.Add(child);
                        walker.GetNextSiblingElement(child, out child);
                        n += 1;
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    /// <summary>The COM handler runs on arbitrary UIA delivery threads; the
    /// target HWND is stored as a bare address and only used for PostMessageW,
    /// which is thread-safe. The CLR CCW keeps this agile.</summary>
    [ComVisible(true)]
    private sealed class StructureChangedHandler : Interop.IUIAutomationStructureChangedEventHandler
    {
        private readonly IntPtr _target;
        private readonly uint _msg;

        public StructureChangedHandler(IntPtr target, uint msg)
        {
            _target = target;
            _msg = msg;
        }

        public void HandleStructureChangedEvent(IntPtr sender, int changeType, IntPtr runtimeId)
        {
            PostMessageW(_target, _msg, 0, 0);
        }
    }

    private const uint CoinitMultithreaded = 0x0; // COINIT_MULTITHREADED

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}
