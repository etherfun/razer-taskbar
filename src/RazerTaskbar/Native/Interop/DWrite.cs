//! Hand-written DirectWrite COM interop — text formats, text layouts for
//! ink measurement and ellipsis trimming signs, the minimal surface the
//! hover panel needs. IIDs and vtable orders verified against the Windows
//! SDK 10.0.26100 dwrite.h.
//!
//! Deliberately FLAT declarations (see D2D1.cs): the CLR's vtable
//! composition for [ComImport] interface inheritance proved unreliable
//! during bring-up, so IDWriteTextLayout lists the full
//! IUnknown → IDWriteTextFormat(25) → layout(34) chain explicitly.
//! Unused members are parameterless placeholders that keep slot indices
//! honest and are never called.

using System.Runtime.InteropServices;

namespace RazerTaskbar.Native.Interop;

public static class DWriteConsts
{
    public const uint FactoryTypeShared = 0;

    public const uint FontWeightNormal = 400;
    public const uint FontWeightSemiBold = 600;
    public const uint FontStyleNormal = 0;
    public const uint FontStretchNormal = 1;

    public const uint TextAlignmentLeading = 0;
    public const uint TextAlignmentTrailing = 1;
    public const uint TextAlignmentCenter = 2;

    public const uint ParagraphAlignmentCenter = 2; // NEAR=0, FAR=1, CENTER=2

    public const uint WordWrappingNoWrap = 1;

    public const uint TrimmingGranularityCharacter = 1;

    public const uint MeasuringModeNatural = 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct DWRITE_TRIMMING
{
    public uint Granularity;
    public uint Delimiter;
    public uint DelimiterCount;
}

[StructLayout(LayoutKind.Sequential)]
public struct DWRITE_TEXT_METRICS
{
    public float Left, Top, Width, WidthIncludingTrailingWhitespace;
    public float Height, LayoutWidth, LayoutHeight;
    public float MaxBidiReachingWidth, MaxBidiReachingHeight;
    public uint LineCount;
}

public static class DWrite
{
    public static IDWriteFactory CreateFactory()
    {
        var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"); // IDWriteFactory
        Marshal.ThrowExceptionForHR(DWriteCreateFactory(
            DWriteConsts.FactoryTypeShared, iid, out var f));
        return f;
    }

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(
        uint factoryType, Guid iid, out IDWriteFactory factory);
}

[ComImport]
[Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteFactory
{
    // IUnknown slots 0-2. Slots 1-12, 14-15, 17 unused placeholders.
    void GetSystemFontCollection(); void CreateCustomFontCollection();
    void RegisterFontCollectionLoader(); void UnregisterFontCollectionLoader();
    void CreateFontFileReference(); void CreateCustomFontFileReference(); void CreateFontFace();
    void CreateRenderingParams(); void CreateMonitorRenderingParams(); void CreateCustomRenderingParams();
    void RegisterFontFileLoader(); void UnregisterFontFileLoader();
    void CreateTextFormat(
        [MarshalAs(UnmanagedType.LPWStr)] string fontFamilyName, IntPtr fontCollection,
        uint fontWeight, uint fontStyle, uint fontStretch, float fontSize,
        [MarshalAs(UnmanagedType.LPWStr)] string localeName, out IDWriteTextFormat textFormat); // slot 13
    void CreateTypography();
    void GetGdiInterop();
    void CreateTextLayout(
        [MarshalAs(UnmanagedType.LPWStr)] string str, uint length, IDWriteTextFormat format,
        float maxWidth, float maxHeight, out IDWriteTextLayout textLayout); // slot 16
    void CreateGdiCompatibleTextLayout();
    void CreateEllipsisTrimmingSign(IDWriteTextFormat format, out IDWriteInlineObject trimmingSign); // slot 18
    // CreateTextAnalyzer, CreateNumberSubstitution, CreateGlyphRunAnalysis omitted
}

[ComImport]
[Guid("9c906818-31d7-4fd3-a151-7c5e225db55a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteTextFormat
{
    // IUnknown slots 0-2. Standalone interface — truncation after the last
    // used member is safe here (nothing inherits it in managed code).
    void SetTextAlignment(uint textAlignment); // slot 1
    void SetParagraphAlignment(uint paragraphAlignment); // slot 2
    void SetWordWrapping(uint wordWrapping); // slot 3
    void SetReadingDirection(); void SetFlowDirection(); void SetIncrementalTabStop();
    void SetTrimming(ref DWRITE_TRIMMING trimmingOptions, IDWriteInlineObject trimmingSign); // slot 7
    // GetTextAlignment ... GetLocaleName omitted
}

[ComImport]
[Guid("53737037-6d14-410b-9bfe-0b182bb70961")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteTextLayout
{
    // IUnknown slots 0-2, then IDWriteTextFormat's 25 members, then the 34
    // layout-specific members — full flat chain, no managed inheritance.

    void SetTextAlignment(uint textAlignment); // format member 1
    void SetParagraphAlignment(uint paragraphAlignment); // format member 2
    void SetWordWrapping(uint wordWrapping); // format member 3
    void SetReadingDirection(); void SetFlowDirection(); void SetIncrementalTabStop();
    void SetTrimming(ref DWRITE_TRIMMING trimmingOptions, IDWriteInlineObject trimmingSign); // format member 7
    void SetLineSpacing(); // format member 8
    void GetTextAlignment(); void GetParagraphAlignment(); void GetWordWrapping();
    void GetReadingDirection(); void GetFlowDirection(); void GetIncrementalTabStop();
    void GetTrimming(); void GetLineSpacing(); void GetFontCollection();
    void GetFontFamilyNameLength(); void GetFontFamilyName(); void GetFontWeight();
    void GetFontStyle(); void GetFontStretch(); void GetFontSize();
    void GetLocaleNameLength(); void GetLocaleName(); // format member 25

    // Layout members 1-32 unused placeholders (slot-numbered: the flat chain
    // makes several names collide with the format region above).
    void L01(); void L02(); void L03(); void L04(); void L05(); void L06();
    void L07(); void L08(); void L09(); void L10(); void L11(); void L12();
    void L13(); void L14(); void L15(); void L16(); void L17(); void L18();
    void L19(); void L20(); void L21(); void L22(); void L23(); void L24();
    void L25(); void L26(); void L27(); void L28(); void L29(); void L30();
    void L31(); void L32();
    void GetMetrics(out DWRITE_TEXT_METRICS textMetrics); // layout member 33
    // GetOverhangMetrics and later members omitted: GetOverhangMetrics
    // mis-slotted against the runtime DLL during bring-up (GetMetrics is
    // verified correct), and the panel only needs advance widths anyway.
}

[ComImport]
[Guid("8339FDE3-106F-47ab-8373-1C6295EB10B3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDWriteInlineObject
{
    // Only passed around; its methods (Draw, GetMetrics, ...) are never called.
}
