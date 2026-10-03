using System.Text.Json;

internal sealed class BridgeConfig
{
    public RuntimeConfig Runtime { get; set; } = new();
    public DeviceConfig Device { get; set; } = new();
    public int VJoyId { get; set; } = 2;
    public bool ExtendedEnabled { get; set; }
    public Dictionary<string, int> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public GyroConfig Gyro { get; set; } = new();
    public AccelConfig Accel { get; set; } = new();
    public YawConfig Yaw { get; set; } = new();
    public OpenTrackConfig OpenTrack { get; set; } = new();
    public TiltConfig Tilt { get; set; } = new();
    public StandardOutputConfig Standard { get; set; } = new();

    internal static BridgeConfig Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<BridgeConfig>(document.RootElement.GetRawText(), options)
            ?? throw new InvalidDataException("bridge.json did not contain a configuration object.");
    }
}

internal sealed class StandardOutputConfig
{
    public bool Enabled { get; set; }
    public int VJoyId { get; set; } = 1;
    public float StickDeadzone { get; set; }
    public Dictionary<string, int> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 1, ["B"] = 2, ["X"] = 3, ["Y"] = 4, ["LB"] = 5, ["RB"] = 6,
        ["Back"] = 7, ["Start"] = 8, ["LS"] = 9, ["RS"] = 10,
        ["M1"] = 11, ["M2"] = 12, ["M3"] = 13, ["M4"] = 14,
        ["LM"] = 15, ["RM"] = 16, ["C"] = 17, ["Z"] = 18, ["Fn"] = 19, ["Guide"] = 20
    };
}

internal sealed class RuntimeConfig
{
    public string VJoyLibraryPath { get; set; } = "";
}

internal sealed class DeviceConfig
{
    public string Vid { get; set; } = "0x37D7";
    public string Pid { get; set; } = "0x2401";
}

internal sealed class GyroConfig
{
    public float FullScaleRadiansPerSecond { get; set; } = 4.0f;
    public AxisConfig X { get; set; } = new();
    public AxisConfig Y { get; set; } = new();
    public AxisConfig Z { get; set; } = new();
}

internal sealed class TiltConfig
{
    public float FullScaleDegrees { get; set; } = 45.0f;
    public float CorrectionTimeConstantSeconds { get; set; } = 0.4f;
    public AxisConfig Roll { get; set; } = new() { Axis = "X" };
    public AxisConfig Pitch { get; set; } = new() { Axis = "Slider2" };
}

internal sealed class AccelConfig
{
    public float FullScaleMetersPerSecondSquared { get; set; } = 20.0f;
}

internal sealed class YawConfig
{
    public double YawRangeDeg { get; set; } = 90;
    public string SensorAxis { get; set; } = "Y";
    public bool Invert { get; set; } = true;
    public double BiasCalibrationSeconds { get; set; } = 2;
    public int MinimumBiasSamples { get; set; } = 200;
    public double StationaryMaxRateDegPerSecond { get; set; } = 3;
    public double StationaryStdDevDegPerSecond { get; set; } = .35;
    public double StationaryGravityTolerance { get; set; } = 1.5;
    public double StationaryAccelChangeTolerance { get; set; } = .35;
    public double HomeShortPressMilliseconds { get; set; } = 600;
    public double MaxSampleGapSeconds { get; set; } = .25;
}

internal sealed class AxisConfig
{
    public string Axis { get; set; } = "";
    public bool Invert { get; set; }
    public float Gain { get; set; } = 1.0f;
    public float Deadzone { get; set; } = 0.02f;
    public float CenterOffset { get; set; }
    public float Smoothing { get; set; }
}

internal sealed class OpenTrackConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4242;
    public int SendHz { get; set; } = 100;
    public double SmoothingMilliseconds { get; set; } = 30;
    public double YawGain { get; set; } = 1;
    public double PitchGain { get; set; } = 1;
    public double RollGain { get; set; } = 1;
    public bool InvertYaw { get; set; }
    public bool InvertPitch { get; set; }
    public bool InvertRoll { get; set; }
    public double YawLimitDeg { get; set; } = 180;
    public double PitchLimitDeg { get; set; } = 89;
    public double RollLimitDeg { get; set; } = 89;
}
