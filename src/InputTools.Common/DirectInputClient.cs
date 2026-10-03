using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InputTools;

// Native signatures and structure layout follow Windows SDK dinput.h (DirectInput 8 Unicode).
public sealed class DirectInputClient : IDisposable
{
    public sealed record Device(Guid Instance, string Name, string Product, string Path, int? VJoyId, string Identification);
    public sealed record Axis(string Name, uint ObjectId, int Offset, int Minimum, int Maximum);
    public sealed record Snapshot(int Result, int[] Axes, bool[] Buttons, uint[] Pov);
    private IntPtr _directInput, _device;
    private readonly IntPtr _window;
    private int _dataSize;
    private int[] _buttonOffsets = Array.Empty<int>(), _povOffsets = Array.Empty<int>();
    public Axis[] Axes { get; private set; } = Array.Empty<Axis>();
    public int ButtonCount => _buttonOffsets.Length;
    public int PovCount => _povOffsets.Length;
    public Device? Selected { get; private set; }

    public DirectInputClient(IntPtr window)
    {
        _window = window;
        Guid iid = new("BF798031-483A-4DA2-AA99-5D64ED369700");
        Check(DirectInput8Create(GetModuleHandle(null), 0x0800, ref iid, out _directInput, IntPtr.Zero), "DirectInput8Create");
    }

    public Device[] Enumerate()
    {
        var instances = new List<DeviceInstance>();
        Exception? error = null;
        EnumCallback callback = (pointer, _) =>
        {
            try { instances.Add(Marshal.PtrToStructure<DeviceInstance>(pointer)); return 1; }
            catch (Exception ex) { error = ex; return 0; }
        };
        Check(Method<EnumDevices>(_directInput, 4)(_directInput, 4, callback, IntPtr.Zero, 1), "EnumDevices");
        GC.KeepAlive(callback);
        if (error is not null) throw error;
        var result = new List<Device>();
        foreach (var instance in instances)
        {
            // vJoy's product GUID encodes PID BEAD / VID 1234. Name is kept as additional evidence.
            uint productCode = BitConverter.ToUInt32(instance.Product.ToByteArray(), 0);
            if (productCode != 0xBEAD1234 && !instance.ProductName.Contains("vJoy", StringComparison.OrdinalIgnoreCase)) continue;
            Guid id = instance.Instance;
            int hr = Method<CreateDevice>(_directInput, 3)(_directInput, ref id, out IntPtr device, IntPtr.Zero);
            if (hr < 0) continue;
            try
            {
                var property = new GuidPath { Header = Header(Marshal.SizeOf<GuidPath>()), Path = "" };
                hr = Property(device, 12, ref property);
                string path = hr >= 0 ? property.Path : "";
                int? reportId = VJoyReportId(path, out string identification);
                result.Add(new(id, instance.InstanceName, instance.ProductName, path, reportId, identification));
            }
            finally { Release(device); }
        }
        return result.ToArray();
    }

    public void Open(Device device)
    {
        CloseDevice();
        Guid id = device.Instance;
        Check(Method<CreateDevice>(_directInput, 3)(_directInput, ref id, out _device, IntPtr.Zero), "CreateDevice");
        try
        {
            var objects = new List<ObjectInstance>();
            Exception? error = null;
            EnumCallback callback = (pointer, _) =>
            {
                try { objects.Add(Marshal.PtrToStructure<ObjectInstance>(pointer)); return 1; }
                catch (Exception ex) { error = ex; return 0; }
            };
            Check(Method<EnumObjects>(_device, 4)(_device, callback, IntPtr.Zero, 0), "EnumObjects");
            GC.KeepAlive(callback); if (error is not null) throw error;
            var formatObjects = new List<ObjectFormat>(); var axisList = new List<Axis>();
            var buttonOffsets = new List<int>(); var povOffsets = new List<int>(); var guids = new List<IntPtr>();
            int offset = 0;
            try
            {
                foreach (var obj in objects.Where(o => (o.Type & 3) != 0 || (o.Type & 0x10) != 0))
                {
                    IntPtr guid = Marshal.AllocHGlobal(16); guids.Add(guid); Marshal.StructureToPtr(obj.TypeGuid, guid, false);
                    formatObjects.Add(new() { Guid = guid, Offset = (uint)offset, Type = obj.Type });
                    if ((obj.Type & 3) != 0)
                    {
                        var range = new RangeProperty { Header = Header(Marshal.SizeOf<RangeProperty>(), obj.Type, 2) };
                        Check(Property(_device, 4, ref range), "DIPROP_RANGE");
                        axisList.Add(new(AxisName(obj, axisList.Count), obj.Type, offset, range.Minimum, range.Maximum));
                    }
                    else povOffsets.Add(offset);
                    offset += 4;
                }
                foreach (var obj in objects.Where(o => (o.Type & 0x0C) != 0))
                {
                    IntPtr guid = Marshal.AllocHGlobal(16); guids.Add(guid); Marshal.StructureToPtr(obj.TypeGuid, guid, false);
                    formatObjects.Add(new() { Guid = guid, Offset = (uint)offset, Type = obj.Type }); buttonOffsets.Add(offset++);
                }
                _dataSize = (offset + 3) & ~3;
                int size = Marshal.SizeOf<ObjectFormat>(); IntPtr array = Marshal.AllocHGlobal(size * formatObjects.Count);
                try
                {
                    for (int i = 0; i < formatObjects.Count; i++) Marshal.StructureToPtr(formatObjects[i], array + i * size, false);
                    var format = new DataFormat { Size = (uint)Marshal.SizeOf<DataFormat>(), ObjectSize = (uint)size, Flags = 1, DataSize = (uint)_dataSize, ObjectCount = (uint)formatObjects.Count, Objects = array };
                    Check(Method<SetDataFormat>(_device, 11)(_device, ref format), "SetDataFormat");
                }
                finally { Marshal.FreeHGlobal(array); }
            }
            finally { foreach (IntPtr guid in guids) Marshal.FreeHGlobal(guid); }
            Check(Method<SetCooperativeLevel>(_device, 13)(_device, _window, 0x0000000A), "SetCooperativeLevel background/nonexclusive");
            Check(Method<SimpleMethod>(_device, 7)(_device), "Acquire DirectInput");
            Axes = axisList.ToArray(); _buttonOffsets = buttonOffsets.ToArray(); _povOffsets = povOffsets.ToArray(); Selected = device;
        }
        catch { CloseDevice(); throw; }
    }

    public Snapshot Read()
    {
        if (_device == IntPtr.Zero) return new(unchecked((int)0x8007001E), Array.Empty<int>(), Array.Empty<bool>(), Array.Empty<uint>());
        int result = Method<SimpleMethod>(_device, 25)(_device);
        if (result < 0) { _ = Method<SimpleMethod>(_device, 7)(_device); return new(result, Array.Empty<int>(), Array.Empty<bool>(), Array.Empty<uint>()); }
        byte[] data = new byte[_dataSize];
        result = Method<GetDeviceState>(_device, 9)(_device, (uint)data.Length, data);
        if (result < 0) { _ = Method<SimpleMethod>(_device, 7)(_device); return new(result, Array.Empty<int>(), Array.Empty<bool>(), Array.Empty<uint>()); }
        return new(result, Axes.Select(a => BitConverter.ToInt32(data, a.Offset)).ToArray(), _buttonOffsets.Select(o => (data[o] & 0x80) != 0).ToArray(), _povOffsets.Select(o => BitConverter.ToUInt32(data, o)).ToArray());
    }

    public static double Normalize(int value, int minimum, int maximum) => maximum <= minimum ? 0 : (value - (double)minimum) * 2 / (maximum - (double)minimum) - 1;
    private static string AxisName(ObjectInstance obj, int index) => obj.Usage switch { 0x30 => "X", 0x31 => "Y", 0x32 => "Z", 0x33 => "Rx", 0x34 => "Ry", 0x35 => "Rz", 0x36 => "Slider1", 0x37 => "Slider2", 0x38 => "Wheel", _ => $"Axis{index} {obj.Name}" };
    private static PropertyHeader Header(int size, uint objectId = 0, uint how = 0) => new() { Size = (uint)size, HeaderSize = 16, ObjectId = objectId, How = how };
    private static int Property<T>(IntPtr device, int id, ref T property) where T : struct
    {
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(property, buffer, false); int hr = Method<GetProperty>(device, 5)(device, new IntPtr(id), buffer); if (hr >= 0) property = Marshal.PtrToStructure<T>(buffer); return hr; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Check(int hr, string operation) { if (hr < 0) throw new InvalidOperationException($"{operation}: HRESULT=0x{hr:X8}"); }
    private static void Release(IntPtr instance) { if (instance != IntPtr.Zero) Marshal.Release(instance); }
    public void CloseDevice()
    {
        if (_device != IntPtr.Zero) { _ = Method<SimpleMethod>(_device, 8)(_device); Release(_device); _device = IntPtr.Zero; }
        Selected = null; Axes = Array.Empty<Axis>(); _buttonOffsets = Array.Empty<int>(); _povOffsets = Array.Empty<int>();
    }
    public void Dispose() { CloseDevice(); Release(_directInput); _directInput = IntPtr.Zero; }

    private static int? VJoyReportId(string path, out string detail)
    {
        detail = "path-empty";
        if (string.IsNullOrEmpty(path)) return null;
        // Access 0 reads HID descriptor metadata only; it neither reads input nor sends output.
        using SafeFileHandle handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) { detail = $"descriptor-open-error={Marshal.GetLastWin32Error()}"; return null; }
        if (!HidD_GetPreparsedData(handle, out IntPtr prep)) { detail = $"preparsed-data-error={Marshal.GetLastWin32Error()}"; return null; }
        try
        {
            int status = HidP_GetCaps(prep, out HidCaps caps);
            if (status != 0x00110000) { detail = $"caps-status=0x{status:X8}"; return null; }
            ushort count = caps.InputButtonCaps;
            byte[] bytes = new byte[72 * count];
            if (count == 0) { detail = "input-button-caps=0"; return null; }
            status = HidP_GetButtonCaps(0, bytes, ref count, prep);
            if (status != 0x00110000) { detail = $"button-caps-status=0x{status:X8}, count={count}"; return null; }
            int[] ids = Enumerable.Range(0, count).Select(i => (int)bytes[i * 72 + 2]).Distinct().ToArray();
            detail = $"descriptor-report-ids={string.Join(',', ids)}";
            // Additional report IDs can share a vJoy collection; select the device ID in vJoy's 1-16 range.
            int[] deviceIds = ids.Where(id => id is >= 1 and <= 16).ToArray();
            return deviceIds.Length == 1 ? deviceIds[0] : null;
        }
        finally { HidD_FreePreparsedData(prep); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DeviceInstance
    {
        public uint Size; public Guid Instance, Product; public uint Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string InstanceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ProductName;
        public Guid ForceFeedback; public ushort UsagePage, Usage;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ObjectInstance
    {
        public uint Size; public Guid TypeGuid; public uint Offset, Type, Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        public uint ForceMaximum, ForceResolution; public ushort Collection, Designator, UsagePage, Usage; public uint Dimension; public ushort Exponent, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyHeader { public uint Size, HeaderSize, ObjectId, How; }
    [StructLayout(LayoutKind.Sequential)] private struct RangeProperty { public PropertyHeader Header; public int Minimum, Maximum; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct GuidPath { public PropertyHeader Header; public Guid Class; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectFormat { public IntPtr Guid; public uint Offset, Type, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct DataFormat { public uint Size, ObjectSize, Flags, DataSize, ObjectCount; public IntPtr Objects; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtonCaps, InputValueCaps, InputIndices, OutputButtonCaps, OutputValueCaps, OutputIndices, FeatureButtonCaps, FeatureValueCaps, FeatureIndices;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumCallback(IntPtr item, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumDevices(IntPtr self, uint type, EnumCallback callback, IntPtr context, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumObjects(IntPtr self, EnumCallback callback, IntPtr context, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDevice(IntPtr self, ref Guid instance, out IntPtr device, IntPtr outer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetProperty(IntPtr self, IntPtr property, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetDataFormat(IntPtr self, ref DataFormat format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetCooperativeLevel(IntPtr self, IntPtr window, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDeviceState(IntPtr self, uint size, [Out] byte[] data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SimpleMethod(IntPtr self);
    [DllImport("dinput8.dll", CallingConvention = CallingConvention.StdCall)] private static extern int DirectInput8Create(IntPtr module, uint version, ref Guid iid, out IntPtr result, IntPtr outer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string file, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, [Out] byte[] caps, ref ushort length, IntPtr data);
}
