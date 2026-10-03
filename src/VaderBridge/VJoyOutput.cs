using System.Runtime.InteropServices;
using System.Diagnostics;

internal sealed class VJoyOutput : IDisposable
{
    private static readonly (string Name, int SourceIndex)[] SourceButtons =
    {
        ("M1", 11), ("M2", 12), ("M3", 13), ("M4", 14),
        ("C", 15), ("Z", 16), ("LM", 17), ("RM", 18), ("Fn", 19), ("Guide", 5)
    };

    private readonly uint _deviceId;
    private readonly BridgeConfig _config;
    private readonly Action<string> _log;
    private readonly Dictionary<string, AxisState> _axes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _buttonStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly TiltEstimator _tiltEstimator = new();
    private readonly Dictionary<uint, (int Min, int Max)> _enabledAxes = new();
    private long _sampleTimestamp, _previousTimestamp;
    private double _sampleSeconds = .01;
    private readonly Dictionary<uint, int> _lastAxisValues = new();
    private readonly Dictionary<int, bool> _lastButtonValues = new();
    internal Dictionary<int, bool> ButtonValues => new(_lastButtonValues);
    internal Dictionary<string, double> NormalizedAxes => _enabledAxes.ToDictionary(a => AxisName(a.Key), a => _lastAxisValues.TryGetValue(a.Key, out int value) ? (value - a.Value.Min) * 2d / (a.Value.Max - a.Value.Min) - 1 : 0);
    private static string AxisName(uint usage) => usage switch { 0x30 => "X", 0x31 => "Y", 0x32 => "Z", 0x33 => "Rx", 0x34 => "Ry", 0x35 => "Rz", 0x36 => "Slider1", 0x37 => "Slider2", _ => throw new ArgumentOutOfRangeException(nameof(usage)) };
    private bool _acquired;

    internal VJoyOutput(BridgeConfig config, Action<string> log)
    {
        _config = config;
        _deviceId = checked((uint)config.VJoyId);
        _log = log;
    }

    internal bool Start()
    {
        if (!_config.ExtendedEnabled)
        {
            _log($"vJoy device {_deviceId}: 拡張出力OFF。");
            return false;
        }
        if (!vJoyEnabled())
        {
            _log("vJoy is not enabled.");
            return false;
        }
        if (_deviceId is < 1 or > 16)
        {
            _log($"vJoy device ID {_deviceId} is outside the supported range 1-16.");
            return false;
        }

        int status = GetVJDStatus(_deviceId);
        int buttonCount = GetVJDButtonNumber(_deviceId);
        _log($"vJoy device {_deviceId}: status={StatusName(status)}, buttons={buttonCount}.");
        _log($"vJoy device {_deviceId}: ownerPid={GetOwnerPid(_deviceId)}.");
        if (status != 1)
        {
            _log("vJoy output not started. The configured device must exist and be free.");
            return false;
        }

        var configuredButtons = new HashSet<int>();
        foreach ((string name, _) in SourceButtons)
        {
            if (!_config.Buttons.TryGetValue(name, out int outputButton) || outputButton < 1 || outputButton > buttonCount)
            {
                _log($"vJoy output not started. Button {name} needs a configured vJoy button; device {_deviceId} currently has {buttonCount}.");
                return false;
            }
            if (!configuredButtons.Add(outputButton))
            {
                _log($"vJoy output not started. Multiple source buttons map to vJoy button {outputButton}.");
                return false;
            }
        }

        if (!float.IsFinite(_config.Gyro.FullScaleRadiansPerSecond) || _config.Gyro.FullScaleRadiansPerSecond <= 0)
        {
            _log("vJoy output not started. gyro.fullScaleRadiansPerSecond must be a positive finite value.");
            return false;
        }
        if (!float.IsFinite(_config.Accel.FullScaleMetersPerSecondSquared) || _config.Accel.FullScaleMetersPerSecondSquared <= 0 ||
            !double.IsFinite(_config.Yaw.YawRangeDeg) || _config.Yaw.YawRangeDeg <= 0)
        {
            _log("加速度とYawの出力範囲には有限の正の値を設定してください。");
            return false;
        }
        if (!float.IsFinite(_config.Tilt.FullScaleDegrees) || _config.Tilt.FullScaleDegrees <= 0 ||
            !float.IsFinite(_config.Tilt.CorrectionTimeConstantSeconds) || _config.Tilt.CorrectionTimeConstantSeconds <= 0)
        {
            _log("vJoy output not started. Tilt fullScaleDegrees and correctionTimeConstantSeconds must be positive finite values.");
            return false;
        }

        foreach ((string name, AxisConfig settings) in ConfiguredAxes())
        {
            if (!TryGetUsage(settings.Axis, out uint usage))
            {
                _log($"vJoy output not started. Unsupported vJoy axis '{settings.Axis}' for {name}.");
                return false;
            }
            if (!float.IsFinite(settings.Gain) || !float.IsFinite(settings.Deadzone) || settings.Deadzone is < 0 or >= 1 ||
                !float.IsFinite(settings.CenterOffset) || !float.IsFinite(settings.Smoothing) || settings.Smoothing is < 0 or >= 1)
            {
                _log($"vJoy output not started. Check gain, deadzone, centerOffset, and smoothing for {name}.");
                return false;
            }
            int existence = GetVJDAxisExist(_deviceId, usage);
            _log($"vJoy device {_deviceId} axis {settings.Axis}: existenceResult={existence}.");
            if (existence != 1 || !GetVJDAxisMin(_deviceId, usage, out int min) || !GetVJDAxisMax(_deviceId, usage, out int max) || max <= min)
            {
                _log($"vJoy output not started. Axis {settings.Axis} is not enabled or has no valid range on device {_deviceId}.");
                return false;
            }
            if (_axes.Values.Any(a => a.Usage == usage)) throw new InvalidDataException("vJoy軸の割当が重複しています。");
            _axes.Add(name, new AxisState(usage, min, max, settings));
        }

        for (uint usage = 0x30; usage <= 0x37; usage++)
            if (GetVJDAxisExist(_deviceId, usage) == 1 && GetVJDAxisMin(_deviceId, usage, out int low) && GetVJDAxisMax(_deviceId, usage, out int high) && high > low)
                _enabledAxes.Add(usage, (low, high));

        if (!AcquireVJD(_deviceId))
        {
            _log($"AcquireVJD({_deviceId}) failed; vJoy status={StatusName(GetVJDStatus(_deviceId))}.");
            return false;
        }
        _acquired = true;

        foreach (int button in configuredButtons)
        {
            if (!SetBtn(false, _deviceId, checked((byte)button)))
            {
                _log($"Could not initialize vJoy button {button}; releasing device.");
                Stop();
                return false;
            }
            _lastButtonValues[button] = false;
        }
        foreach (var axis in _enabledAxes)
        {
            int center = (int)Math.Round((axis.Value.Min + (double)axis.Value.Max) / 2);
            if (!SetAxis(center, _deviceId, axis.Key))
            {
                _log("Could not center the configured vJoy axes; releasing device.");
                Stop();
                return false;
            }
            _lastAxisValues[axis.Key] = center;
        }
        foreach (AxisState axis in _axes.Values) axis.LastOutput = axis.Center;
        _tiltEstimator.Reset();
        _log($"vJoy device {_deviceId} acquired; {SourceButtons.Length} buttons, accel X/Y/Z, gyro Rx/Ry/Rz, relative yaw Slider1, and pitch Slider2 are ready.");
        return true;
    }

    internal void WriteButtonStates(IReadOnlyList<bool> buttonStates) =>
        WriteButtonStates(index => index < buttonStates.Count && buttonStates[index], "HID button");

    private void WriteButtonStates(Func<int, bool> readButton, string sourceName)
    {
        if (!_acquired) return;
        foreach ((string name, int sourceIndex) in SourceButtons)
        {
            bool pressed = readButton(sourceIndex);
            int outputButton = _config.Buttons[name];
            if (!SetBtn(pressed, _deviceId, checked((byte)outputButton)))
                throw new InvalidOperationException($"SetBtn failed for {name} -> vJoy button {outputButton}.");
            _lastButtonValues[outputButton] = pressed;
            bool previous = _buttonStates.TryGetValue(name, out bool previousState) && previousState;
            if (pressed != previous)
            {
                _buttonStates[name] = pressed;
                _log($"vJoy button {outputButton} ({name}, {sourceName} {sourceIndex}): {(pressed ? "DOWN" : "UP")}");
            }
        }
    }

    internal void WriteGyro(float[] values)
    {
        if (!_acquired) return;
        ApplyAxis("X", values[0], _config.Gyro.FullScaleRadiansPerSecond, "gyro");
        ApplyAxis("Y", values[1], _config.Gyro.FullScaleRadiansPerSecond, "gyro");
        ApplyAxis("Z", values[2], _config.Gyro.FullScaleRadiansPerSecond, "gyro");
    }

    internal void WriteTilt(float[] gyroValues, float[] accelValues)
    {
        if (!_acquired) return;
        WriteAccel(accelValues);
        (double roll, double pitch) = _tiltEstimator.Update(gyroValues, accelValues, _config.Tilt.CorrectionTimeConstantSeconds, _sampleTimestamp);
        WriteTiltDegrees(roll * 180.0 / Math.PI, pitch * 180.0 / Math.PI);
    }
    internal void WriteTiltDegrees(double roll, double pitch)
    {
        if (!_acquired) return;
        ApplyAxis("TiltPitch", (float)pitch, _config.Tilt.FullScaleDegrees, "tilt pitch");
    }

    internal void WriteAccel(float[] values)
    {
        if (!_acquired) return;
        for (int i = 0; i < 3; i++)
            WriteNormalized($"Accel{"XYZ"[i]}", values[i] / (double)_config.Accel.FullScaleMetersPerSecondSquared);
    }

    internal void WriteYawDegrees(double angle)
    {
        if (_acquired) WriteNormalized("Yaw", angle / _config.Yaw.YawRangeDeg);
    }

    // 生加速度と相対Yawは実際のvJoy軸範囲への換算と飽和処理を行う。
    private void WriteNormalized(string name, double normalized)
    {
        AxisState axis = _axes[name];
        normalized = Math.Clamp(normalized, -1d, 1d);
        int value = Math.Clamp((int)Math.Round(axis.Min + (normalized + 1) * .5 * (axis.Max - (double)axis.Min)), axis.Min, axis.Max);
        if (!SetAxis(value, _deviceId, axis.Usage))
            throw new InvalidOperationException($"SetAxis failed for {name} -> vJoy {axis.Settings.Axis}.");
        axis.Smoothed = (float)normalized;
        axis.LastOutput = value;
        _lastAxisValues[axis.Usage] = value;
    }

    internal string TiltSummary => _tiltEstimator.IsInitialized
        ? $"Roll={TelemetryFormatting.AngleDegrees(_tiltEstimator.RollDegrees)}°, Pitch={TelemetryFormatting.AngleDegrees(_tiltEstimator.PitchDegrees)}°"
        : "not calibrated";

    internal string AxisOutputSummary => string.Join(", ", _axes.Select(pair =>
        TelemetryFormatting.AxisOutput(pair.Value.Settings.Axis, pair.Value.LastOutput, pair.Value.Smoothed)));

    internal double RollDegrees => _tiltEstimator.RollDegrees;
    internal double PitchDegrees => _tiltEstimator.PitchDegrees;
    internal Dictionary<string, int> AxisValues => _lastAxisValues.ToDictionary(a => AxisName(a.Key), a => a.Value);
    internal void SetSampleTime(long timestamp)
    {
        _sampleTimestamp = timestamp;
        _sampleSeconds = _previousTimestamp == 0 ? .01 : Math.Clamp((timestamp - _previousTimestamp) / (double)Stopwatch.Frequency, 0, .1);
        _previousTimestamp = timestamp;
    }
    internal void ResumeClock() { _previousTimestamp = 0; _tiltEstimator.ResumeClock(); }
    internal void SelfTestAxes(double normalized)
    {
        foreach (var axis in _enabledAxes)
        {
            int value = (int)Math.Round(axis.Value.Min + (normalized + 1) * .5 * (axis.Value.Max - axis.Value.Min));
            bool success = SetAxis(value, _deviceId, axis.Key);
            _log($"self-test axis 0x{axis.Key:X2}: value={value}, result={success}");
            if (!success) throw new IOException("vJoy自己試験の軸書込み結果を確認してください。");
            _lastAxisValues[axis.Key] = value;
        }
    }
    internal void SelfTestButton(byte button, bool down)
    {
        bool success = SetBtn(down, _deviceId, button);
        _log($"self-test button {button}: down={down}, result={success}");
        if (!success) throw new IOException("vJoy自己試験のボタン書込み結果を確認してください。");
        _lastButtonValues[button] = down;
    }

    private void ApplyAxis(string name, float rawValue, float fullScale, string source)
    {
        AxisState axis = _axes[name];
        float value = (rawValue - axis.Settings.CenterOffset) * axis.Settings.Gain / fullScale;
        float magnitude = Math.Abs(value);
        if (magnitude <= axis.Settings.Deadzone)
            value = 0;
        else
            value = MathF.CopySign((magnitude - axis.Settings.Deadzone) / (1 - axis.Settings.Deadzone), value);
        if (axis.Settings.Invert) value = -value;
        value = Math.Clamp(value, -1.0f, 1.0f);
        float smoothing = (float)Math.Pow(axis.Settings.Smoothing, _sampleSeconds / .01);
        axis.Smoothed = smoothing * axis.Smoothed + (1 - smoothing) * value;
        int output = (int)Math.Round(axis.Min + (axis.Smoothed + 1.0f) * 0.5f * (axis.Max - axis.Min));
        if (!SetAxis(output, _deviceId, axis.Usage))
            throw new InvalidOperationException($"SetAxis failed for {source} {name} -> vJoy {axis.Settings.Axis}.");
        axis.LastOutput = output;
        _lastAxisValues[axis.Usage] = output;
    }

    internal void Stop()
    {
        if (!_acquired) return;
        for (int button = 1; button <= Math.Min(255, GetVJDButtonNumber(_deviceId)); button++)
            _log($"stop button {button}: result={SetBtn(false, _deviceId, checked((byte)button))}");
        foreach (var axis in _enabledAxes)
        {
            int center = (int)Math.Round((axis.Value.Min + (double)axis.Value.Max) / 2);
            _log($"stop axis 0x{axis.Key:X2}: value={center}, result={SetAxis(center, _deviceId, axis.Key)}");
        }
        foreach (AxisState axis in _axes.Values) axis.LastOutput = axis.Center;
        _buttonStates.Clear();
        RelinquishVJD(_deviceId);
        _acquired = false;
        _log($"vJoy device {_deviceId} relinquish completed; status={StatusName(GetVJDStatus(_deviceId))}.");
    }

    public void Dispose() => Stop();

    private IEnumerable<(string Name, AxisConfig Config)> ConfiguredAxes()
    {
        yield return ("X", _config.Gyro.X);
        yield return ("Y", _config.Gyro.Y);
        yield return ("Z", _config.Gyro.Z);
        yield return ("AccelX", new AxisConfig { Axis = "X", Deadzone = 0 });
        yield return ("AccelY", new AxisConfig { Axis = "Y", Deadzone = 0 });
        yield return ("AccelZ", new AxisConfig { Axis = "Z", Deadzone = 0 });
        yield return ("Yaw", new AxisConfig { Axis = "Slider1", Deadzone = 0 });
        yield return ("TiltPitch", _config.Tilt.Pitch);
    }

    private static bool TryGetUsage(string axis, out uint usage)
    {
        usage = axis.Trim().ToUpperInvariant() switch
        {
            "X" => 0x30,
            "Y" => 0x31,
            "Z" => 0x32,
            "RX" => 0x33,
            "RY" => 0x34,
            "RZ" => 0x35,
            "SLIDER1" => 0x36,
            "SLIDER2" => 0x37,
            _ => 0
        };
        return usage != 0;
    }

    private static string StatusName(int status) => status switch
    {
        0 => "owned",
        1 => "free",
        2 => "busy",
        3 => "missing",
        _ => $"unknown({status})"
    };

    internal sealed class TiltEstimator
    {
        private const double Gravity = 9.80665;
        private bool _initialized;
        private double _neutralRoll;
        private double _neutralPitch;
        private double _roll;
        private double _pitch;
        private long _lastTimestamp;

        internal bool IsInitialized => _initialized;
        internal double RollDegrees => _roll * 180.0 / Math.PI;
        internal double PitchDegrees => _pitch * 180.0 / Math.PI;

        internal void Reset()
        {
            _initialized = false;
            _neutralRoll = 0;
            _neutralPitch = 0;
            _roll = 0;
            _pitch = 0;
            _lastTimestamp = 0;
        }

        internal void ResumeClock() => _lastTimestamp = 0;

        internal (double Roll, double Pitch) Update(float[] gyro, float[] accel, double correctionTimeConstant, long timestamp)
        {
            double x = accel[0];
            double y = accel[1];
            double z = accel[2];
            // VADERの左右傾斜をRoll、前後傾斜をPitchとして扱う。
            double accelRoll = Math.Atan2(-x, Math.Sqrt(y * y + z * z));
            double accelPitch = Math.Atan2(z, y);
            long now = timestamp == 0 ? Stopwatch.GetTimestamp() : timestamp;

            if (!_initialized)
            {
                _neutralRoll = accelRoll;
                _neutralPitch = accelPitch;
                _lastTimestamp = now;
                _initialized = true;
                return (0, 0);
            }

            double elapsed = _lastTimestamp == 0 ? 0 : (double)(now - _lastTimestamp) / Stopwatch.Frequency;
            _lastTimestamp = now;
            double dt = Math.Clamp(elapsed, 0, 0.1);
            _roll = WrapAngle(_roll - gyro[2] * dt);
            _pitch = WrapAngle(_pitch - gyro[0] * dt);

            double accelMagnitude = Math.Sqrt(x * x + y * y + z * z);
            if (accelMagnitude is >= Gravity * 0.75 and <= Gravity * 1.25)
            {
                double alpha = Math.Exp(-dt / correctionTimeConstant);
                double measuredRoll = WrapAngle(accelRoll - _neutralRoll);
                double measuredPitch = WrapAngle(accelPitch - _neutralPitch);
                _roll = WrapAngle(_roll + (1 - alpha) * WrapAngle(measuredRoll - _roll));
                _pitch = WrapAngle(_pitch + (1 - alpha) * WrapAngle(measuredPitch - _pitch));
            }

            return (_roll, _pitch);
        }

        private static double WrapAngle(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }
    }

    private sealed class AxisState(uint usage, int min, int max, AxisConfig settings)
    {
        internal uint Usage { get; } = usage;
        internal int Min { get; } = min;
        internal int Max { get; } = max;
        internal int Center => (int)Math.Round((Min + (double)Max) / 2);
        internal AxisConfig Settings { get; } = settings;
        internal float Smoothed { get; set; }
        internal int LastOutput { get; set; }
    }

    private const string VJoyLibrary = "vJoyInterface";

    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool vJoyEnabled();
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDStatus(uint id);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int GetOwnerPid(uint id);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int GetVJDButtonNumber(uint id);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetVJDAxisExist(uint id, uint axis);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVJDAxisMin(uint id, uint axis, out int value);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVJDAxisMax(uint id, uint axis, out int value);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AcquireVJD(uint id);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern void RelinquishVJD(uint id);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetAxis(int value, uint id, uint axis);
    [DllImport(VJoyLibrary, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetBtn([MarshalAs(UnmanagedType.Bool)] bool value, uint id, byte button);
}
