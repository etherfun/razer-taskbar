//! Headless regression test for the hand-written D2D1/DWrite interop
//! (linked from src/RazerTaskbar/Native/Interop). Runs the full hover-panel
//! pipeline — factory → DC render target → BindDC → 50% translucent rounded
//! card + border + DWrite text → EndDraw — against a memory DIB and asserts
//! premultiplied pixels. Guards the vtable slot order: two composition bugs
//! surfaced during bring-up exactly here (ID2D1Resource.GetFactory declared
//! as an empty interface shifted every slot; CLR inherited-interface
//! composition mis-slotted BindDC even with complete bases — hence the flat
//! interop style).

using System.Runtime.InteropServices;
using RazerTaskbar.Native.Interop;
using Xunit;
using static RazerTaskbar.Native.Interop.D2D1;
using static RazerTaskbar.Native.Interop.D2D1Consts;
using static RazerTaskbar.Native.Interop.DWriteConsts;

namespace RazerTaskbar.Tests;

public class D2dSmokeTests
{
    private const int W = 220, H = 64;

    [Fact]
    public void DcRenderTarget_Pipeline_RendersCardTextAndAlpha()
    {
        // 32bpp top-down DIB section — the same surface type HoverPanel paints.
        var wdc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(wdc);
        var bmi = new BITMAPINFO();
        bmi.BmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.BmiHeader.biWidth = W;
        bmi.BmiHeader.biHeight = -H;
        bmi.BmiHeader.biPlanes = 1;
        bmi.BmiHeader.biBitCount = 32;
        var bmp = CreateDIBSection(wdc, ref bmi, DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, wdc);
        Assert.True(memDc != 0 && bmp != 0 && bits != 0);
        SelectObject(memDc, bmp);

        var d2d = D2D1.CreateFactory();
        var dwrite = DWrite.CreateFactory();

        var props = new D2D1_RENDER_TARGET_PROPERTIES
        {
            Type = RenderTargetTypeDefault,
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = DxiFormatB8G8R8A8Unorm,
                AlphaMode = AlphaModePremultiplied,
            },
            DpiX = 96f,
            DpiY = 96f,
        };
        d2d.CreateDCRenderTarget(ref props, out var rt);

        var full = new RECT(0, 0, W, H);
        rt.BindDC(memDc, ref full);
        var white = new D2D1_COLOR_F(1f, 1f, 1f, 1f);
        rt.CreateSolidColorBrush(ref white, IntPtr.Zero, out var brush);

        rt.BeginDraw();
        var transparent = new D2D1_COLOR_F(0f, 0f, 0f, 0f);
        rt.Clear(ref transparent);
        rt.SetAntialiasMode(AntialiasModePerPrimitive);
        rt.SetTextAntialiasMode(TextAntialiasModeGrayscale);

        var rect = new D2D1_RECT_F(0f, 0f, W, H);
        var cardColor = D2D1.Color(0x20, 0x20, 0x20, 0.5f);
        brush.SetColor(ref cardColor);
        rt.FillRoundedRectangle(new D2D1_ROUNDED_RECT { Rect = rect, RadiusX = 8f, RadiusY = 8f }, brush);
        var inset = new D2D1_RECT_F(0.5f, 0.5f, W - 0.5f, H - 0.5f);
        var borderColor = D2D1.Color(0x5A, 0x5A, 0x5A, 1f);
        brush.SetColor(ref borderColor);
        rt.DrawRoundedRectangle(new D2D1_ROUNDED_RECT { Rect = inset, RadiusX = 8f, RadiusY = 8f }, brush, 1f, IntPtr.Zero);

        dwrite.CreateTextFormat("Segoe UI", IntPtr.Zero, FontWeightSemiBold, FontStyleNormal,
            FontStretchNormal, 14, "en-US", out var fmt);
        fmt.SetTextAlignment(TextAlignmentCenter);
        fmt.SetParagraphAlignment(ParagraphAlignmentCenter);
        fmt.SetWordWrapping(WordWrappingNoWrap);
        dwrite.CreateEllipsisTrimmingSign(fmt, out var sign);
        var trimming = new DWRITE_TRIMMING { Granularity = TrimmingGranularityCharacter };
        fmt.SetTrimming(ref trimming, sign);
        dwrite.CreateTextLayout("measure me", 10, fmt, 10000f, 10000f, out var layout);
        layout.GetMetrics(out var metrics);

        var layoutRect = new D2D1_RECT_F(0f, 0f, W, H);
        rt.DrawText("80% Battery", 11, fmt, ref layoutRect, brush, DrawTextOptionsNone, MeasuringModeNatural);

        int hr = rt.EndDraw(out _, out _);

        // Corner (outside the rounded card and its stroke) fully transparent;
        // bare-card pixel exactly 50% premultiplied dark; text ink opaque.
        var corner = Read(bits, 0, 0);
        var card = Read(bits, 20, H / 2);
        int ink = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (Read(bits, x, y)[3] > 0xE0)
                {
                    ink++;
                }
            }
        }

        Assert.Equal(0, hr);
        Assert.Equal(0, corner[3]);
        Assert.InRange(card[3], 126, 130);
        Assert.Equal(16, card[0]);
        Assert.True(ink > 200, $"expected opaque ink pixels, got {ink}");
        Assert.True(metrics.WidthIncludingTrailingWhitespace > 20f,
            $"implausible text metrics: {metrics.WidthIncludingTrailingWhitespace}");
    }

    private static byte[] Read(IntPtr bits, int x, int y)
    {
        var b = new byte[4];
        Marshal.Copy(new IntPtr(bits.ToInt64() + (y * W + x) * 4), b, 0, 4);
        return b;
    }

    private const uint DIB_RGB_COLORS = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hdc, IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER BmiHeader;
    }
}
