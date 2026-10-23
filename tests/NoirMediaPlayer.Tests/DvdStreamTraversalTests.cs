using System.IO;
using System.Security.Cryptography;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdStreamTraversalTests
{
    [Fact]
    public void FollowsForwardNavLinksAcrossVobPartsAndPreservesEveryStoredByte() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var first = Path.Combine(folder, "VTS_01_1.VOB"); var second = Path.Combine(folder, "VTS_01_2.VOB");
        var nav = DvdStreamFixture.Nav(100, 9, 0x8000000a);
        var expected = nav.Concat(Enumerable.Range(1, 9).SelectMany(i => DvdStreamFixture.Media((byte)i))).ToArray();
        DvdStreamFixture.Vob(first, 105, (100, expected[..(5 * 2048)]));
        DvdStreamFixture.Vob(second, 295, (0, expected[(5 * 2048)..]), (5, DvdStreamFixture.Nav(110, 89)));
        using (var file = File.Open(second, FileMode.Open, FileAccess.Write))
            for (var sector = 111; sector <= 199; sector++) { file.Position = (sector - 105) * 2048; file.Write(DvdStreamFixture.Media()); }
        var original = SHA256.HashData(File.ReadAllBytes(first).Concat(File.ReadAllBytes(second)).ToArray());
        var traversal = new DvdStreamTraversal(catalog);
        var read = traversal.Read(state); Ready(read); Assert.Same(state, read.State);
        Assert.Equal(expected, read.Unit!.Bytes.ToArray()); Assert.Null(read.Unit.Boundary);
        Assert.Equal(110u, read.Next!.Position.Sector); Assert.Equal(state.Epoch, read.Next.Epoch);
        Assert.Same(state.Registers, read.Next.Registers); Assert.Same(state.Title, read.Next.Title);
        var last = traversal.Read(read.Next); Ready(last); Assert.Null(last.Next);
        Assert.NotNull(last.Unit!.Boundary); Assert.Equal(90 * 2048, last.Unit.ByteCount); Assert.False(last.PlaybackVerified);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(first).Concat(File.ReadAllBytes(second)).ToArray()));
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (File.Open(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    });

    [Fact]
    public void ForwardLinksCanSkipUnusedSectorsWithoutScanningThem() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_1.VOB"), 100, 1, 0x80000014);
        var traversal = new DvdStreamTraversal(catalog); var read = traversal.Read(state); Ready(read);
        Assert.Equal(2 * 2048, read.Unit!.ByteCount); Assert.Equal(120u, read.Next!.Position.Sector);
        // Zero-filled gap has not been scanned or decoded; its destination must
        // still independently prove that it is the expected NAV pack.
        Failed(traversal.Read(read.Next), read.Next);
    });

    [Theory]
    [InlineData("manager")] [InlineData("set")] [InlineData("title")] [InlineData("first")]
    public void DomainsSelectOnlyTheirOwnVobSectorSpace(string domain) => DvdStreamFixture.WithTitle((folder, catalog, titleState) =>
    {
        DvdStreamFixture.Unit(Path.Combine(folder, "VIDEO_TS.VOB"), 10, 9, fileSectors: 50);
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_0.VOB"), 20, 9, fileSectors: 50);
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_1.VOB"), 100, 99);
        var resolver = new DvdDomainResolver(catalog);
        var state = domain switch
        {
            "manager" => resolver.Start().State,
            "set" => resolver.ObserveCell(DvdStreamFixture.SetMenuKey, 1, 20, new()),
            "title" => titleState,
            _ => FirstPlayCell(catalog, out catalog)
        };
        var read = new DvdStreamTraversal(catalog).Read(state); Ready(read);
        Assert.Equal(state.Position.Sector, read.Unit!.Navigation.Sector);
        Assert.Null(read.Next); Assert.NotNull(read.Unit.Boundary); Assert.Equal(7, titleState.Registers.General[0]);
    });

    [Fact]
    public void MenuCallAndResumeReopenTheCorrectDomainWithoutLosingRegisters() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_1.VOB"), 100, 99);
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_0.VOB"), 20, 9, fileSectors: 50);
        var resolver = new DvdDomainResolver(catalog); var traversal = new DvdStreamTraversal(catalog);
        Ready(traversal.Read(state));
        var called = resolver.Execute(state, [new(0x3008000000830000)]);
        Assert.Equal(DvdDomainStatus.Resolved, called.Status); Ready(traversal.Read(called.State));
        var resumed = resolver.Execute(called.State, [new(0x2001000000000010)]);
        Assert.Equal(DvdDomainStatus.Resolved, resumed.Status); var read = traversal.Read(resumed.State); Ready(read);
        Assert.Equal(100u, read.Unit!.Navigation.Sector); Assert.Equal(7, read.State.Registers.General[0]);
        Assert.Same(called.State.Resume, resumed.State.Resume);
    });

    [Fact]
    public void CellBoundaryRetainsStillAndCommandRequirementsWithoutExecutingThem() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var chain = catalog.Chains.Single(c => c.Key == state.Position.Chain);
        var cells = chain.Cells.ToArray(); cells[0] = cells[0] with { StillSeconds = 255, CommandNumber = 1 };
        var changed = chain with { Cells = Array.AsReadOnly(cells), StillSeconds = 9, CellCommands = new[] { new DvdButtonCommand(0x7100000000630000) } };
        var custom = Replace(catalog, changed);
        var observed = new DvdDomainResolver(custom).ObserveCell(chain.Key, 1, 100, state.Registers, 1);
        DvdStreamFixture.Unit(Path.Combine(folder, "VTS_01_1.VOB"), 100, 99);
        var read = new DvdStreamTraversal(custom).Read(observed); Ready(read);
        Assert.Equal(new DvdCellBoundary(255, 0, 1, true), read.Unit!.Boundary); Assert.Null(read.Next);
        Assert.Equal(7, read.State.Registers.General[0]); Assert.Equal(100u, read.State.Position.Sector);
        // A PGC still belongs only to its last cell; it is not applied after each VOBU/cell.
        var oneCell = changed with { Programs = new[] { 1 }, Cells = new[] { cells[0] } };
        custom = Replace(catalog, oneCell); observed = new DvdDomainResolver(custom).ObserveCell(chain.Key, 1, 100, state.Registers, 1);
        read = new DvdStreamTraversal(custom).Read(observed); Ready(read);
        Assert.Equal(9, read.Unit!.Boundary!.ProgramChainStillSeconds);
    });

    [Fact]
    public void NavOnlyVobuIsValidAtCellEnd() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_1.VOB"), 400, (199, DvdStreamFixture.Nav(199, 0)));
        var observed = new DvdDomainResolver(catalog).ObserveCell(state.Position.Chain, 1, 199, state.Registers, 1);
        var read = new DvdStreamTraversal(catalog).Read(observed); Ready(read);
        Assert.Equal(2048, read.Unit!.ByteCount); Assert.Null(read.Next); Assert.NotNull(read.Unit.Boundary);
    });

    [Theory]
    [InlineData(0u)] [InlineData(10u)] [InlineData(0x80000000u)] [InlineData(0x80000009u)]
    [InlineData(0xc000000au)] [InlineData(0x80000064u)] [InlineData(0xffffffffu)] [InlineData(0x3fffffffu)]
    public void InvalidUnavailableOverlappingOrPrematureEndLinksReturnNoBytes(uint next) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_1.VOB"), 400, (100, DvdStreamFixture.Nav(100, 9, next)));
        Failed(new DvdStreamTraversal(catalog).Read(state), state);
    });

    [Theory]
    [InlineData("lbn")] [InlineData("vob")] [InlineData("cell")] [InlineData("span")] [InlineData("budget")]
    [InlineData("stale")] [InlineData("stopped")] [InlineData("pre")] [InlineData("program")] [InlineData("registers")]
    public void WrongIdentitiesRangesAndInvalidDomainSnapshotsAreAtomicFailures(string fault) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var nav = DvdStreamFixture.Nav(100, 99);
        switch (fault)
        {
            case "lbn": DvdNavFixture.Sector(nav, 101); break;
            case "vob": DvdNavFixture.U16(nav, DvdNavFixture.Dsi + 24, 2); break;
            case "cell": nav[DvdNavFixture.Dsi + 27] = 2; break;
            case "span": DvdNavFixture.U32(nav, DvdNavFixture.Dsi + 8, 100); break;
            case "budget": DvdNavFixture.U32(nav, DvdNavFixture.Dsi + 8, 1024); break;
            case "stale": state = state with { CatalogIdentity = Guid.NewGuid() }; break;
            case "stopped": state = state with { Stopped = true }; break;
            case "pre": state = state with { Position = state.Position with { Phase = DvdNavigationPhase.PreCommands, Program = 1, Cell = 0, Sector = 0 } }; break;
            case "program": state = state with { Position = state.Position with { Program = 2 } }; break;
            case "registers": state = state with { Registers = state.Registers.WithSystem(4, 99) }; break;
        }
        DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_1.VOB"), 400, (100, nav));
        Failed(new DvdStreamTraversal(catalog).Read(state), state);
    });

    [Theory]
    [InlineData(32, 0x80)] [InlineData(32, 0x40)] [InlineData(32, 0x20)] [InlineData(32, 0x10)]
    [InlineData(33, 1)] [InlineData(34, 1)] [InlineData(43, 1)] [InlineData(180, 1)] [InlineData(233, 1)]
    public void InterleavingAndSeamlessAngleInformationCannotBeFlattenedIntoLinearPlayback(int offset, byte value) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var nav = DvdStreamFixture.Nav(100, 99); nav[DvdNavFixture.Dsi + offset] = value;
        Assert.Null(DvdNavigationPacket.Read(nav).CompatibilityReason);
        Assert.NotNull(DvdNavigationPacket.Read(nav).TraversalCompatibilityReason);
        DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_1.VOB"), 400, (100, nav));
        Failed(new DvdStreamTraversal(catalog).Read(state), state);
    });

    [Fact]
    public void NonSeamlessAnglesAndUnsupportedHighlightsKeepCompatibility() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var path = Path.Combine(folder, "VTS_01_1.VOB"); var nav = DvdStreamFixture.Nav(100, 99);
        DvdNavFixture.U32(nav, DvdNavFixture.Pci + 60, 0x80000010);
        DvdStreamFixture.Vob(path, 400, (100, nav)); Failed(new DvdStreamTraversal(catalog).Read(state), state);
        nav = DvdStreamFixture.Nav(100, 99); nav[DvdNavFixture.Hli + 16] = 1;
        DvdStreamFixture.Vob(path, 400, (100, nav)); Failed(new DvdStreamTraversal(catalog).Read(state), state);
    });

    [Theory]
    [InlineData("scramble1")] [InlineData("scramble2")] [InlineData("scramble3")]
    [InlineData("prefix")] [InlineData("length")] [InlineData("unbounded")] [InlineData("mpeg1")]
    [InlineData("optional")] [InlineData("nested")] [InlineData("unknown")] [InlineData("stuffing")]
    public void ProtectedAndMalformedPayloadPacksDoNotExposePartialUnits(string fault) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var path = Path.Combine(folder, "VTS_01_1.VOB");
        var media = DvdStreamFixture.Media();
        switch (fault)
        {
            case "scramble1": media[20] |= 0x10; break;
            case "scramble2": media[20] |= 0x20; break;
            case "scramble3": media[20] |= 0x30; break;
            case "prefix": media[2] = 2; break;
            case "length": DvdNavFixture.U16(media, 18, 65535); break;
            case "unbounded": DvdNavFixture.U16(media, 18, 0); break;
            case "mpeg1": media[20] = 0x0f; break;
            case "optional": DvdNavFixture.U16(media, 18, 3); media[22] = 1; break;
            case "nested": media = DvdStreamFixture.Nav(101, 0); break;
            case "unknown": media[17] = 0xf0; break;
            case "stuffing": media[13] = 0xf9; media[14] = 0; break;
        }
        DvdStreamFixture.Vob(path, 400, (100, DvdStreamFixture.Nav(100, 1, 0x80000002)), (101, media));
        Failed(new DvdStreamTraversal(catalog).Read(state), state);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    });

    [Theory]
    [InlineData(0xbd)] [InlineData(0xc0)] [InlineData(0xdf)] [InlineData(0xe0)] [InlineData(0xef)]
    public void ClearPrivateAudioAndVideoPesPayloadsAreNotSearchedForNestedStartCodes(byte code) => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var media = DvdStreamFixture.Media(stream: code);
        new byte[] { 0, 0, 1, 0xbf, 0xff, 0xff, 0x30 }.CopyTo(media, 100);
        DvdStreamFixture.Vob(Path.Combine(folder, "VTS_01_1.VOB"), 400,
            (100, DvdStreamFixture.Nav(100, 1, 0x80000002)), (101, media));
        var read = new DvdStreamTraversal(catalog).Read(state); Ready(read);
        Assert.Equal(media, read.Unit!.Bytes.Slice(2048).ToArray()); Assert.Equal(102u, read.Next!.Position.Sector);
    });

    [Fact]
    public void LatePayloadFailureAndCancellationReleaseFilesAndKeepOriginalState() => DvdStreamFixture.WithTitle((folder, catalog, state) =>
    {
        var path = Path.Combine(folder, "VTS_01_1.VOB");
        // First payload succeeds, second is truncated/framing-invalid.
        DvdStreamFixture.Vob(path, 400, (100, DvdStreamFixture.Nav(100, 2, 0x80000003)), (101, DvdStreamFixture.Media()));
        var traversal = new DvdStreamTraversal(catalog); Failed(traversal.Read(state), state);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => traversal.Read(state, cancelled.Token));
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(100u, state.Position.Sector); Assert.Equal(7, state.Registers.General[0]);
    });

    private static DvdDomainState FirstPlayCell(DvdNavigationCatalog catalog, out DvdNavigationCatalog replacement)
    {
        var menu = catalog.Chains.Single(c => c.Key == DvdStreamFixture.ManagerKey);
        var first = menu with { Key = new(DvdPlaybackDomain.FirstPlay, 0, 0, 1), PreCommands = Array.Empty<DvdButtonCommand>() };
        replacement = Replace(catalog, first);
        return new DvdDomainResolver(replacement).ObserveCell(first.Key, 1, 10, new());
    }
    private static DvdNavigationCatalog Replace(DvdNavigationCatalog catalog, DvdNavigationChain chain) =>
        new(catalog.VideoTsDirectory, catalog.MetadataFiles, catalog.Chains.Select(c => c.Key == chain.Key ? chain : c), catalog.Titles);
    private static void Ready(DvdStreamRead read)
    {
        Assert.True(read.Status == DvdStreamStatus.Ready, read.Reason); Assert.NotNull(read.Unit); Assert.Null(read.Reason);
    }
    private static void Failed(DvdStreamRead read, DvdDomainState original)
    {
        Assert.Equal(DvdStreamStatus.CompatibilityRequired, read.Status); Assert.Same(original, read.State);
        Assert.Null(read.Unit); Assert.Null(read.Next); Assert.False(string.IsNullOrWhiteSpace(read.Reason));
    }
}
