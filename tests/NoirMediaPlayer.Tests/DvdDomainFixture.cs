using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Tests;

// Synthetic navigation metadata only. No disc video, keys or third-party fixtures.
internal static class DvdDomainFixture
{
    internal const int ManagerMenus = 4096, SetMenus = 6144, FirstPlay = 512;
    internal static void Write(string root, ulong[]? titlePre = null, ulong[]? managerPre = null,
        ulong[]? managerPost = null, ulong[]? rootPre = null, bool extraLanguage = false, bool secondSet = false, bool firstPlay = true,
        ulong[]? titlePost = null, ulong[]? titleCell = null)
    {
        DvdFixture.Write(root);
        var folder = Path.Combine(root, "VIDEO_TS");
        var manager = new byte[8192];
        File.ReadAllBytes(Path.Combine(folder, "VIDEO_TS.IFO")).CopyTo(manager, 0);
        DvdFixture.U32(manager, 0x80, 1023);
        if (firstPlay)
        {
            DvdFixture.U32(manager, 0x84, FirstPlay);
            Pgc(0, pre: [0x3006000000420000], empty: true).CopyTo(manager, FirstPlay);
        }
        DvdFixture.U32(manager, 0xc8, 2);
        var managerTable = Languages(extraLanguage ? [0x656e, 0x6672] : [0x656e],
            [(0x82, Pgc(10, managerPre, managerPost, next: 2)), (0x83, Pgc(30, previous: 1, goUp: 1))]);
        managerTable.CopyTo(manager, ManagerMenus);
        var set = new byte[8192]; File.ReadAllBytes(Path.Combine(folder, "VTS_01_0.IFO")).CopyTo(set, 0);
        if (titlePre is { Length: > 0 } || titlePost is { Length: > 0 } || titleCell is { Length: > 0 })
        {
            var command = Commands(titlePre ?? [], titlePost ?? [], titleCell ?? []);
            DvdFixture.U16(set, DvdFixture.Chain + 228, 324);
            command.CopyTo(set, DvdFixture.Chain + 324);
            DvdFixture.U32(set, DvdFixture.ChainTable + 4, (uint)(16 + 324 + command.Length - 1));
        }
        DvdFixture.U32(set, 0xd0, 3);
        Languages([0x656e], [(0x83, Pgc(20, rootPre, next: 2)), (0x85, Pgc(40, previous: 1, goUp: 1))]).CopyTo(set, SetMenus);
        if (secondSet)
        {
            DvdFixture.U16(manager, 0x3e, 2); DvdFixture.U16(manager, DvdFixture.TitleTable, 2);
            DvdFixture.U32(manager, DvdFixture.TitleTable + 4, 31);
            Array.Copy(manager, DvdFixture.TitleTable + 8, manager, DvdFixture.TitleTable + 20, 12);
            manager[DvdFixture.TitleTable + 26] = 2;
            File.WriteAllBytes(Path.Combine(folder, "VTS_02_0.IFO"), set);
        }
        File.WriteAllBytes(Path.Combine(folder, "VIDEO_TS.IFO"), manager);
        File.WriteAllBytes(Path.Combine(folder, "VTS_01_0.IFO"), set);
    }

    internal static byte[] Pgc(uint sector, ulong[]? pre = null, ulong[]? post = null,
        int next = 0, int previous = 0, int goUp = 0, bool empty = false)
    {
        pre ??= []; post ??= [];
        var commandTable = Commands(pre, post, []);
        var baseSize = empty ? 236 : 268;
        var bytes = new byte[baseSize + (pre.Length + post.Length == 0 ? 0 : commandTable.Length)];
        if (!empty)
        {
            bytes[2] = bytes[3] = 1; bytes[6] = 1; bytes[7] = 0x40;
            DvdFixture.U16(bytes, 230, 236); DvdFixture.U16(bytes, 232, 240); DvdFixture.U16(bytes, 234, 264);
            bytes[236] = 1; bytes[246] = 1; bytes[247] = 0x40;
            DvdFixture.U32(bytes, 248, sector); DvdFixture.U32(bytes, 256, sector + 5); DvdFixture.U32(bytes, 260, sector + 9);
            DvdFixture.U16(bytes, 264, 1); bytes[267] = 1;
        }
        DvdFixture.U16(bytes, 156, next); DvdFixture.U16(bytes, 158, previous); DvdFixture.U16(bytes, 160, goUp);
        if (pre.Length + post.Length != 0) { DvdFixture.U16(bytes, 228, baseSize); commandTable.CopyTo(bytes, baseSize); }
        return bytes;
    }
    private static byte[] Commands(ulong[] pre, ulong[] post, ulong[] cell)
    {
        var rows = pre.Concat(post).Concat(cell).ToArray(); var bytes = new byte[8 + rows.Length * 8];
        DvdFixture.U16(bytes, 0, pre.Length); DvdFixture.U16(bytes, 2, post.Length); DvdFixture.U16(bytes, 4, cell.Length);
        DvdFixture.U16(bytes, 6, bytes.Length - 1);
        for (var i = 0; i < rows.Length; i++) BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8 + i * 8), rows[i]);
        return bytes;
    }
    private static byte[] Table((byte Entry, byte[] Pgc)[] chains)
    {
        var start = 8 + chains.Length * 8; var bytes = new byte[start + chains.Sum(chain => chain.Pgc.Length)];
        DvdFixture.U16(bytes, 0, chains.Length); DvdFixture.U32(bytes, 4, (uint)(bytes.Length - 1));
        for (var i = 0; i < chains.Length; i++)
        {
            bytes[8 + i * 8] = chains[i].Entry; DvdFixture.U32(bytes, 12 + i * 8, (uint)start);
            chains[i].Pgc.CopyTo(bytes, start); start += chains[i].Pgc.Length;
        }
        return bytes;
    }
    private static byte[] Languages(ushort[] languages, (byte Entry, byte[] Pgc)[] chains)
    {
        var table = Table(chains); var start = 8 + languages.Length * 8; var bytes = new byte[start + languages.Length * table.Length];
        DvdFixture.U16(bytes, 0, languages.Length); DvdFixture.U32(bytes, 4, (uint)(bytes.Length - 1));
        for (var i = 0; i < languages.Length; i++)
        {
            DvdFixture.U16(bytes, 8 + i * 8, languages[i]); bytes[11 + i * 8] = 0xff;
            DvdFixture.U32(bytes, 12 + i * 8, (uint)start); table.CopyTo(bytes, start); start += table.Length;
        }
        return bytes;
    }
}
