//! Win32 RECT mirror for the linked D2D1 interop sources (D2dSmokeTests);
//! the main exe declares its own — this one is 16 bytes, four ints, same.

using System.Runtime.InteropServices;

namespace RazerTaskbar.Native.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public RECT(int l, int t, int r, int b)
        {
            Left = l; Top = t; Right = r; Bottom = b;
        }
    }
}
