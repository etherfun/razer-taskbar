// Win32 P/Invoke for direct HID battery queries (hid.dll + setupapi +
// kernel32), hand-written in the style of the main project's Win32.cs.
// Windows-only. Callers funnel every failure through Log; none of these
// declarations throw on API failure (they return false / invalid handles).

using System.Runtime.InteropServices;
using System.Text;

namespace RazerTaskbar.Core.Interop;

internal static class HidApi
{
    public static readonly Guid GuidDevinterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public const uint DIGCF_PRESENT = 0x2;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    public const uint GENERIC_READ = 0x8000_0000;
    public const uint GENERIC_WRITE = 0x4000_0000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    // — hid.dll —

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    /// <summary>Returns HIDP_STATUS_SUCCESS (0x0011_0000) on success.</summary>
    [DllImport("hid.dll", SetLastError = true)]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetProductString(IntPtr hidDeviceObject, StringBuilder buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetSerialNumberString(IntPtr hidDeviceObject, StringBuilder buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    internal static extern bool HidD_GetInputReport(IntPtr hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    /// <summary>Read a device string descriptor, empty string on failure.</summary>
    internal static string GetString(IntPtr handle, bool serial)
    {
        var sb = new StringBuilder(256);
        var ok = serial
            ? HidD_GetSerialNumberString(handle, sb, (uint)sb.Capacity)
            : HidD_GetProductString(handle, sb, (uint)sb.Capacity);
        return ok ? sb.ToString().TrimEnd('\0') : "";
    }

    // — setupapi.dll —

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize,
        out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // — kernel32.dll —

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    /// <summary>Open a HID device path for feature-report access (read/write,
    /// fully shared — Synapse may hold the same collection open). Returns
    /// IntPtr.Zero when the collection is exclusive to the OS input stack;
    /// callers fall through to the next candidate interface.</summary>
    internal static IntPtr Open(string path) => Open(path, GENERIC_READ | GENERIC_WRITE);

    internal static IntPtr Open(string path, uint desiredAccess)
    {
        var handle = CreateFileW(path, desiredAccess, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return handle == InvalidHandleValue ? IntPtr.Zero : handle;
    }

    /// <summary>Keyboard/mouse top-level collections are exclusive to the OS
    /// input stack for RW access, and on some modern dongles they are the
    /// only collections that carry the vendor feature report. The feature
    /// IOCTLs are FILE_ANY_ACCESS, so narrower access modes still yield a
    /// handle that can talk feature reports. Tries RW → W → R → query-only.
    /// Returns IntPtr.Zero when every mode is refused.</summary>
    internal static IntPtr OpenForFeature(string path, out uint usedAccess)
    {
        foreach (var access in new[] { GENERIC_READ | GENERIC_WRITE, GENERIC_WRITE, GENERIC_READ, 0u })
        {
            var handle = Open(path, access);
            if (IsValid(handle))
            {
                usedAccess = access;
                return handle;
            }
        }
        usedAccess = 0;
        return IntPtr.Zero;
    }

    public static bool IsValid(IntPtr handle) => handle != IntPtr.Zero && handle != InvalidHandleValue;
}
