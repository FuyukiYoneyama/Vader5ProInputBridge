using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

internal sealed class ResidentSettings
{
    public bool StartOnLogin { get; set; }
    public bool LaunchOpenTrack { get; set; }
    public bool OpenTrackOutputEnabled { get; set; } = true;
    public bool UseWriteFile { get; set; } = true;
    public string OpenTrackPath { get; set; } = @"C:\Program Files (x86)\opentrack\opentrack.exe";

    internal static ResidentSettings Load(string root)
    {
        string path = Path.Combine(root, "config", "application.json");
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<ResidentSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("常駐設定を確認してください。")
            : new ResidentSettings();
        settings.StartOnLogin = LoginStartup.IsEnabled;
        return settings;
    }

    internal void Save(string root)
    {
        if (LaunchOpenTrack && (!File.Exists(OpenTrackPath) || !Path.GetExtension(OpenTrackPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("OpenTrackの実行ファイルを指定してください。");
        string path = Path.Combine(root, "config", "application.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        object? previousStartup = LoginStartup.Value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            LoginStartup.Set(StartOnLogin);
            try { File.Move(temporary, path, true); }
            catch { LoginStartup.Restore(previousStartup); throw; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal void StartOpenTrack()
    {
        if (!LaunchOpenTrack) return;
        if (!File.Exists(OpenTrackPath)) throw new FileNotFoundException("OpenTrackの実行ファイルを設定してください。", OpenTrackPath);
        string expected = Path.GetFullPath(OpenTrackPath);
        int sessionId = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expected)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId == sessionId && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) return;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        using var started = Process.Start(new ProcessStartInfo(expected) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(expected)! });
    }
}

internal static class LoginStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "VADERBridge";
    internal static object? Value { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name); } }
    internal static bool IsEnabled => string.Equals(Value as string, Command, StringComparison.OrdinalIgnoreCase);
    private static string Command => $"\"{Environment.ProcessPath}\"";
    internal static void Set(bool enabled) => Restore(enabled ? Command : null);
    internal static void Restore(object? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, true);
        if (value is null) key.DeleteValue(Name, false);
        else key.SetValue(Name, value, RegistryValueKind.String);
    }
}
