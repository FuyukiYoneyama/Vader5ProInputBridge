using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int ErrorNoMoreItems = 259;
    private const ushort TargetVid = 0x37D7;
    private const ushort TargetPid = 0x2401;
    private static readonly List<CaptureTarget> CaptureTargets = new();

    [STAThread]
    private static void Main(string[] args)
    {
        using var capture = new StringWriter();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        Console.SetOut(capture);
        Console.SetError(capture);
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: VaderHidProbe.exe");
        }
        else
        {
            Probe();
        }

        Console.SetOut(originalOut);
        Console.SetError(originalError);
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeForm(capture.ToString(), CaptureTargets));
    }

    private static void Probe()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == new IntPtr(-1))
        {
            Console.Error.WriteLine($"SetupDiGetClassDevs failed: {Marshal.GetLastWin32Error()}");
            return;
        }

        int found = 0;
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorNoMoreItems)
                        Console.Error.WriteLine($"SetupDiEnumDeviceInterfaces[{index}] failed: {error}");
                    break;
                }

                var deviceInfo = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };
                SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, IntPtr.Zero, 0,
                    out uint required, ref deviceInfo);
                if (required == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(deviceSet, ref interfaceData, detail,
                            required, out _, ref deviceInfo))
                    {
                        Console.Error.WriteLine($"SetupDiGetDeviceInterfaceDetail[{index}] failed: {Marshal.GetLastWin32Error()}");
                        continue;
                    }

                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                    Inspect(path);
                    if (path.Contains($"vid_{TargetVid:x4}&pid_{TargetPid:x4}", StringComparison.OrdinalIgnoreCase))
                        found++;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(deviceSet); }

        Console.WriteLine($"\nMatching VADER 5 Pro HID interfaces: {found}");
    }

    private static void Inspect(string path)
    {
        if (!path.Contains($"vid_{TargetVid:x4}&pid_{TargetPid:x4}", StringComparison.OrdinalIgnoreCase))
            return;

        using var handle = CreateFile(path, GenericRead, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        Console.WriteLine("--- VADER 5 PRO HID interface ---");
        Console.WriteLine($"Path: {path}");
        Match mi = Regex.Match(path, @"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
        Console.WriteLine($"Interface number: {(mi.Success ? Convert.ToInt32(mi.Groups[1].Value, 16).ToString() : "not encoded in path")}");
        if (handle.IsInvalid)
        {
            Console.WriteLine($"Open: FAILED (Win32 {Marshal.GetLastWin32Error()})");
            return;
        }

        Console.WriteLine("Open: OK");
        var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
        if (HidD_GetAttributes(handle, ref attributes))
            Console.WriteLine($"VID:PID: {attributes.VendorId:X4}:{attributes.ProductId:X4}, version 0x{attributes.VersionNumber:X4}");
        Console.WriteLine($"Manufacturer: {GetHidString(handle, HidD_GetManufacturerString)}");
        Console.WriteLine($"Product: {GetHidString(handle, HidD_GetProductString)}");
        Console.WriteLine($"Serial: {GetHidString(handle, HidD_GetSerialNumberString)}");

        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            Console.WriteLine($"Capabilities: unavailable (Win32 {Marshal.GetLastWin32Error()})");
            return;
        }

        try
        {
            int status = HidP_GetCaps(preparsed, out HidpCaps caps);
            if (status != 0x00110000)
            {
                Console.WriteLine($"Capabilities: unavailable (HIDP status 0x{status:X8})");
                return;
            }
            Console.WriteLine($"Usage page: 0x{caps.UsagePage:X4}; usage: 0x{caps.Usage:X4}");
            Console.WriteLine($"Input report length: {caps.InputReportByteLength} bytes");
            Console.WriteLine($"Output report length: {caps.OutputReportByteLength} bytes");
            Console.WriteLine($"Feature report length: {caps.FeatureReportByteLength} bytes");
            if (caps.InputReportByteLength > 0)
            {
                Match collection = Regex.Match(path, @"&(?<kind>mi|ig)_(?<number>[0-9a-f]{2})", RegexOptions.IgnoreCase);
                string label = collection.Success
                    ? $"{collection.Groups["kind"].Value.ToUpperInvariant()}_{collection.Groups["number"].Value.ToUpperInvariant()}"
                    : "HID collection";
                CaptureTargets.Add(new CaptureTarget(label, path, caps.InputReportByteLength));
            }
        }
        finally { HidD_FreePreparsedData(preparsed); }
    }

    private delegate bool HidStringGetter(SafeFileHandle handle, StringBuilder buffer, int length);

    private static string GetHidString(SafeFileHandle handle, HidStringGetter getter)
    {
        var buffer = new StringBuilder(256);
        return getter(handle, buffer, buffer.Capacity * 2) ? buffer.ToString() : "(unavailable)";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
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

    internal sealed record CaptureTarget(string Name, string Path, int InputReportLength)
    {
        public override string ToString() => $"{Name} (input {InputReportLength} bytes)";
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, StringBuilder buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle handle, StringBuilder buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, StringBuilder buffer, int length);
    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HidAttributes attributes);
    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);
    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize,
        out uint requiredSize, ref SpDevInfoData deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
