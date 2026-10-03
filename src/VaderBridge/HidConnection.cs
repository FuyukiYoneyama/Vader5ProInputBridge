// Flydigi acquire/release request layout follows SDL_hidapi_flydigi.c.
// SDL-origin portions: Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>.
// Adapted C# implementation: Copyright (c) 2026 FUYUKI YONEYAMA (MIT).
// Preserve the upstream Zlib notice in licenses/SDL-LICENSE.txt.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using InputTools;

internal sealed class HidConnection : IDisposable
{
    // Flydigiの全PIDを探索し、拡張HIDの形式で対象を選ぶ。
    private const string VendorPathToken = "VID_37D7";
    internal sealed record Device(string Path, ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int Error);
    internal Device Target { get; }
    internal FileStream Input { get; }
    private readonly SafeFileHandle _output;
    private readonly SessionLog _log;
    private readonly object _writeLock = new();
    private bool _acquireSent;
    internal bool UseWriteFile { get; }

    internal HidConnection(Device target, bool useWriteFile, SessionLog log)
    {
        Target = target; UseWriteFile = useWriteFile; _log = log;
        SafeFileHandle input = CreateFile(target.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (input.IsInvalid) { int error = Marshal.GetLastWin32Error(); input.Dispose(); throw new Win32Exception(error, "MI_01 input open"); }
        _output = CreateFile(target.Path, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (_output.IsInvalid) { int error = Marshal.GetLastWin32Error(); input.Dispose(); _output.Dispose(); throw new Win32Exception(error, "MI_01 output open"); }
        try { Input = new FileStream(input, FileAccess.Read, 1, true); }
        catch { input.Dispose(); _output.Dispose(); throw; }
        _log.Write("hid-open", new { target, readAccess = "GENERIC_READ", writeAccess = "GENERIC_WRITE", share = "READ|WRITE", overlappedRead = true });
    }

    internal bool Acquire(bool queryInfo = true)
    {
        byte[] data = Request(0x1C, 23); data[5] = 1; data[6] = (byte)'S'; data[7] = (byte)'D'; data[8] = (byte)'L';
        bool result = Send(data, "acquire"); _acquireSent |= result;
        if (queryInfo) Send(Request(0x01, 2), "info");
        return result;
    }
    internal bool QueryStatus() => Send(Request(0x10, 0), "status");
    internal void Release()
    {
        if (!_acquireSent) return;
        byte[] data = Request(0x1C, 23); data[6] = (byte)'S'; data[7] = (byte)'D'; data[8] = (byte)'L';
        bool result = Send(data, "release"); _log.Write("hid-release-result", new { success = result }); _acquireSent = false;
    }
    private byte[] Request(byte command, byte length)
    {
        var data = new byte[Target.OutputLength]; data[1] = 0x5A; data[2] = 0xA5; data[3] = command; data[4] = length; return data;
    }
    private bool Send(byte[] data, string command)
    {
        lock (_writeLock)
        {
            uint bytes = 0;
            bool result = UseWriteFile ? WriteFile(_output, data, (uint)data.Length, out bytes, IntPtr.Zero) : HidD_SetOutputReport(_output, data, (uint)data.Length);
            int error = result ? 0 : Marshal.GetLastWin32Error();
            if (UseWriteFile && bytes != data.Length) result = false;
            _log.Write("hid-output", new { path = Target.Path, command, api = UseWriteFile ? "WriteFile" : "HidD_SetOutputReport", payload = Convert.ToHexString(data), success = result, bytes, win32Error = error });
            return result;
        }
    }
    public void Dispose() { Input.Dispose(); _output.Dispose(); _log.Write("hid-handles-closed", new { path = Target.Path }); }

    internal static Device[] Enumerate()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<Device>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var item = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref item))
                {
                    int error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new Win32Exception(error);
                }
                var info = new DevInfo { Size = (uint)Marshal.SizeOf<DevInfo>() };
                SetupDiGetDeviceInterfaceDetail(set, ref item, IntPtr.Zero, 0, out uint required, ref info);
                if (required == 0) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref item, detail, required, out _, ref info)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                    if (!path.Contains(VendorPathToken, StringComparison.OrdinalIgnoreCase)) continue;
                    using SafeFileHandle handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) { devices.Add(new(path, 0, 0, 0, 0, Marshal.GetLastWin32Error())); continue; }
                    if (!HidD_GetPreparsedData(handle, out IntPtr data)) { devices.Add(new(path, 0, 0, 0, 0, Marshal.GetLastWin32Error())); continue; }
                    try
                    {
                        int result = HidP_GetCaps(data, out Caps caps);
                        devices.Add(new(path, caps.UsagePage, caps.Usage, caps.InputLength, caps.OutputLength, result == 0x110000 ? 0 : result));
                    }
                    finally { HidD_FreePreparsedData(data); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return devices.ToArray();
    }
    internal static bool Matches(Device d) => d.Error == 0 && d.Path.Contains("MI_01", StringComparison.OrdinalIgnoreCase) && d.UsagePage == 0xFFA0 && d.Usage == 1 && d.InputLength == 33 && d.OutputLength == 33;

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public uint Size; public Guid Guid; public uint Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct DevInfo { public uint Size; public Guid Guid; public uint Instance; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Caps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort Links, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetOutputReport(SafeFileHandle handle, [In] byte[] data, uint length);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DevInfo info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle handle, byte[] data, uint size, out uint written, IntPtr overlapped);
}
