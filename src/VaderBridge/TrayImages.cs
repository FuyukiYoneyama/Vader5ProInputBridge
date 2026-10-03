using System.Reflection;

internal sealed class TrayImages : IDisposable
{
    internal Icon Application { get; } = Load("VADERBridge", 48);
    internal Icon Starting { get; } = Load("Starting");
    internal Icon Ready { get; } = Load("Ready");
    internal Icon Waiting { get; } = Load("Waiting");
    internal Icon Error { get; } = Load("Error");
    private static Icon Load(string name, int? size = null)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"VADERBridge.Icons.{name}.ico")
            ?? throw new InvalidDataException("Bridgeのアイコン資源を確認してください。");
        int pixels = size ?? SystemInformation.SmallIconSize.Width;
        using var icon = new Icon(stream, pixels, pixels);
        return (Icon)icon.Clone();
    }
    public void Dispose() { Application.Dispose(); Starting.Dispose(); Ready.Dispose(); Waiting.Dispose(); Error.Dispose(); }
}
