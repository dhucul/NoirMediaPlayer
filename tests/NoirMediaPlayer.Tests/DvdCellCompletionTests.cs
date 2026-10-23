using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdCellCompletionTests
{
    [Fact]
    public void CellCommandWaitsForPresentationAndAdvancesExactlyOnce() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Clock.Seconds(60);
        Assert.Equal(DvdCellPlaybackStatus.AwaitingPresentation, c.Playback.Tick(token).Status);
        Assert.Equal(DvdCellPlaybackStatus.AwaitingPresentation, c.Playback.SkipStill(token).Status);
        Assert.Same(c.State, c.Playback.State);
        var result = c.Playback.Presented(token);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(8, result.State.Registers.General[0]);
        Assert.Equal(2, result.State.Position.Cell); Assert.Equal(200u, result.State.Position.Sector);
        Assert.Equal(1, result.State.Epoch); Assert.Equal(1, result.InstructionsExecuted); Assert.False(result.PlaybackVerified);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Presented(token).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Tick(token).Status);
        Assert.Equal(8, c.Playback.State.Registers.General[0]); Assert.Equal(7, c.State.Registers.General[0]);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void OnlyTheOneBasedSelectedCommandRuns() => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue());
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(9, result.State.Registers.General[0]);
        Assert.Equal(1, result.InstructionsExecuted);
    }, commands: [0xe000000000000000, 0x7300000000020000, 0x7100000000630000], commandNumber: 2);

    [Theory]
    [InlineData(1, 2, 1)] [InlineData(2, 3, 2)]
    public void NaturalCompletionUpdatesProgramAndChapterContext(int cell, int nextCell, int chapter) => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue());
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(nextCell, result.State.Position.Cell);
        Assert.Equal(chapter, result.State.Position.Program); Assert.Equal<ushort?>((ushort)chapter, result.State.Registers.System[7]);
    }, cellNumber: cell);

    [Theory]
    [InlineData(0x0002000000000000UL)] // Break ends this one-row command.
    [InlineData(0x71a0010000630001UL)] // g1 != 1: assignment skipped.
    [InlineData(0UL)]
    public void BreakNoopAndFalseConditionFallThroughToTheNextCell(ulong command) => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue()); Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status);
        Assert.Equal(2, result.State.Position.Cell); Assert.Equal(7, result.State.Registers.General[0]);
    }, commands: [command]);

    [Theory]
    [InlineData(0x2007000000000003UL, 3, 300)]
    [InlineData(0x2001000000000001UL, 1, 100)]
    [InlineData(0x2001000000000002UL, 2, 200)]
    public void AuthoredCellLinksChooseTheirDestinationWithoutAnExtraAdvance(ulong command, int cell, uint sector) => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue()); Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status);
        Assert.Equal(cell, result.State.Position.Cell); Assert.Equal(sector, result.State.Position.Sector); Assert.Equal(1, result.State.Epoch);
    }, commands: [command]);

    [Fact]
    public void CellCallSavesCurrentTitleCellAndResumeSlot() => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue()); Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status);
        Assert.Equal(DvdPlaybackDomain.TitleSetMenu, result.State.Position.Chain.Domain);
        Assert.Equal(1, result.State.Resume!.Position.Cell); Assert.Equal(100u, result.State.Resume.Position.Sector);
        var resumed = c.Resolver.Execute(result.State, [new(0x2001000000000010)]);
        Assert.Equal(DvdDomainStatus.Resolved, resumed.Status); Assert.Equal(100u, resumed.State.Position.Sector);
    }, commands: [0x3008000000830000]);

    [Fact]
    public void LastCellRunsCommandThenPostTableAndStops() => DvdCompletionFixture.With(c =>
    {
        var result = c.Playback.Presented(c.Queue());
        Assert.Equal(DvdCellPlaybackStatus.Stopped, result.Status); Assert.True(result.State.Stopped);
        Assert.Equal(DvdNavigationPhase.PostCommands, result.State.Position.Phase);
        Assert.Equal(10, result.State.Registers.General[0]); Assert.Equal(2, result.InstructionsExecuted);
    }, cellNumber: 3, commands: [0x7300000000010000], edit: chain => chain with { PostCommands = new[] { new DvdButtonCommand(0x7300000000020000) } });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LastMenuCellRunsDestinationPreCommandsOrRollsBackTheWholeCompletion(bool fail) => DvdCompletionFixture.With(c =>
    {
        var key = DvdStreamFixture.ManagerKey with { Number = 2 };
        var catalog = new DvdNavigationCatalog(c.Folder, c.Catalog.MetadataFiles, c.Catalog.Chains.Select(chain => chain.Key != key ? chain :
            chain with { PreCommands = new[] { new DvdButtonCommand(fail ? 0xe000000000000000 : 0x7300000000020000) } }), c.Catalog.Titles);
        var state = new DvdDomainResolver(catalog).ObserveCell(DvdStreamFixture.ManagerKey, 1, 10, c.State.Registers);
        var player = new DvdCellPlayback(catalog, state, c.Clock);
        var queued = player.Queue(c.Read with { State = state }); var result = player.Presented(queued.Ticket!.Value);
        if (fail)
        {
            Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
            Assert.Equal(7, result.State.Registers.General[0]);
        }
        else
        {
            Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status);
            Assert.Equal(2, result.State.Position.Chain.Number); Assert.Equal(30u, result.State.Position.Sector);
            Assert.Equal(10, result.State.Registers.General[0]); Assert.Equal(2, result.InstructionsExecuted);
        }
    }, menu: true, commands: [0x7300000000010000]);

    [Fact]
    public void FailedPostCommandRollsBackTheCellCommandAndLatchesCompatibility() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); var result = c.Playback.Presented(token);
        Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, result.Status); Assert.Same(c.State, result.State);
        Assert.Equal(7, result.State.Registers.General[0]); Assert.Equal(2, result.InstructionsExecuted);
        Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, c.Playback.Tick(token).Status);
        Assert.Equal(0, c.Playback.Presented(token).InstructionsExecuted); Assert.Same(c.State, c.Playback.State);
    }, cellNumber: 3, commands: [0x7300000000010000], edit: chain => chain with { PostCommands = new[] { new DvdButtonCommand(0xe000000000000000) } });

    [Theory]
    [InlineData(0xe000000000000000UL, "CompatibilityRequired")]
    [InlineData(0x0001000000000002UL, "CompatibilityRequired")]
    [InlineData(0x2007000000000009UL, "CompatibilityRequired")]
    [InlineData(0x0001000000000001UL, "Limit")]
    public void UnsupportedMissingTargetsAndCommandLoopsNeverCommitOrRepeat(ulong command, string status) => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); var result = c.Playback.Presented(token);
        Assert.Equal(status, result.Status.ToString()); Assert.Same(c.State, result.State); Assert.NotNull(result.Reason);
        Assert.Equal(status, c.Playback.Tick(token).Status.ToString()); Assert.Equal(0, c.Playback.Tick(token).InstructionsExecuted);
    }, commands: [command]);

    [Fact]
    public void CompletionBudgetsAggregateCellPostAndDestinationPreWork() => DvdCompletionFixture.With(c =>
    {
        var limited = c.Resolver.CompleteCell(c.State, c.Read.Unit!, instructionLimit: 1);
        Assert.Equal(DvdDomainStatus.Limit, limited.Status); Assert.Same(c.State, limited.State); Assert.Equal(1, limited.InstructionsExecuted);
        limited = c.Resolver.CompleteCell(c.State, c.Read.Unit!, transitionLimit: 1);
        Assert.Equal(DvdDomainStatus.Limit, limited.Status); Assert.Same(c.State, limited.State);
    }, cellNumber: 3, commands: [0x7300000000010000], edit: chain => chain with { PostCommands = new[] { new DvdButtonCommand(0x7300000000020000) } });

    [Theory]
    [InlineData("snapshot")] [InlineData("sector")] [InlineData("cell")] [InlineData("boundary")]
    [InlineData("missing-command")] [InlineData("epoch")]
    public void MismatchedBoundaryProposalsCannotRunCellCommands(string fault) => DvdCompletionFixture.With(c =>
    {
        var state = c.State; var unit = c.Read.Unit!;
        if (fault == "snapshot") state = state with { CatalogIdentity = Guid.NewGuid() };
        if (fault == "sector") state = state with { Position = state.Position with { Sector = 101 } };
        if (fault == "cell") unit = new(unit.Navigation, unit.Bytes.ToArray(), unit.Cell with { CellId = 9 }, unit.Boundary);
        if (fault == "boundary") unit = new(unit.Navigation, unit.Bytes.ToArray(), unit.Cell, unit.Boundary! with { CellStillSeconds = 5 });
        if (fault == "epoch") state = state with { Epoch = -1 };
        var result = c.Resolver.CompleteCell(state, unit);
        Assert.Equal(DvdDomainStatus.CompatibilityRequired, result.Status); Assert.Same(state, result.State);
        Assert.Equal(0, result.InstructionsExecuted);
    }, commands: [0x7300000000010000], edit: fault == "missing-command" ? chain => chain with { CellCommands = Array.Empty<DvdButtonCommand>() } : null);
}
