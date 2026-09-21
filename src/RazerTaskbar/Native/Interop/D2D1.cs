//! Hand-written Direct2D COM interop — the minimal surface the hover panel
//! needs to paint without GDI text: a DC render target bound to the panel's
//! 32bpp DIB section (premultiplied BGRA, 96dpi so 1 DIP == 1 px), one solid
//! brush, rounded-rect fill/stroke and DWrite-backed DrawText. IIDs and
//! vtable orders verified against the Windows SDK 10.0.26100 d2d1.h AND
//! empirically slot-swept at runtime (see the d2dsmoke headless test that
//! caught two composition bugs during bring-up).
//!
//! deliberately FLAT declarations: the CLR's vtable composition for
//! [ComImport] interface inheritance proved unreliable here (an inherited
//! ID2D1DCRenderTarget : ID2D1RenderTarget : ID2D1Resource mis-slotted
//! BindDC even with complete bases — derived GetDpi and flat GetDpi probes
//! disagreed at the same IID). Every interface lists the full chain
//! IUnknown → ID2D1Resource(GetFactory) → members explicitly.
//!
//! Unused members are parameterless placeholders that exist purely to keep
//! slot indices honest and are never called.

using System.Runtime.InteropServices;

namespace RazerTaskbar.Native.Interop;

public static class D2D1Consts
{
    public const uint FactoryTypeMultiThreaded = 1;

    public const uint RenderTargetTypeDefault = 0;
    public const uint AlphaModePremultiplied = 1;

    public const uint DxiFormatB8G8R8A8Unorm = 87;

    public const uint AntialiasModePerPrimitive = 0;
    public const uint TextAntialiasModeGrayscale = 2;

    public const uint DrawTextOptionsNone = 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_RECT_F
{
    public float Left, Top, Right, Bottom;

    public D2D1_RECT_F(float l, float t, float r, float b)
    {
        Left = l; Top = t; Right = r; Bottom = b;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_COLOR_F
{
    public float R, G, B, A;

    public D2D1_COLOR_F(float r, float g, float b, float a)
    {
        R = r; G = g; B = b; A = a;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_ROUNDED_RECT
{
    public D2D1_RECT_F Rect;
    public float RadiusX, RadiusY;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_PIXEL_FORMAT
{
    public uint Format; // DXGI_FORMAT
    public uint AlphaMode;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_RENDER_TARGET_PROPERTIES
{
    public uint Type;
    public D2D1_PIXEL_FORMAT PixelFormat;
    public float DpiX, DpiY;
    public uint Usage;
    public uint MinLevel;
}

public static class D2D1
{
    /// <summary>D2D1CreateFactory, MULTI_THREADED so one factory can be shared
    /// by every thread that paints (the factory itself is the sync point).</summary>
    public static ID2D1Factory CreateFactory()
    {
        var iid = new Guid("06152247-6f50-465a-9245-118bfd3b6007"); // ID2D1Factory
        Marshal.ThrowExceptionForHR(D2D1CreateFactory(
            D2D1Consts.FactoryTypeMultiThreaded, iid, IntPtr.Zero, out var f));
        return f;
    }

    /// <summary>0xAARRGGBB-style byte triple → D2D float color.</summary>
    public static D2D1_COLOR_F Color(byte r, byte g, byte b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(
        uint factoryType, Guid riid, IntPtr factoryOptions, out ID2D1Factory factory);
}

[ComImport]
[Guid("06152247-6f50-465a-9245-118bfd3b6007")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID2D1Factory
{
    // IUnknown slots 0-2. Members 1 and 3-13 unused placeholders.
    void ReloadSystemMetrics(); // member 1
    void GetDesktopDpi(out float dpiX, out float dpiY); // member 2
    void CreateRectangleGeometry(); void CreateRoundedRectangleGeometry(); void CreateEllipseGeometry();
    void CreateGeometryGroup(); void CreateTransformedGeometry(); void CreatePathGeometry();
    void CreateStrokeStyle(); void CreateDrawingStateBlock();
    void CreateWicBitmapRenderTarget(); void CreateHwndRenderTarget(); void CreateDxgiSurfaceRenderTarget();
    void CreateDCRenderTarget(ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, out ID2D1DCRenderTarget dcRenderTarget); // member 14
}

[ComImport]
[Guid("1c51bc64-de61-46fd-9899-63a5d8f03950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID2D1DCRenderTarget
{
    // IUnknown slots 0-2, then ID2D1Resource (1 member), then the 53
    // ID2D1RenderTarget members, then BindDC — full flat chain.

    void GetFactory(out ID2D1Factory factory); // Resource

    // Slots 1-4, 6-14, 17-43 unused placeholders keep the used slots honest.
    void CreateBitmap(); void CreateBitmapFromWicBitmap(); void CreateSharedBitmap(); void CreateBitmapBrush();
    void CreateSolidColorBrush(ref D2D1_COLOR_F color, IntPtr brushProperties, out ID2D1SolidColorBrush brush); // member 5
    void CreateGradientStopCollection(); void CreateLinearGradientBrush(); void CreateRadialGradientBrush();
    void CreateCompatibleRenderTarget(); void CreateLayer(); void CreateMesh();
    void DrawLine(); void DrawRectangle();
    void FillRectangle(ref D2D1_RECT_F rect, ID2D1SolidColorBrush brush); // member 14
    void DrawRoundedRectangle(ref D2D1_ROUNDED_RECT rect, ID2D1SolidColorBrush brush, float strokeWidth, IntPtr strokeStyle); // member 15
    void FillRoundedRectangle(ref D2D1_ROUNDED_RECT rect, ID2D1SolidColorBrush brush); // member 16
    void DrawEllipse(); void FillEllipse(); void DrawGeometry(); void FillGeometry();
    void FillMesh(); void FillOpacityMask(); void DrawBitmap();
    void DrawText([MarshalAs(UnmanagedType.LPWStr)] string str, uint length, IDWriteTextFormat format, ref D2D1_RECT_F layoutRect, ID2D1SolidColorBrush brush, uint options, uint measuringMode); // member 24
    void DrawTextLayout(); void DrawGlyphRun();
    void SetTransform(); void GetTransform(); // members 27-28
    void SetAntialiasMode(uint antialiasMode); // member 29
    void GetAntialiasMode();
    void SetTextAntialiasMode(uint textAntialiasMode); // member 31
    void GetTextAntialiasMode(); void SetTextRenderingParams(); void GetTextRenderingParams();
    void SetTags(); void GetTags(); void PushLayer(); void PopLayer(); void Flush();
    void SaveDrawingState(); void RestoreDrawingState(); void PushAxisAlignedClip(); void PopAxisAlignedClip();
    void Clear(ref D2D1_COLOR_F clearColor); // member 44
    void BeginDraw(); // member 45
    [PreserveSig] int EndDraw(out ulong tag1, out ulong tag2); // member 46
    void GetPixelFormat(); void SetDpi(float dpiX, float dpiY); void GetDpi(out float dpiX, out float dpiY);
    void GetSize(out D2D1_SIZE_F size); void GetPixelSize(out D2D1_SIZE_U size);
    [PreserveSig] uint GetMaximumBitmapSize();
    [PreserveSig] int IsSupported(ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties); // member 53

    void BindDC(IntPtr hdc, ref RECT subRect); // DCRT's own member
}

[ComImport]
[Guid("2cd906a9-12e2-11dc-9fed-001143a055f9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ID2D1SolidColorBrush
{
    // IUnknown slots 0-2, ID2D1Resource.GetFactory, ID2D1Brush's 4 members.
    void GetFactory(out ID2D1Factory factory);
    void SetOpacity(); void SetTransform(); void GetOpacity(); void GetTransform();
    void SetColor(ref D2D1_COLOR_F color);
    void GetColor(out D2D1_COLOR_F color);
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_SIZE_F
{
    public float Width, Height;
}

[StructLayout(LayoutKind.Sequential)]
public struct D2D1_SIZE_U
{
    public uint Width, Height;
}
