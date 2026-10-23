using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdNavigationCatalogTests
{
    [Fact]
    public void ReadsRealTableLayoutsAndRetainsDomainsLanguagesCommandsAndLinks() => WithDisc(root =>
    {
        DvdDomainFixture.Write(root, titlePre: [0x7100000000070000], extraLanguage: true, secondSet: true);
        var files = Directory.GetFiles(Path.Combine(root, "VIDEO_TS")).ToDictionary(file => file, File.ReadAllBytes);
        var catalog = DvdNavigationCatalog.Read(root);
        Assert.False(catalog.PlaybackVerified); Assert.Equal(3, catalog.MetadataFiles.Count); Assert.Equal(2, catalog.Titles.Count);
        Assert.Equal(11, catalog.Chains.Count);
        var first = Assert.Single(catalog.Chains, chain => chain.Key.Domain == DvdPlaybackDomain.FirstPlay);
        Assert.Empty(first.Cells); Assert.Equal("3006000000420000", Assert.Single(first.PreCommands).Hex);
        var menu = catalog.Chains.Single(chain => chain.Key == new DvdPgcKey(DvdPlaybackDomain.ManagerMenu, 0, 0x656e, 1));
        Assert.Equal(0x82, menu.EntryId); Assert.Equal(2, menu.Next); Assert.Equal(10u, menu.Cells[0].FirstSector);
        var title = catalog.Chains.Single(chain => chain.Key == new DvdPgcKey(DvdPlaybackDomain.Title, 1, 0, 1));
        Assert.Equal(new[] { 1, 3 }, title.Programs); Assert.Equal("7100000000070000", title.PreCommands[0].Hex);
        Assert.Equal(300u, title.Cells[2].FirstSector); Assert.Equal(3, catalog.Titles[0].Chapters[1].FirstCell);
        Assert.All(files, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.NotEqual(catalog.Identity, DvdNavigationCatalog.Read(root).Identity);
    });

    [Fact]
    public void WholeFileBackupRecoveryIncludesMenuTopologyAndCommandTables() => WithDisc(root =>
    {
        DvdDomainFixture.Write(root);
        foreach (var file in Directory.GetFiles(Path.Combine(root, "VIDEO_TS"), "*.IFO")) File.Copy(file, Path.ChangeExtension(file, ".BUP"));
        DvdFixture.Edit(root, "VIDEO_TS.IFO", data => DvdFixture.U32(data, DvdDomainFixture.ManagerMenus + 12, uint.MaxValue));
        DvdFixture.Edit(root, "VTS_01_0.IFO", data => DvdFixture.U32(data, DvdDomainFixture.SetMenus + 12, uint.MaxValue));
        var catalog = DvdNavigationCatalog.Read(root);
        Assert.All(catalog.MetadataFiles, file => Assert.EndsWith(".BUP", file)); Assert.Equal(6, catalog.Chains.Count);
    });

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void RejectsMalformedDomainTablesAndCrossReferences(int fault) => WithDisc(root =>
    {
        DvdDomainFixture.Write(root, titlePre: [0]);
        if (fault <= 3) DvdFixture.Edit(root, "VIDEO_TS.IFO", data =>
        {
            if (fault == 0) DvdFixture.U32(data, 0x80, 600); // First play extends past manager info.
            if (fault == 1) DvdFixture.U32(data, DvdDomainFixture.ManagerMenus + 12, uint.MaxValue);
            if (fault == 2) DvdFixture.U16(data, DvdDomainFixture.ManagerMenus + 16, 4097);
            if (fault == 3) DvdFixture.U32(data, DvdDomainFixture.ManagerMenus + 16 + 20, 24); // Aliased PGC offsets.
        });
        else DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            if (fault == 4) DvdFixture.U16(data, DvdFixture.PartTable + 12, 2);
            if (fault == 5) DvdFixture.U16(data, DvdFixture.Chain + 324 + 6, 7);
            if (fault == 6) data[DvdFixture.Chain + DvdFixture.Playback + 3] = 1; // No cell command exists.
        });
        Assert.Throws<InvalidDataException>(() => DvdNavigationCatalog.Read(root));
    });

    [Fact]
    public void CancellationAndExplicitLocalSourceRulesArePreserved() => WithDisc(root =>
    {
        DvdDomainFixture.Write(root); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DvdNavigationCatalog.Read(root, cancelled.Token));
        Assert.Throws<ArgumentException>(() => DvdNavigationCatalog.Read("VIDEO_TS"));
        Assert.Throws<ArgumentException>(() => DvdNavigationCatalog.Read(@"\\server\disc"));
    });

    internal static void WithDisc(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDvdDomain-" + Guid.NewGuid().ToString("N"));
        try { test(root); }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("NoirDvdDomain-"))
                throw new InvalidOperationException("Unexpected synthetic disc path.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
