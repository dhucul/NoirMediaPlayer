using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

internal sealed class DvdHoldClock : TimeProvider
{
    private long value;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => value;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch; // Never used for hold timing.
    internal void Advance(TimeSpan elapsed) => value += elapsed.Ticks;
    internal void Seconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}

internal sealed record DvdCompletionCase(DvdNavigationCatalog Catalog, DvdDomainResolver Resolver, DvdDomainState State,
    DvdStreamRead Read, DvdCellPlayback Playback, DvdHoldClock Clock, string Folder)
{
    internal Guid Queue()
    {
        var result = Playback.Queue(Read);
        Assert.Equal(DvdCellPlaybackStatus.AwaitingPresentation, result.Status);
        return result.Ticket!.Value;
    }
}

internal static class DvdCompletionFixture
{
    internal static void With(Action<DvdCompletionCase> test, byte cellStill = 0, byte pgcStill = 0,
        int cellNumber = 1, ulong[]? commands = null, byte commandNumber = 1, bool menu = false,
        Func<DvdNavigationChain, DvdNavigationChain>? edit = null, uint navProhibitions = 0, bool highlight = true, ulong? buttonCommand = null)
    {
        DvdNavigationCatalogTests.WithDisc(root =>
        {
            // Read real synthetic IFO command/still fields before running the
            // model; individual tests may additionally replace a chain policy.
            DvdDomainFixture.Write(root, titleCell: commands);
            DvdFixture.Edit(root, "VTS_01_0.IFO", bytes =>
            {
                bytes[DvdFixture.Chain + 163] = pgcStill;
                bytes[DvdFixture.Chain + DvdFixture.Playback + (cellNumber - 1) * 24 + 2] = cellStill;
                bytes[DvdFixture.Chain + DvdFixture.Playback + (cellNumber - 1) * 24 + 3] = commands is { Length: > 0 } ? commandNumber : (byte)0;
            });
            var original = DvdNavigationCatalog.Read(root);
            var key = menu ? DvdStreamFixture.ManagerKey : DvdStreamFixture.TitleKey;
            var chain = original.Chains.Single(c => c.Key == key);
            if (menu)
                chain = chain with { StillSeconds = pgcStill, Cells = new[] { chain.Cells[0] with {
                    StillSeconds = cellStill, CommandNumber = commands is { Length: > 0 } ? commandNumber : (byte)0 } },
                    CellCommands = Array.AsReadOnly((commands ?? []).Select(c => new DvdButtonCommand(c)).ToArray()) };
            chain = edit?.Invoke(chain) ?? chain;
            var catalog = new DvdNavigationCatalog(original.VideoTsDirectory, original.MetadataFiles,
                original.Chains.Select(c => c.Key == key ? chain : c), original.Titles);
            var resolver = new DvdDomainResolver(catalog);
            var cell = chain.Cells[cellNumber - 1];
            var state = resolver.ObserveCell(key, cellNumber, cell.FirstSector, new DvdVmState().WithGeneral(0, 7), menu ? 0 : 1);
            var nav = DvdStreamFixture.Nav(cell.FirstSector, cell.LastSector - cell.FirstSector, cell: cell.CellId, vob: cell.VobId);
            DvdNavFixture.U32(nav, DvdNavFixture.Pci + 8, navProhibitions);
            if (buttonCommand.HasValue) DvdNavFixture.Command(nav, 0, buttonCommand.Value);
            if (!highlight) DvdNavFixture.U16(nav, DvdNavFixture.Hli, 0);
            var writes = new List<(int, byte[])> { ((int)cell.FirstSector, nav) };
            for (var sector = cell.FirstSector + 1; sector <= cell.LastSector; sector++) writes.Add(((int)sector, DvdStreamFixture.Media()));
            DvdStreamFixture.Vob(Path.Combine(catalog.VideoTsDirectory, menu ? "VIDEO_TS.VOB" : "VTS_01_1.VOB"), 400, writes.ToArray());
            var read = new DvdStreamTraversal(catalog).Read(state);
            Assert.True(read.Status == DvdStreamStatus.Ready, read.Reason);
            var clock = new DvdHoldClock();
            test(new(catalog, resolver, state, read, new(catalog, state, clock), clock, catalog.VideoTsDirectory));
        });
    }
}
