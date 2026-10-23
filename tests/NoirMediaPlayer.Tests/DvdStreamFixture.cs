using System.IO;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.Tests;

// Original synthetic NAV/PES packs with recognizable payloads; no disc content.
internal static class DvdStreamFixture
{
    internal static readonly DvdPgcKey TitleKey = new(DvdPlaybackDomain.Title, 1, 0, 1);
    internal static readonly DvdPgcKey ManagerKey = new(DvdPlaybackDomain.ManagerMenu, 0, 0x656e, 1);
    internal static readonly DvdPgcKey SetMenuKey = new(DvdPlaybackDomain.TitleSetMenu, 1, 0x656e, 1);

    internal static byte[] Nav(uint sector, uint end, uint next = 0x3fffffff, int cell = 1, int vob = 1)
    {
        var bytes = DvdNavFixture.Create(); DvdNavFixture.Sector(bytes, sector);
        DvdNavFixture.U32(bytes, DvdNavFixture.Dsi + 8, end);
        DvdNavFixture.U32(bytes, DvdNavFixture.Dsi + 314, next);
        DvdNavFixture.U16(bytes, DvdNavFixture.Dsi + 24, (ushort)vob); bytes[DvdNavFixture.Dsi + 27] = (byte)cell;
        return bytes;
    }

    internal static byte[] Media(byte marker = 0x25, byte stream = 0xe0)
    {
        var bytes = new byte[2048];
        DvdNavFixture.Create().AsSpan(0, 14).CopyTo(bytes);
        bytes[16] = 1; bytes[17] = stream; DvdNavFixture.U16(bytes, 18, 2028);
        bytes[20] = 0x80; // MPEG-2 PES, no scrambling, PTS or optional bytes.
        bytes.AsSpan(23).Fill(marker);
        return bytes;
    }

    internal static void Vob(string path, int sectors, params (int Sector, byte[] Bytes)[] writes)
    {
        using var file = File.Create(path);
        file.SetLength((long)sectors * 2048);
        foreach (var (sector, bytes) in writes) { file.Position = (long)sector * 2048; file.Write(bytes); }
    }

    internal static void Unit(string path, uint start, uint end, uint next = 0x3fffffff, int fileSectors = 400)
    {
        var writes = new List<(int, byte[])> { ((int)start, Nav(start, end, next)) };
        for (var i = 1; i <= end; i++) writes.Add(((int)start + i, Media((byte)i)));
        Vob(path, fileSectors, writes.ToArray());
    }

    internal static void WithTitle(Action<string, DvdNavigationCatalog, DvdDomainState> test) =>
        DvdNavigationCatalogTests.WithDisc(root =>
        {
            DvdDomainFixture.Write(root);
            var catalog = DvdNavigationCatalog.Read(root);
            var state = new DvdDomainResolver(catalog).ObserveCell(TitleKey, 1, 100, new DvdVmState().WithGeneral(0, 7), 1);
            test(Path.Combine(root, "VIDEO_TS"), catalog, state);
        });
}
