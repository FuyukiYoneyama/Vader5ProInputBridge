using System.Runtime.InteropServices;

internal sealed class VJoyStandardOutput : IDisposable
{
    internal static readonly (string Name, int SourceIndex)[] SourceButtons =
    {
        ("A", 0), ("B", 1), ("X", 2), ("Y", 3), ("LB", 9), ("RB", 10),
        ("Back", 4), ("Start", 6), ("LS", 7), ("RS", 8)
    };
    private static readonly (string Name, int SourceIndex)[] AdditionalButtons =
    {
        ("M1", 11), ("M2", 12), ("M3", 13), ("M4", 14), ("LM", 17), ("RM", 18),
        ("C", 15), ("Z", 16), ("Fn", 19), ("Guide", 5)
    };
    internal static readonly (string Name, int SourceIndex)[] OutputButtons = SourceButtons.Concat(AdditionalButtons).ToArray();
    private static readonly (string Name, uint Usage)[] Axes =
    {
        ("X", 0x30), ("Y", 0x31), ("Z", 0x32), ("Rx", 0x33), ("Ry", 0x34),
        ("Rz", 0x35), ("Slider1", 0x36), ("Slider2", 0x37)
    };
    private readonly StandardOutputConfig _config;
    private readonly Action<string> _log;
    private readonly uint _id;
    private readonly Dictionary<string, (uint Usage, int Min, int Max)> _axes = new();
    private readonly Dictionary<string, int> _values = new();
    private readonly Dictionary<int, bool> _buttons = new();
    private int _buttonCount, _povCount;
    private bool _acquired;
    internal uint Pov { get; private set; } = uint.MaxValue;
    internal Dictionary<string, int> AxisValues => new(_values);
    internal Dictionary<int, bool> ButtonValues => new(_buttons);
    internal Dictionary<string, double> NormalizedAxes => _axes.ToDictionary(a => a.Key,
        a => _values.TryGetValue(a.Key, out int value) ? (value - a.Value.Min) * 2d / (a.Value.Max - a.Value.Min) - 1 : 0);

    internal VJoyStandardOutput(StandardOutputConfig config, Action<string> log)
    {
        _config = config; _id = checked((uint)config.VJoyId); _log = log;
    }

    internal bool Start()
    {
        if (_id is < 1 or > 16 || !vJoyEnabled()) { _log("通常入力用vJoyのID・有効状態を確認してください。"); return false; }
        int status = GetVJDStatus(_id), owner = GetOwnerPid(_id);
        _buttonCount = GetVJDButtonNumber(_id); _povCount = GetVJDContPovNumber(_id);
        _log($"standard device {_id}: status={status}, ownerPid={owner}, buttons={_buttonCount}, continuousPovs={_povCount}");
        if (status != 1) { _log($"通常入力用Device {_id}の所有状態を確認してください。ownerPid={owner}"); return false; }
        if (!float.IsFinite(_config.StickDeadzone) || _config.StickDeadzone is < 0 or >= 1)
            throw new InvalidDataException("standard.stickDeadzoneは0以上1未満で設定してください。");
        var numbers = new HashSet<int>();
        foreach (var button in OutputButtons)
            if (!_config.Buttons.TryGetValue(button.Name, out int number) || number < 1 || number > _buttonCount || !numbers.Add(number))
                throw new InvalidDataException($"Device {_id}の{button.Name}割当とボタン数を確認してください。");
        if (_povCount < 1) throw new InvalidDataException($"Device {_id}をContinuous POV 1個以上に設定してください。");
        foreach (var axis in Axes)
        {
            uint usage = axis.Usage;
            int existence = GetVJDAxisExist(_id, usage);
            _log($"standard device {_id} axis {axis.Name}: existenceResult={existence}");
            if (axis.Name == "Rz" && existence != 1) continue;
            if (existence != 1 || !GetVJDAxisMin(_id, usage, out int min) || !GetVJDAxisMax(_id, usage, out int max) || max <= min)
                throw new InvalidDataException($"Device {_id}の軸{axis.Name}を有効にしてください。");
            _axes.Add(axis.Name, (usage, min, max));
        }
        if (!AcquireVJD(_id)) { _log($"通常入力用AcquireVJD({_id})の結果を確認してください。ownerPid={GetOwnerPid(_id)}"); return false; }
        _acquired = true;
        try
        {
            for (int b = 1; b <= Math.Min(255, _buttonCount); b++) Button(b, false, true);
            foreach (var axis in _axes) Axis(axis.Key, axis.Key is "Slider1" or "Slider2" ? -1 : 0, true);
            for (byte p = 1; p <= _povCount; p++)
                if (!SetContPov(uint.MaxValue, _id, p)) throw new IOException($"Device {_id}の十字キー初期化結果を確認してください。");
            Pov = uint.MaxValue;
            _log($"Device {_id}を取得しました。7使用軸・20ボタン・十字キー。Rzは中央。ownerPid={GetOwnerPid(_id)}");
            return true;
        }
        catch { Stop(); throw; }
    }

    internal void Write(VaderInput input)
    {
        if (!_acquired) return;
        foreach (var button in OutputButtons) Button(_config.Buttons[button.Name], input.Buttons[button.SourceIndex]);
        var s = input.Standard;
        Axis("X", Stick(s.LX)); Axis("Y", -Stick(s.LY)); Axis("Rx", Stick(s.RX)); Axis("Ry", -Stick(s.RY));
        Axis("Slider1", s.LT / 255d * 2 - 1); Axis("Slider2", s.RT / 255d * 2 - 1);
        Axis("Z", (s.LT - s.RT) / 255d);
        if (_axes.ContainsKey("Rz")) Axis("Rz", 0);
        WritePov(s.Pov);
    }
    private double Stick(short raw)
    {
        double value = raw < 0 ? raw / 32768d : raw / 32767d;
        double magnitude = Math.Abs(value), deadzone = _config.StickDeadzone;
        return magnitude <= deadzone ? 0 : Math.CopySign((magnitude - deadzone) / (1 - deadzone), value);
    }
    private void Axis(string name, double normalized, bool force = false)
    {
        var axis = _axes[name];
        int value = (int)Math.Round(axis.Min + (Math.Clamp(normalized, -1, 1) + 1) * .5 * (axis.Max - axis.Min));
        if (!force && _values.TryGetValue(name, out int old) && old == value) return;
        if (!SetAxis(value, _id, axis.Usage)) throw new IOException($"Device {_id}の軸{name}書込み結果を確認してください。");
        _values[name] = value;
    }
    private void Button(int number, bool down, bool force = false)
    {
        if (!force && _buttons.TryGetValue(number, out bool old) && old == down) return;
        if (!SetBtn(down, _id, checked((byte)number))) throw new IOException($"Device {_id}のボタン{number}書込み結果を確認してください。");
        _buttons[number] = down;
    }
    internal void WritePov(uint value, bool force = false)
    {
        if (!force && Pov == value) return;
        if (!SetContPov(value, _id, 1)) throw new IOException($"Device {_id}の十字キー書込み結果を確認してください。");
        Pov = value;
    }
    internal void SelfTestAxes(double value)
    {
        foreach (string name in _axes.Keys) Axis(name, name == "Rz" ? 0 : value, true);
        _log($"standard self-test axes: normalized={value}");
    }
    internal void SelfTestButton(byte number, bool down) { Button(number, down, true); _log($"standard self-test button {number}: down={down}"); }
    internal void SelfTestPov(uint value) { WritePov(value, true); _log($"standard self-test pov: value={value}"); }

    private void Stop()
    {
        if (!_acquired) return;
        for (int b = 1; b <= Math.Min(255, _buttonCount); b++) _log($"standard stop button {b}: result={SetBtn(false, _id, checked((byte)b))}");
        foreach (var axis in _axes)
        {
            int neutral = axis.Key is "Slider1" or "Slider2" ? axis.Value.Min : (axis.Value.Min + axis.Value.Max) / 2;
            _log($"standard stop axis {axis.Key}: value={neutral}, result={SetAxis(neutral, _id, axis.Value.Usage)}");
        }
        for (byte p = 1; p <= _povCount; p++) _log($"standard stop pov {p}: result={SetContPov(uint.MaxValue, _id, p)}");
        RelinquishVJD(_id); _acquired = false; _log($"standard device {_id} relinquish completed: status={GetVJDStatus(_id)}, ownerPid={GetOwnerPid(_id)}");
    }
    public void Dispose() => Stop();

    private const string Library = "vJoyInterface";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool vJoyEnabled();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDStatus(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int GetOwnerPid(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDButtonNumber(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDContPovNumber(uint id);
    // vJoy returns 1 on success and can return a negative error code through this BOOL export.
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDAxisExist(uint id, uint usage);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVJDAxisMin(uint id, uint usage, out int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVJDAxisMax(uint id, uint usage, out int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AcquireVJD(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] private static extern void RelinquishVJD(uint id);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetAxis(int value, uint id, uint usage);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool down, uint id, byte button);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetContPov(uint value, uint id, byte pov);
}
