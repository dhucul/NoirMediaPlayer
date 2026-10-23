using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class DvdNativeSessionTests
{
    [Theory]
    [InlineData(255, 0)] [InlineData(0, 255)] [InlineData(2, 3)]
    public void HeldTransitionKeepsMenuSelectionRegistersAndWaitsForFinalPaint(int cellStill, int pgcStill) => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c, two: true);
        var renderer = new Renderer { RequireSubpicturePaint = true };
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        session.OpenCell(); var timing = renderer.Timing!; timing.BindFirstFrame(0); timing.Sample(0);
        renderer.Status = new(true, false, 20, true, 0);
        session.MovingMenuInput(new(DvdMenuInputKind.Number, Number: 2)); session.Pump();
        Assert.Equal(2, timing.Menu!.Highlight!.Button);
        renderer.Status = renderer.Status with { Ended = true };
        Assert.Equal(DvdNativeStatus.Playing, session.Pump().Status);
        Assert.True(timing.Held); Assert.Same(timing, renderer.Timing); Assert.Equal(2, renderer.Highlight!.Button);
        Assert.Equal(135_000, timing.Current90Khz); Assert.Equal(1, renderer.Holds);
        c.Clock.Seconds(100); Assert.Equal(DvdNativeStatus.Playing, session.Pump().Status); Assert.Null(session.HeldNavigation);
        renderer.Status = renderer.Status with { Painted = true };
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status); Assert.True(session.HasMovingMenu);
        Assert.Equal<ushort?>(2 << 10, session.Navigation.Registers.System[8]);
        Assert.Throws<InvalidOperationException>(() => session.CreateHeldMenu(DvdMovingMenuFixture.Context(c), DvdMenuDisplay.Normal));
        session.MovingMenuInput(new(DvdMenuInputKind.Point, X: 10, Y: 20)); session.Pump();
        Assert.Equal(1, timing.Menu.Highlight!.Button); Assert.Equal<ushort?>(1 << 10, session.Navigation.Registers.System[8]);
        Assert.Equal(1, renderer.Opens); Assert.Equal(1, renderer.Holds); Assert.False(renderer.Disposed);
        c.Clock.Seconds(4); Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
    }, menu: true, cellStill: (byte)cellStill, pgcStill: (byte)pgcStill);

    [Fact]
    public void HeldLocalCommandDoesNotResetFiniteClockAndCompletionRunsOnce() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c); var renderer = new Renderer();
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        StartTimedHold(session, renderer);
        c.Clock.Seconds(1); session.MovingMenuInput(new(DvdMenuInputKind.Activate));
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status); Assert.Equal(8, session.Navigation.Registers.General[0]);
        Assert.Null(renderer.Timing!.Menu!.Pending); c.Clock.Seconds(1);
        Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status); Assert.Equal(18, session.Navigation.Registers.General[0]);
        Assert.False(session.HasMovingMenu); var state = session.Navigation;
        session.MovingMenuInput(new(DvdMenuInputKind.Activate)); session.Pump(); Assert.Same(state, session.Navigation);
    }, menu: true, cellStill: 2, commands: [0x73000000000a0000]);

    [Fact]
    public void HeldSelectionReachesCellCommandAndUserPauseFreezesOnlyStillTimer() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c); var renderer = new Renderer();
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        StartTimedHold(session, renderer); c.Clock.Seconds(1); session.SetPaused(true); c.Clock.Seconds(100);
        session.MovingMenuInput(new(DvdMenuInputKind.Number, Number: 2));
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status); Assert.True(renderer.Paused);
        session.SetPaused(false); c.Clock.Seconds(1);
        Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status);
        Assert.Equal(2 << 10, session.Navigation.Registers.General[0]); Assert.Equal(135_000, renderer.Timing!.Current90Khz);
    }, menu: true, cellStill: 2, commands: [0x6100000000880000]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HeldBranchDiscardsOldHoldWithoutCellCommandAndPreservesUserPause(bool paused) => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c, 0x2004000000000002); WriteMovingTarget(c);
        var renderers = new List<Renderer>(); using var session = new WindowsDvdCellSession(c.Catalog, c.State,
            () => { var renderer = new Renderer(); renderers.Add(renderer); return renderer; }, c.Clock);
        session.OpenCell(); var renderer = renderers[0]; renderer.Timing!.BindFirstFrame(0); renderer.Timing.Sample(0);
        renderer.Status = new(true, true, 20, true, 0); session.Pump(); Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
        if (paused) session.SetPaused(true);
        session.MovingMenuInput(new(DvdMenuInputKind.Activate));
        Assert.Equal(DvdNativeStatus.Loading, session.Pump().Status); Assert.True(renderer.Disposed);
        Assert.Equal(2, renderers.Count); Assert.Equal(paused, renderers[1].Paused);
        Assert.Equal(2, session.Navigation.Position.Chain.Number); Assert.Equal(7, session.Navigation.Registers.General[0]);
        Assert.True(session.Navigation.Epoch > c.State.Epoch);
    }, menu: true, cellStill: 255, commands: [0x73000000000a0000]);

    [Theory]
    [InlineData("Continue")] [InlineData("CommandsOnly")]
    public void HeldTransitionDoesNotRepeatForcedActivationAndRetainsFinalNavCommands(string continuation) => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c, forced: true, two: true, second: Enum.Parse<DvdHighlightUpdate>(continuation));
        var renderer = new Renderer(); using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        session.OpenCell(); renderer.Timing!.BindFirstFrame(0); renderer.Timing.Sample(0);
        var menu = renderer.Timing.Menu!; menu.Painted(); renderer.Status = new(true, false, 20, true, 0, MenuCommand: menu.Pending);
        session.Pump(); Assert.Equal(8, session.Navigation.Registers.General[0]);
        renderer.Status = renderer.Status with { Ended = true }; session.Pump(); session.Pump();
        Assert.True(menu.Held); Assert.Equal(DvdMenuOutcome.Unchanged, menu.Painted().Outcome);
        Assert.Null(menu.Pending); Assert.Equal(8, menu.Registers.General[0]);
        session.MovingMenuInput(new(DvdMenuInputKind.Activate)); Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
        Assert.Equal(continuation == "Continue" ? 9 : 10, session.Navigation.Registers.General[0]);
    }, menu: true, cellStill: 255);

    [Fact]
    public void HeldUnsupportedCommandRetainsCommittedPositionAndRequiresCompatibility() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c, 0xe000000000000000); var renderer = new Renderer();
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        StartTimedHold(session, renderer); var state = session.Navigation;
        session.MovingMenuInput(new(DvdMenuInputKind.Activate));
        Assert.Equal(DvdNativeStatus.CompatibilityRequired, session.Pump().Status); Assert.Same(state, session.Navigation); Assert.True(renderer.Disposed);
    }, menu: true, cellStill: 255);

    [Fact]
    public void HeldNoOpAndMovingTicketsCannotBeReplayedAcrossTheTransition() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, 0); var menu = media.MovingSubpictures!.Menu!;
        menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Activate)); var moving = menu.Pending!;
        Assert.Equal(DvdCellPlaybackStatus.Ready, c.Playback.MovingMenuCommand(moving).Status); menu.Complete(moving.Ticket);
        var read = media.Units[0] with { State = media.Units[0].State with { Registers = c.Playback.State.Registers } };
        var queued = c.Playback.Queue(read); Assert.Equal(DvdCellPlaybackStatus.Holding, c.Playback.Presented(queued.Ticket!.Value).Status);
        menu.EnterHold(135_000); menu.Painted(new(DvdMenuInputKind.Activate)); var held = menu.Pending!;
        Assert.Equal(DvdCellPlaybackStatus.Holding, c.Playback.HeldMenuCommand(held).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.HeldMenuCommand(held).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.HeldMenuCommand(moving).Status);
        Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, menu: true, cellStill: 255);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HeldFinalNavStillRespectsExpiredHighlightAndSelectionWindows(bool selectionOnly) => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c);
        var path = Path.Combine(c.Folder, "VIDEO_TS.VOB"); var bytes = File.ReadAllBytes(path);
        var nav = bytes.AsSpan(10 * 2048, 2048).ToArray();
        DvdNavFixture.U32(nav, DvdNavFixture.Hli + 10, 130_000);
        if (!selectionOnly) DvdNavFixture.U32(nav, DvdNavFixture.Hli + 6, 130_000);
        nav.CopyTo(bytes, 10 * 2048); File.WriteAllBytes(path, bytes);
        var timing = DvdCellMedia.Prepare(c.Catalog, c.State).MovingSubpictures!;
        timing.BindFirstFrame(0); timing.Sample(0); timing.EnterHold();
        Assert.Equal(selectionOnly, timing.Menu!.Highlight is not null);
        Assert.Equal(selectionOnly ? DvdMenuOutcome.Prohibited : DvdMenuOutcome.Inactive,
            timing.Menu.Painted(new(DvdMenuInputKind.Number, Number: 2)).Outcome);
        Assert.Null(timing.Menu.Pending); Assert.Throws<InvalidOperationException>(() => timing.Sample(0));
        Assert.Throws<InvalidOperationException>(() => timing.Menu.Advance(135_001));
    }, menu: true, cellStill: 255);

    [Fact]
    public void HeldPreparationRequiresDecodedClockAndPendingActionResolution() => DvdCompletionFixture.With(c =>
    {
        var timing = DvdMovingMenuFixture.Prepare(c).MovingSubpictures!;
        Assert.Throws<InvalidOperationException>(() => timing.EnterHold());
        timing.BindFirstFrame(0); timing.Sample(0); timing.Menu!.Painted(new(DvdMenuInputKind.Activate));
        Assert.Throws<InvalidOperationException>(() => timing.EnterHold()); Assert.False(timing.Held);
        timing.Menu.Complete(timing.Menu.Pending!.Ticket); var picture = timing.EnterHold();
        Assert.Same(picture, timing.EnterHold()); Assert.Equal(135_000, timing.Current90Khz);
    }, menu: true, cellStill: 255);

    private static void StartTimedHold(WindowsDvdCellSession session, Renderer renderer)
    {
        session.OpenCell(); renderer.Timing!.BindFirstFrame(0); renderer.Timing.Sample(0);
        renderer.Status = new(true, true, 20, true, 0); session.Pump();
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
    }
}
