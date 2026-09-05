//! Hand-written UIAutomation COM interop — the minimal surface used by
//! taskbar.rs port (WidgetsButton rect) and uia_events.rs (structure-change
//! listener). IIDs and vtable orders verified against Microsoft's official
//! Win32 metadata (Microsoft.Windows.SDK.Win32Metadata), the same source the
//! Rust `windows` 0.58 crate generates from.
//!
//! Truncated interface declarations are valid: methods must be declared in
//! exact vtable order, everything after the last one used can be omitted.

using System.Runtime.InteropServices;

namespace RazerTaskbar.Native.Interop;

// CLSID_CUIAutomation (windows 0.58: CUIAutomation constant)
public static class UiaClsids
{
    internal static readonly Guid CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
}

[ComImport]
[Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    // IUnknown: QueryInterface, AddRef, Release (slots 0-2)

    [PreserveSig] int CompareElements(IntPtr element1, IntPtr element2, out int areSame);
    [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
    void GetRootElement(out IUIAutomationElement root);
    void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
    void ElementFromPoint(POINT pt, out IUIAutomationElement element);
    void GetFocusedElement(out IUIAutomationElement element);
    void GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement root);
    void ElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);
    void ElementFromPointBuildCache(POINT pt, IntPtr cacheRequest, out IUIAutomationElement element);
    void GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);
    void CreateTreeWalker(IntPtr condition, out IUIAutomationTreeWalker walker);
    void GetControlViewWalker(out IUIAutomationTreeWalker walker);
    void GetContentViewWalker(out IUIAutomationTreeWalker walker);
    void GetRawViewWalker(out IUIAutomationTreeWalker walker);
    void GetRawViewCondition(out IntPtr condition);
    void GetControlViewCondition(out IntPtr condition);
    void GetContentViewCondition(out IntPtr condition);
    void CreateCacheRequest(out IntPtr cacheRequest);
    void CreateTrueCondition(out IntPtr newCondition);
    void CreateFalseCondition(out IntPtr newCondition);
    // VARIANT marshaled as object
    void CreatePropertyCondition(uint propertyId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.IUnknown)] out object newCondition);
    void CreatePropertyConditionEx(uint propertyId, [MarshalAs(UnmanagedType.Struct)] object value, uint flags, [MarshalAs(UnmanagedType.IUnknown)] out object newCondition);
    void CreateAndCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
    void CreateAndConditionFromArray(IntPtr conditionArray, out IntPtr newCondition);
    void CreateAndConditionFromNativeArray(IntPtr[] conditionArray, int conditionCount, out IntPtr newCondition);
    void CreateOrCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
    void CreateOrConditionFromArray(IntPtr conditionArray, out IntPtr newCondition);
    void CreateOrConditionFromNativeArray(IntPtr[] conditionArray, int conditionCount, out IntPtr newCondition);
    void CreateNotCondition(IntPtr condition, out IntPtr newCondition);
    void AddAutomationEventHandler(IntPtr element, uint scope, uint eventId, IntPtr cacheRequest, IntPtr handler);
    void RemoveAutomationEventHandler(IntPtr element, uint eventId, IntPtr handler);
    void AddPropertyChangedEventHandlerNativeArray(IntPtr element, uint scope, IntPtr cacheRequest, IntPtr handler, uint[] propertyArray, int propertyCount);
    void AddPropertyChangedEventHandler(IntPtr element, uint scope, IntPtr cacheRequest, IntPtr handler, IntPtr propertyArray, int propertyCount);
    void RemovePropertyChangedEventHandler(IntPtr element, IntPtr handler);
    void AddStructureChangedEventHandler(IUIAutomationElement element, uint scope, IntPtr cacheRequest, IUIAutomationStructureChangedEventHandler handler);
    void RemoveStructureChangedEventHandler(IUIAutomationElement element, IUIAutomationStructureChangedEventHandler handler);
}

[ComImport]
[Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElement
{
    // IUnknown: QueryInterface, AddRef, Release (slots 0-2)

    [PreserveSig] int SetFocus();
    [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
    void FindFirst(uint scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IUIAutomationElement found);
    void FindAll(uint scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, out IntPtr found);
    void FindFirstBuildCache(uint scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, IntPtr cacheRequest, out IUIAutomationElement found);
    void FindAllBuildCache(uint scope, [MarshalAs(UnmanagedType.IUnknown)] object condition, IntPtr cacheRequest, out IntPtr found);
    void BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updated);
    void GetCurrentPropertyValue(uint propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCurrentPropertyValueEx(uint propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCachedPropertyValue(uint propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCachedPropertyValueEx(uint propertyId, int ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetCurrentPatternAs(uint patternId, ref Guid riid, out IntPtr patternObject);
    void GetCachedPatternAs(uint patternId, ref Guid riid, out IntPtr patternObject);
    void GetCurrentPattern(uint patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
    void GetCachedPattern(uint patternId, [MarshalAs(UnmanagedType.IUnknown)] out object patternObject);
    void GetCachedParent(out IUIAutomationElement parent);
    void GetCachedChildren(out IntPtr children);
    void GetCurrentProcessId(out int retVal);
    void GetCurrentControlType(out int retVal);
    void GetCurrentLocalizedControlType([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentName([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentAcceleratorKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentAccessKey([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentHasKeyboardFocus(out int retVal);
    void GetCurrentIsKeyboardFocusable(out int retVal);
    void GetCurrentIsEnabled(out int retVal);
    void GetCurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentClassName([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentHelpText([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentCulture(out int retVal);
    void GetCurrentIsControlElement(out int retVal);
    void GetCurrentIsContentElement(out int retVal);
    void GetCurrentIsPassword(out int retVal);
    void GetCurrentNativeWindowHandle(out IntPtr retVal);
    void GetCurrentItemType([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentIsOffscreen(out int retVal);
    void GetCurrentOrientation(out int retVal);
    void GetCurrentFrameworkId([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentIsRequiredForForm(out int retVal);
    void GetCurrentItemStatus([MarshalAs(UnmanagedType.BStr)] out string retVal);
    void GetCurrentBoundingRectangle(out UiaRect retVal);
    // … remaining getters unused; truncated.
}

[StructLayout(LayoutKind.Sequential)]
public struct UiaRect
{
    public double left;
    public double top;
    public double width;
    public double height;
}

[ComImport]
[Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationTreeWalker
{
    // IUnknown: QueryInterface, AddRef, Release (slots 0-2)

    void GetParentElement(IUIAutomationElement element, out IUIAutomationElement parent);
    void GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement first);
    void GetLastChildElement(IUIAutomationElement element, out IUIAutomationElement last);
    void GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement next);
    // … remaining methods unused; truncated.
}

[ComImport]
[Guid("E81D1B4E-11C5-42F8-9754-E7036C79F054")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationStructureChangedEventHandler
{
    // IUnknown: QueryInterface, AddRef, Release (slots 0-2)

    void HandleStructureChangedEvent(IntPtr sender, int changeType, IntPtr runtimeId);
}

public static class UiaInterop
{
    /// <summary>CoCreateInstance(CUIAutomation), or null when UIAutomation is
    /// unavailable. Uses the CLSID activation path; works on MTA threads.</summary>
    public static IUIAutomation? Create()
    {
        var type = Type.GetTypeFromCLSID(UiaClsids.CUIAutomation);
        if (type is null)
        {
            return null;
        }
        try
        {
            return (IUIAutomation)Activator.CreateInstance(type)!;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
