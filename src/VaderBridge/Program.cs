using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    private const string VJoyLibrary = "vJoyInterface";
    private static readonly string VJoyPath = ReadVJoyPath();

    [STAThread]
    private static void Main(string[] args)
    {
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly, (name, _, _) => name switch
        {
            VJoyLibrary when File.Exists(VJoyPath) => NativeLibrary.Load(VJoyPath),
            _ => IntPtr.Zero
        });
        ApplicationConfiguration.Initialize();
        using var instance = SingleInstance.Enter(out bool first);
        if (!first) { SingleInstance.ShowExisting(); return; }
        try
        {
            var session = InputTools.Session.FromArguments(args);
            using var context = new ResidentApplicationContext(new BridgeDashboardForm(session, ResidentSettings.Load(session.Root)));
            Application.Run(context);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "VADER Bridge 起動結果", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { instance.ReleaseMutex(); }
    }

    private static string ReadVJoyPath()
    {
        string? environmentPath = Environment.GetEnvironmentVariable("VADER_VJOY_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath)) return environmentPath;
        string? configured = ReadRuntimePath("vjoyLibraryPath");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", "vJoyInterface.dll");
    }

    private static string? ReadRuntimePath(string property)
    {
        string rootPath = InputTools.Session.FromArguments(Array.Empty<string>()).Root;
        string configPath = Path.Combine(rootPath, "config", "bridge.json");
        if (!File.Exists(configPath)) return null;
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(configPath));
        string? configured = config.RootElement.GetProperty("runtime").GetProperty(property).GetString();
        return string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured, rootPath);
    }

    internal static string ConfigPath => Path.Combine(InputTools.Session.FromArguments(Array.Empty<string>()).Root, "config", "bridge.json");

}
