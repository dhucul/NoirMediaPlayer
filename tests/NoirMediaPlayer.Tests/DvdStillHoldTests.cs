using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdStillHoldTests
{
    [Fact]
    public void TimedHoldStartsAtPresentationAndExpiresOnlyOnce() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Clock.Seconds(100);
        var result = c.Playback.Presented(token); Assert.Equal(DvdCellPlaybackStatus.Holding, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Remaining); Assert.Same(c.State, result.State);
        c.Clock.Seconds(2.999); result = c.Playback.Tick(token);
        Assert.Equal(DvdCellPlaybackStatus.Holding, result.Status); Assert.Equal(TimeSpan.FromMilliseconds(1), result.Remaining);
        Assert.Equal(7, result.State.Registers.General[0]);
        c.Clock.Advance(TimeSpan.FromMilliseconds(1)); result = c.Playback.Tick(token);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(8, result.State.Registers.General[0]);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Tick(token).Status);
    }, cellStill: 3, commands: [0x7300000000010000]);

    [Fact]
    public void PauseIntervalsDoNotConsumeStillTimeAndDuplicateAcknowledgementsDoNotRestartIt() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token); c.Clock.Seconds(1);
        Assert.Equal(TimeSpan.FromSeconds(2), c.Playback.SetPaused(true).Remaining);
        c.Clock.Seconds(1000); var result = c.Playback.Tick(token);
        Assert.True(result.Paused); Assert.Equal(TimeSpan.FromSeconds(2), result.Remaining);
        Assert.Equal(DvdCellPlaybackStatus.Holding, c.Playback.Presented(token).Status);
        c.Playback.SetPaused(false); c.Clock.Seconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), c.Playback.Tick(token).Remaining);
        c.Clock.Seconds(1); Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 3);

    [Fact]
    public void PausedPresentationDoesNotStartConsumingTheHoldUntilResumed() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.SetPaused(true); c.Playback.Presented(token);
        c.Clock.Seconds(90); Assert.Equal(TimeSpan.FromSeconds(2), c.Playback.Tick(token).Remaining);
        c.Playback.SetPaused(false); c.Clock.Seconds(2); Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 2);

    [Theory]
    [InlineData(0, 3, 3)] [InlineData(2, 3, 5)] [InlineData(254, 1, 255)] [InlineData(254, 254, 508)]
    public void LastCellAddsFiniteCellAndPgcHoldsWithoutByteOverflow(byte cell, byte pgc, int seconds) => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); var result = c.Playback.Presented(token);
        Assert.False(result.Indefinite); Assert.Equal(TimeSpan.FromSeconds(seconds), result.Remaining);
        c.Clock.Seconds(seconds - 0.01); Assert.Equal(DvdCellPlaybackStatus.Holding, c.Playback.Tick(token).Status);
        c.Clock.Seconds(0.01); Assert.Equal(DvdCellPlaybackStatus.Stopped, c.Playback.Tick(token).Status);
    }, cellStill: cell, pgcStill: pgc, cellNumber: 3);

    [Fact]
    public void PgcHoldIsNotAppliedToEarlierCells() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token); c.Clock.Seconds(2);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 2, pgcStill: 200);

    [Theory]
    [InlineData(255, 0)] [InlineData(0, 255)] [InlineData(255, 1)] [InlineData(1, 255)] [InlineData(255, 255)]
    public void EitherIndefiniteFieldKeepsTheLastCellHeldUntilExplicitRelease(byte cell, byte pgc) => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); var result = c.Playback.Presented(token);
        Assert.True(result.Indefinite); Assert.Null(result.Remaining);
        c.Clock.Seconds(1_000_000); result = c.Playback.Tick(token);
        Assert.Equal(DvdCellPlaybackStatus.Holding, result.Status); Assert.Equal(7, result.State.Registers.General[0]);
        result = c.Playback.SkipStill(token); Assert.Equal(DvdCellPlaybackStatus.Stopped, result.Status);
        Assert.Equal(8, result.State.Registers.General[0]);
    }, cellStill: cell, pgcStill: pgc, cellNumber: 3, commands: [0x7300000000010000]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PgcAndNavStillOffProhibitionsBlockUserReleaseButNotTimedExpiry(bool nav) => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        Assert.Equal(DvdCellPlaybackStatus.Prohibited, c.Playback.SkipStill(token).Status); Assert.Same(c.State, c.Playback.State);
        c.Clock.Seconds(2); Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 2, navProhibitions: nav ? 1u << 18 : 0,
        edit: chain => chain with { ProhibitedOperations = nav ? 0 : 1u << 18 });

    [Fact]
    public void PauseProhibitionDoesNotAccidentallyPauseTheStillClock() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        var result = c.Playback.SetPaused(true); Assert.Equal(DvdCellPlaybackStatus.Prohibited, result.Status); Assert.False(result.Paused);
        c.Clock.Seconds(2); Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.Tick(token).Status);
    }, cellStill: 2, navProhibitions: 1u << 19);

    [Fact]
    public void HeldButtonMenuRequiresItsAuthoredActionAndDoesNotRunTheOldCellCommandOnBranch() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token); c.Clock.Seconds(99);
        Assert.Equal(DvdCellPlaybackStatus.Prohibited, c.Playback.SkipStill(token).Status);
        var result = c.Playback.MenuCommand(Context(c), c.Read.Unit!.Navigation, new(0x3002000000010000), c.State.Registers);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(DvdPlaybackDomain.Title, result.State.Position.Chain.Domain);
        Assert.Equal(100u, result.State.Position.Sector); Assert.Equal(7, result.State.Registers.General[0]);
        Assert.Null(result.Ticket); Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Tick(token).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.SkipStill(token).Status);
    }, cellStill: 255, menu: true, commands: [0x7100000000630000]);

    [Fact]
    public void AMenuWithoutButtonsCanReleaseItsStill() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.SkipStill(token).Status);
        Assert.Equal(30u, c.Playback.State.Position.Sector);
    }, cellStill: 255, menu: true, highlight: false);

    [Fact]
    public void MenuRegisterAndHighlightCommandsDoNotResetTheHoldTimer() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token); c.Clock.Seconds(2);
        var result = c.Playback.MenuCommand(Context(c), c.Read.Unit!.Navigation, new(0x7300000000010000), c.State.Registers);
        Assert.Equal(DvdCellPlaybackStatus.Holding, result.Status); Assert.Equal(token, result.Ticket); Assert.Equal(8, result.State.Registers.General[0]);
        result = c.Playback.MenuCommand(Context(c), c.Read.Unit.Navigation, new(0x5600000008000000), result.State.Registers);
        Assert.Equal(DvdCellPlaybackStatus.Holding, result.Status); Assert.Equal<ushort?>(2 << 10, result.State.Registers.System[8]);
        c.Clock.Seconds(3); result = c.Playback.Tick(token); Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status);
        Assert.Equal(9, result.State.Registers.General[0]);
    }, cellStill: 5, commands: [0x7300000000010000]);

    [Fact]
    public void SeekCancelsTheOldHoldAndRequiresANewEpoch() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.ReplaceState(c.State).Status);
        var seek = c.Resolver.Execute(c.State, [new(0x2007000000000003)]);
        Assert.Equal(DvdDomainStatus.Resolved, seek.Status);
        Assert.Equal(DvdCellPlaybackStatus.Advanced, c.Playback.ReplaceState(seek.State).Status);
        c.Clock.Seconds(200); Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.Tick(token).Status);
        Assert.Same(seek.State, c.Playback.State); Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, cellStill: 4, commands: [0x7100000000630000]);

    [Fact]
    public void FailedMenuProposalLatchesCompatibilityWithoutReleasingOrExecutingCellCommand() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token);
        var result = c.Playback.MenuCommand(Context(c), c.Read.Unit!.Navigation, new(0xe000000000000000), c.State.Registers);
        Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, result.Status); Assert.Same(c.State, result.State);
        c.Clock.Seconds(100); Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, c.Playback.Tick(token).Status);
        Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, c.Playback.SkipStill(token).Status); Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, cellStill: 2, commands: [0x7100000000630000]);

    [Fact]
    public void CancelledPresentationAndTickLeaveTheCommandAvailableForAnExplicitRetry() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => c.Playback.Presented(token, cancelled.Token)); Assert.Same(c.State, c.Playback.State);
        c.Playback.Presented(token); c.Clock.Seconds(2);
        Assert.Throws<OperationCanceledException>(() => c.Playback.Tick(token, cancelled.Token)); Assert.Same(c.State, c.Playback.State);
        var result = c.Playback.Tick(token); Assert.Equal(DvdCellPlaybackStatus.Advanced, result.Status); Assert.Equal(8, result.State.Registers.General[0]);
    }, cellStill: 2, commands: [0x7300000000010000]);

    [Fact]
    public void BackwardsMonotonicClockRequiresCompatibilityWithoutRunningCommands() => DvdCompletionFixture.With(c =>
    {
        var token = c.Queue(); c.Playback.Presented(token); c.Clock.Seconds(-1);
        var result = c.Playback.Tick(token); Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, result.Status); Assert.Same(c.State, result.State);
        Assert.NotNull(result.Reason);
    }, cellStill: 2, commands: [0x7300000000010000]);

    private static DvdMenuContext Context(DvdCompletionCase c) => new(c.State.Position.Chain.Domain == DvdPlaybackDomain.Title ?
        DvdMenuDomain.Title : DvdMenuDomain.Manager, c.State.Position.Chain.TitleSet, c.State.Position.Chain.Number, c.State.Epoch);
}
