using System.Buffers.Binary;

namespace NoirMediaPlayer.Tests;

// Authored synthetic sectors, no disc content, decryption material or library fixtures.
internal static class DvdNavFixture
{
    internal const int Pci = 45, Hli = Pci + 96, Dsi = 1031;
    internal static byte[] Create(int groups = 1)
    {
        var data = new byte[2048];
        data[2] = 1; data[3] = 0xba;
        data[4] = 0x44; data[6] = 4; data[8] = 4; data[9] = 1; data[12] = 3; data[13] = 0xf8;
        Packet(data, 14, 0xbb, 18);
        Packet(data, 38, 0xbf, 980);
        Packet(data, 1024, 0xbf, 1018); data[1030] = 1;
        U32(data, Pci, 100); U32(data, Dsi + 4, 100);
        U32(data, Pci + 12, 90_000); U32(data, Pci + 16, 135_000);
        U32(data, Dsi + 8, 9); U16(data, Dsi + 24, 1); data[Dsi + 27] = 2;
        U32(data, Dsi + 314, 0x8000000a); U32(data, Dsi + 318, 0x3fffffff);
        U16(data, Hli, 1);
        U32(data, Hli + 2, 90_000); U32(data, Hli + 6, 360_000); U32(data, Hli + 10, 270_000);
        data[Hli + 14] = (byte)(groups << 4);
        if (groups > 1) data[Hli + 15] = 0x10; // group 2 is wide
        if (groups > 2) data[Hli + 15] |= 2; // group 3 is letterbox
        data[Hli + 17] = data[Hli + 18] = 2;
        U32(data, Hli + 22, 0x1234abcd); U32(data, Hli + 26, 0x4321dcba);
        for (var group = 0; group < groups; group++)
        {
            Button(data, group * (36 / groups), 10 + group * 100, 20, 50 + group * 100, 40, 0, 2, 0, 2);
            Button(data, group * (36 / groups) + 1, 60 + group * 100, 20, 90 + group * 100, 40, 1, 0, 1, 0);
        }
        return data;
    }
    internal static int ButtonOffset(int slot) => Hli + 46 + slot * 18;
    internal static void Command(byte[] data, int slot, ulong command) => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(ButtonOffset(slot) + 10), command);
    internal static void U16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
    internal static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    internal static void Sector(byte[] data, uint sector) { U32(data, Pci, sector); U32(data, Dsi + 4, sector); }
    private static void Packet(byte[] data, int offset, byte code, ushort size)
    {
        data[offset + 2] = 1; data[offset + 3] = code; U16(data, offset + 4, size);
    }
    private static void Button(byte[] data, int slot, int left, int top, int right, int bottom, int up, int down, int previous, int next)
    {
        var offset = ButtonOffset(slot);
        var x = (1 << 22) | (left << 12) | right;
        var y = (top << 12) | bottom;
        data[offset] = (byte)(x >> 16); data[offset + 1] = (byte)(x >> 8); data[offset + 2] = (byte)x;
        data[offset + 3] = (byte)(y >> 16); data[offset + 4] = (byte)(y >> 8); data[offset + 5] = (byte)y;
        data[offset + 6] = (byte)up; data[offset + 7] = (byte)down; data[offset + 8] = (byte)previous; data[offset + 9] = (byte)next;
    }
}
