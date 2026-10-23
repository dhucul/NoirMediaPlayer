using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Tests;

// Synthetic, first-party information files; no disc, media, keys or library fixture is used.
internal static class DvdFixture
{
    internal const int TitleTable = 2048, PartTable = 2048, ChainTable = 4096, Chain = ChainTable + 16;
    internal const int Programs = 236, Playback = 240, Positions = 312;

    internal static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), (ushort)value);
    internal static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    internal static void Write(string root, bool backups = false)
    {
        var folder = Path.Combine(root, "VIDEO_TS");
        Directory.CreateDirectory(folder);
        var manager = new byte[4096];
        "DVDVIDEO-VMG"u8.CopyTo(manager);
        U32(manager, 0x1c, 1); U16(manager, 0x3e, 1);
        U32(manager, 0xc4, 1);
        U16(manager, TitleTable, 1); U32(manager, TitleTable + 4, 19);
        manager[TitleTable + 9] = 1; // One angle.
        U16(manager, TitleTable + 10, 2);
        manager[TitleTable + 14] = manager[TitleTable + 15] = 1;

        var set = new byte[6144];
        "DVDVIDEO-VTS"u8.CopyTo(set);
        U32(set, 0x1c, 2); U32(set, 0xc8, 1); U32(set, 0xcc, 2);
        U16(set, PartTable, 1); U32(set, PartTable + 4, 19); U32(set, PartTable + 8, 12);
        U16(set, PartTable + 12, 1); U16(set, PartTable + 14, 1);
        U16(set, PartTable + 16, 1); U16(set, PartTable + 18, 2);
        U16(set, ChainTable, 1); U32(set, ChainTable + 4, 16 + Positions + 12 - 1);
        U32(set, ChainTable + 12, 16);
        set[Chain + 2] = 2; set[Chain + 3] = 3;
        Time(set, Chain + 4, 0x30); // 30 seconds; BCD, not binary.
        U16(set, Chain + 230, Programs); U16(set, Chain + 232, Playback); U16(set, Chain + 234, Positions);
        set[Chain + Programs] = 1; set[Chain + Programs + 1] = 3;
        for (var index = 0; index < 3; index++)
        {
            var cell = Chain + Playback + index * 24;
            Time(set, cell + 4, 0x10);
            U32(set, cell + 8, (uint)(100 + index * 100));
            U32(set, cell + 16, (uint)(180 + index * 100));
            U32(set, cell + 20, (uint)(199 + index * 100));
            U16(set, Chain + Positions + index * 4, 1);
            set[Chain + Positions + index * 4 + 3] = (byte)(index + 1);
        }
        File.WriteAllBytes(Path.Combine(folder, "VIDEO_TS.IFO"), manager);
        File.WriteAllBytes(Path.Combine(folder, "VTS_01_0.IFO"), set);
        if (backups)
        {
            File.WriteAllBytes(Path.Combine(folder, "VIDEO_TS.BUP"), manager);
            File.WriteAllBytes(Path.Combine(folder, "VTS_01_0.BUP"), set);
        }
    }

    private static void Time(byte[] bytes, int offset, byte seconds)
    {
        bytes[offset + 2] = seconds;
        bytes[offset + 3] = 0x40; // PAL, frame zero.
    }

    internal static void Edit(string root, string file, Action<byte[]> edit)
    {
        var path = Path.Combine(root, "VIDEO_TS", file);
        var data = File.ReadAllBytes(path); edit(data); File.WriteAllBytes(path, data);
    }
}
