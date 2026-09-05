//! Port of main.rs find_existing_instance: find the running widget by its
//! window class (top-level) or among Shell_TrayWnd children (embed mode).

using System.Text;
using static RazerTaskbar.Native.Interop.User32;

namespace RazerTaskbar.Native;

public static class SingleInstance
{
    public const string WidgetClass = "RazerTaskbarWidget";
    public const string WidgetTitle = "RazerTaskbarWidget";

    /// <summary>Returns the HWND of an already running instance, or 0.</summary>
    public static IntPtr FindExisting()
    {
        var top = FindWindowW(WidgetClass, WidgetTitle);
        if (top != 0)
        {
            return top;
        }

        // Embed mode: the widget is a child of the taskbar, invisible to
        // FindWindow as a top-level window.
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0)
        {
            return 0;
        }

        IntPtr found = 0;
        EnumChildWindows(tray, (child, _) =>
        {
            var sb = new StringBuilder(64);
            if (GetClassNameW(child, sb, sb.Capacity) > 0 && sb.ToString() == WidgetClass)
            {
                found = child;
                return false; // stop enumeration
            }
            return true;
        }, 0);
        return found;
    }

    public static bool AnotherInstanceRunning() => FindExisting() != 0;
}
