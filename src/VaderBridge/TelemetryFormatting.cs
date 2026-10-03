using System.Globalization;

internal static class TelemetryFormatting
{
    internal static string SensorValue(float value) =>
        value.ToString("+00.0000;-00.0000; 00.0000", CultureInfo.InvariantCulture);

    internal static string AngleDegrees(double value) =>
        value.ToString("+000.0;-000.0; 000.0", CultureInfo.InvariantCulture);

    internal static string NormalizedAxis(float value) =>
        value.ToString("+0.0000;-0.0000; 0.0000", CultureInfo.InvariantCulture);

    internal static string AxisOutput(string axis, int output, float normalized)
    {
        string name = axis.PadRight(2);
        string outputText = output.ToString("D5", CultureInfo.InvariantCulture);
        return string.Concat(name, "=", outputText, " (", NormalizedAxis(normalized), ")");
    }

    internal static string Vector(float[] value) =>
        string.Concat(
            "X=", SensorValue(value[0]),
            ", Y=", SensorValue(value[1]),
            ", Z=", SensorValue(value[2]));
}
