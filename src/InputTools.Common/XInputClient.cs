using System.Runtime.InteropServices;

namespace InputTools;

public static class XInputClient
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct State { public uint PacketNumber; public Gamepad Gamepad; }
    public static readonly (string Name, ushort Mask)[] Buttons =
    {
        ("↑", 0x0001), ("↓", 0x0002), ("←", 0x0004), ("→", 0x0008), ("Start", 0x0010), ("Back", 0x0020),
        ("LS", 0x0040), ("RS", 0x0080), ("LB", 0x0100), ("RB", 0x0200), ("A", 0x1000), ("B", 0x2000), ("X", 0x4000), ("Y", 0x8000)
    };
    public static double NormalizeStick(short value) => value < 0 ? value / 32768.0 : value / 32767.0;
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static extern uint GetState(uint slot, out State state);
}
