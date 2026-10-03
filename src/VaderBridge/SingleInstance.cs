using System.Runtime.InteropServices;
using System.Security.Principal;

internal static class SingleInstance
{
    private static string Identity { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? Environment.UserName; } }
    internal static uint ShowMessage { get; } = RegisterWindowMessage("VADERBridge.Show." + Identity);
    internal static Mutex Enter(out bool first) => new(true, "Local\\VADERBridge-" + Identity, out first);
    internal static void ShowExisting() => PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
