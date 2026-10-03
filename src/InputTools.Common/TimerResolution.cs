using System.Runtime.InteropServices;

namespace InputTools;

public sealed class TimerResolution : IDisposable
{
    public uint Result { get; } = timeBeginPeriod(1);
    public void Dispose() { if (Result == 0) timeEndPeriod(1); }
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
