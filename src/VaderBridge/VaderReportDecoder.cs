// Copyright (c) 2026 FUYUKI YONEYAMA. This C# implementation is MIT licensed.
// Flydigi field mappings and sensor conversions are adapted from SDL's
// SDL_hidapi_flydigi.c, Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>.
// The SDL-origin portions retain the Zlib notice in licenses/SDL-LICENSE.txt.
// This is an adapted implementation; see THIRD_PARTY_NOTICES.md for its source.
using System.Buffers.Binary;

internal sealed record VaderStandardInput(short LX, short LY, short RX, short RY, byte LT, byte RT, uint Pov);
internal sealed record VaderInput(bool[] Buttons, float[] Gyro, float[] Accel, VaderStandardInput Standard);

internal static class VaderReportDecoder
{
    // SDLと同じく、先頭の任意のレポートIDと、IDを省いた形式を識別する。
    internal static bool TryGetCommand(ReadOnlySpan<byte> report, out int offset, out byte command)
    {
        offset = report.Length > 0 && report[0] == 0x5A ? 0 : 1;
        command = 0;
        if (report.Length - offset < 3 || report[offset] != 0x5A || report[offset + 1] != 0xA5) return false;
        command = report[offset + 2];
        return true;
    }

    internal static bool TryDecode(byte[] report, out VaderInput? input)
    {
        input = null;
        if (!TryGetCommand(report, out int offset, out byte command) || command != 0xEF || report.Length - offset < 31) return false;
        var buttons = new bool[20]; byte extra = report[offset + 13];
        byte b11 = report[offset + 11], b12 = report[offset + 12], b14 = report[offset + 14];
        buttons[0] = (b11 & 0x10) != 0; buttons[1] = (b11 & 0x20) != 0;
        buttons[2] = (b11 & 0x80) != 0; buttons[3] = (b12 & 0x01) != 0;
        buttons[4] = (b11 & 0x40) != 0; buttons[5] = (b14 & 0x08) != 0;
        buttons[6] = (b12 & 0x02) != 0; buttons[7] = (b12 & 0x40) != 0;
        buttons[8] = (b12 & 0x80) != 0; buttons[9] = (b12 & 0x04) != 0; buttons[10] = (b12 & 0x08) != 0;
        int[] masks = { 4, 8, 16, 32, 1, 2, 64, 128 };
        for (int i = 0; i < masks.Length; i++) buttons[11 + i] = (extra & masks[i]) != 0;
        buttons[19] = (report[offset + 14] & 1) != 0;
        short Read(int index) => BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(offset + index, 2));
        float G(int value) => (float)(value / 32768.0 * 2000 * Math.PI / 180);
        float A(int value) => (float)(value * 9.80665 / 4096);
        uint pov = (b11 & 15) switch { 1 => 0, 3 => 4500, 2 => 9000, 6 => 13500, 4 => 18000, 12 => 22500, 8 => 27000, 9 => 31500, _ => uint.MaxValue };
        input = new(buttons, new[] { G(Read(17)), G(Read(21)), G(-Read(19)) }, new[] { A(Read(23)), A(Read(27)), A(-Read(25)) },
            new(Read(3), Read(5), Read(7), Read(9), report[offset + 15], report[offset + 16], pov));
        return true;
    }
}
