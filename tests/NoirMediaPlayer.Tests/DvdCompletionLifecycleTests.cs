using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdCompletionLifecycleTests
{
    [Fact]
    public void PendingEntryCommandsCannotBeAcceptedAsAReadyPresentationState() => DvdCompletionFixture.With(c =>
    {
        var state = c.State with { Position = c.State.Position with { Phase = DvdNavigationPhase.PreCommands, Program = 1, Cell = 0, Sector = 0 }, Epoch = 1 };
        Assert.Throws<InvalidDataException>(() => new DvdCellPlayback(c.Catalog, state));
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.ReplaceState(state).Status); Assert.Same(c.State, c.Playback.State);
    });

    [Fact]
    public void IntermediateVobuPresentationAdvancesWithinCellWithoutRunningCellCommand() => DvdCompletionFixture.With(c =>
    {
        var path = Path.Combine(c.Folder, "VTS_01_1.VOB");
        DvdStreamFixture.Vob(path, 400, (100, DvdStreamFixture.Nav(100, 1, 0x80000002)), (101, DvdStreamFixture.Media()));
        var read = new DvdStreamTraversal(c.Catalog).Read(c.State);
        var queued = c.Playback.Queue(read); Assert.Equal(DvdCellPlaybackStatus.AwaitingPresentation, queued.Status);
        var result = c.Playback.Presented(queued.Ticket!.Value);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(102u, result.State.Position.Sector);
        Assert.Equal(c.State.Epoch, result.State.Epoch); Assert.Equal(7, result.State.Registers.General[0]); Assert.Equal(0, result.InstructionsExecuted);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Queue(read).Status);
    }, cellStill: 255, commands: [0x7300000000010000]);

    [Fact]
    public void AFullTitleCrossesCellsReadsTheirOwnNavAndRunsEachCommandOnce() => DvdCompletionFixture.With(c =>
    {
        var path = Path.Combine(c.Folder, "VTS_01_1.VOB"); var writes = new List<(int, byte[])>();
        foreach (var cell in c.Catalog.Chains.Single(chain => chain.Key == DvdStreamFixture.TitleKey).Cells)
        {
            writes.Add(((int)cell.FirstSector, DvdStreamFixture.Nav(cell.FirstSector, cell.LastSector - cell.FirstSector, cell: cell.CellId)));
            for (var sector = cell.FirstSector + 1; sector <= cell.LastSector; sector++) writes.Add(((int)sector, DvdStreamFixture.Media()));
        }
        DvdStreamFixture.Vob(path, 400, writes.ToArray());
        for (var cell = 1; cell <= 3; cell++)
        {
            var read = new DvdStreamTraversal(c.Catalog).Read(c.Playback.State);
            Assert.Equal(DvdStreamStatus.Ready, read.Status); Assert.Equal(cell, read.Unit!.Navigation.CellId);
            var token = c.Playback.Queue(read).Ticket!.Value; var result = c.Playback.Presented(token);
            Assert.Equal(cell == 3 ? DvdCellPlaybackStatus.Stopped : DvdCellPlaybackStatus.Advanced, result.Status);
            Assert.Equal(7 + cell, result.State.Registers.General[0]);
        }
    }, commands: [0x7300000000010000], edit: chain => chain with { Cells = chain.Cells.Select(cell => cell with { CommandNumber = 1 }).ToArray() });

    [Theory]
    [InlineData("snapshot")] [InlineData("epoch")] [InlineData("boundary")] [InlineData("continuation")]
    public void QueuedReadBindingRejectsMismatchedOrContradictoryProposals(string fault) => DvdCompletionFixture.With(c =>
    {
        var read = c.Read;
        if (fault == "snapshot") read = read with { State = c.State with { CatalogIdentity = Guid.NewGuid() } };
        if (fault == "epoch") read = read with { State = c.State with { Epoch = 1 } };
        if (fault == "boundary") read = read with { Unit = new(read.Unit!.Navigation, read.Unit.Bytes.ToArray(), read.Unit.Cell, read.Unit.Boundary! with { CellCommandNumber = 9 }) };
        if (fault == "continuation") read = read with { Next = c.State };
        var result = c.Playback.Queue(read);
        Assert.True(result.Status is DvdCellPlaybackStatus.Stale or DvdCellPlaybackStatus.CompatibilityRequired);
        Assert.Same(c.State, result.State); Assert.Null(result.Ticket); Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void DuplicateQueueAndUnknownCallbacksDoNotReplaceTheActiveHold() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Queue(c.Read).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Presented(Guid.NewGuid()).Status);
        Assert.Equal(DvdCellPlaybackStatus.Holding, c.Playback.Presented(token).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.SkipStill(Guid.NewGuid()).Status);
        c.Clock.Seconds(2); Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 2);

    [Fact]
    public void AcceptedRestartCanRecoverACompatibilityFailureWithoutReusingItsTicket() => DvdCompletionFixture.With(c =>
    {
        var old = c.Queue(); Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, c.Playback.Presented(old).Status);
        var restart = c.Resolver.Execute(c.State, [new(0x2001000000000001)]);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.ReplaceState(restart.State).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Tick(old).Status);
        var read = new DvdStreamTraversal(c.Catalog).Read(c.Playback.State);
        var queued = c.Playback.Queue(read); Assert.Equal(DvdCellPlaybackStatus.AwaitingPresentation, queued.Status); Assert.NotEqual(old, queued.Ticket);
    }, commands: [0xe000000000000000]);

    [Fact]
    public void CancelledCompletionDoesNotModifyTheDomainSnapshot() => DvdCompletionFixture.With(c =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => c.Resolver.CompleteCell(c.State, c.Read.Unit!, cancellationToken: cancelled.Token));
        Assert.Equal(7, c.State.Registers.General[0]);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void MenuVmActionInputCommitsItsLocalEffectOnceAndPreservesTheStillClock() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        var context = new DvdMenuContext(DvdMenuDomain.Manager, 0, 1, 0); var packet = c.Read.Unit!.Navigation;
        var menu = new DvdMenuNavigation(c.State.Registers);
        menu.Load(packet, context, DvdMenuDisplay.Normal);
        c.Clock.Seconds(1); var action = menu.Activate(100_000);
        Assert.Equal(DvdMenuOutcome.CommandExecuted, action.Outcome); Assert.Equal(8, menu.VmState.General[0]);
        // Passing the result registers must not apply an increment twice.
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.MenuCommand(context, packet, action.Command!, menu.VmState).Status);
        var committed = c.Playback.MenuCommand(context, packet, action.Command!, action.InputState!);
        Assert.Equal(DvdCellPlaybackStatus.Holding, committed.Status); Assert.Equal(8, committed.State.Registers.General[0]);
        Assert.Equal(1, committed.InstructionsExecuted); c.Clock.Seconds(2);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 3, menu: true, buttonCommand: 0x7300000000010000);

    [Fact]
    public void MenuVmPendingBranchCanLeaveAnIndefiniteHold() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        var context = new DvdMenuContext(DvdMenuDomain.Manager, 0, 1, 0); var packet = c.Read.Unit!.Navigation;
        var menu = new DvdMenuNavigation(c.State.Registers); menu.Load(packet, context, DvdMenuDisplay.Normal);
        var action = menu.Activate(100_000); Assert.Equal(DvdMenuOutcome.CommandRequiresCompatibility, action.Outcome);
        var committed = c.Playback.MenuCommand(context, packet, action.Command!, action.InputState!);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, committed.Status); Assert.Equal(DvdPlaybackDomain.Title, committed.State.Position.Chain.Domain);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Presented(token).Status);
    }, cellStill: 255, menu: true, buttonCommand: 0x3002000000010000);

    [Fact]
    public void ConcurrentCompletionCallbacksCannotIncrementOrAdvanceTwice() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue();
        var results = new DvdCellPlaybackResult[16]; Parallel.For(0, results.Length, i => results[i] = c.Playback.Presented(token));
        Assert.Single(results, result => result.Status == DvdCellPlaybackStatus.Advanced);
        Assert.Equal(15, results.Count(result => result.Status == DvdCellPlaybackStatus.Stale));
        Assert.Equal(8, c.Playback.State.Registers.General[0]); Assert.Equal(2, c.Playback.State.Position.Cell);
    }, commands: [0x7300000000010000]);
}
