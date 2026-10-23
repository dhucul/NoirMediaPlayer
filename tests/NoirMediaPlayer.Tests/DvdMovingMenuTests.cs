using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

internal static class DvdMovingMenuFixture
{
    internal static DvdCellMedia Prepare(DvdCompletionCase c, ulong command = 0x7300000000010000, bool forced = false,
        bool two = false, DvdHighlightUpdate second = DvdHighlightUpdate.Continue, bool pictures = true)
    {
        var path = Path.Combine(c.Folder, "VIDEO_TS.VOB"); var bytes = File.ReadAllBytes(path);
        var first = DvdStreamFixture.Nav(10, two ? 4u : 9u, two ? 0x80000005u : 0x3fffffffu);
        DvdNavFixture.Command(first, 0, command); first[DvdNavFixture.Hli + 21] = forced ? (byte)1 : (byte)0;
        if (two)
        {
            DvdNavFixture.U32(first, DvdNavFixture.Pci + 16, 112_500);
            var next = DvdStreamFixture.Nav(15, 4); DvdNavFixture.U32(next, DvdNavFixture.Pci + 12, 112_500);
            DvdNavFixture.U16(next, DvdNavFixture.Hli, (ushort)second);
            DvdNavFixture.Command(next, 0, 0x7300000000020000); next.CopyTo(bytes, 15 * 2048);
        }
        first.CopyTo(bytes, 10 * 2048);
        if (pictures) DvdSpuFixture.Sector(DvdSpuFixture.Pes(DvdSpuFixture.Packet())).CopyTo(bytes, 11 * 2048);
        var video = DvdSpuFixture.Pes([0, 0, 1, 0xb3], 90_000); video[3] = 0xe0;
        DvdSpuFixture.Sector(video).CopyTo(bytes, (pictures ? 12 : 11) * 2048); File.WriteAllBytes(path, bytes);
        return DvdCellMedia.Prepare(c.Catalog, c.State);
    }
    internal static DvdMenuContext Context(DvdCompletionCase c) => new(DvdMenuDomain.Manager, 0, 1, c.State.Epoch);
}

public sealed class DvdMovingMenuTests
{
    [Fact]
    public void PreparationAndReadAheadCannotSelectFutureLayoutOrExecuteForcedCommand() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, forced: true, two: true);
        var menu = media.MovingSubpictures!.Menu!;
        Assert.Null(menu.Highlight); Assert.Null(menu.Pending); Assert.Equal(7, menu.Registers.General[0]);
        menu.Advance(90_000); Assert.Equal(1, menu.Highlight!.Button); Assert.Null(menu.Pending);
        Assert.Equal(7, menu.Registers.General[0]); menu.Painted(); Assert.Equal(8, menu.Registers.General[0]);
        var ticket = menu.Pending!.Ticket; menu.Advance(120_000); Assert.Same(media.Units[0], menu.Pending.Read);
        Assert.Equal(DvdMenuOutcome.Inactive, menu.Painted(new(DvdMenuInputKind.Activate)).Outcome);
        menu.Complete(ticket); menu.Advance(120_000); menu.Painted(); Assert.Null(menu.Pending); Assert.Equal(8, menu.Registers.General[0]);
    }, menu: true);

    [Theory]
    [InlineData("Move")] [InlineData("Number")] [InlineData("Point")]
    public void DirectionsNumbersAndPointerUseCurrentMenuWithoutCompletingItsCell(string input) => DvdCompletionFixture.With(c =>
    {
        var kind = Enum.Parse<DvdMenuInputKind>(input);
        var media = DvdMovingMenuFixture.Prepare(c); var menu = media.MovingSubpictures!.Menu!;
        Assert.Equal(DvdMenuOutcome.Inactive, menu.Painted(new(kind, Number: 2, X: 60, Y: 20)).Outcome);
        menu.Advance(100_000); var action = menu.Painted(new(kind, Number: 2, X: 60, Y: 20));
        Assert.Equal(DvdMenuOutcome.SelectionChanged, action.Outcome); Assert.Equal(2, menu.Highlight!.Button); Assert.Null(menu.Pending);
        Assert.Equal(7, menu.Registers.General[0]); Assert.Same(c.State, c.Playback.State);
    }, menu: true);

    [Theory]
    [InlineData("Continue", 8)] [InlineData("CommandsOnly", 9)]
    public void ContinuationKeepsLayoutAndUsesTheCurrentAuthoredCommand(string value, int expected) => DvdCompletionFixture.With(c =>
    {
        var update = Enum.Parse<DvdHighlightUpdate>(value);
        var media = DvdMovingMenuFixture.Prepare(c, two: true, second: update); var menu = media.MovingSubpictures!.Menu!;
        menu.Advance(120_000); menu.Painted(new(DvdMenuInputKind.Activate));
        Assert.Same(media.Units[1], menu.Pending!.Read); Assert.Equal(expected, menu.Registers.General[0]);
        Assert.Equal(1, menu.Highlight!.Button);
    }, menu: true);

    [Fact]
    public void NoneAndExpiredLayoutsClearHighlightAndRejectActivation() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, two: true, second: DvdHighlightUpdate.None); var menu = media.MovingSubpictures!.Menu!;
        menu.Advance(100_000); Assert.NotNull(menu.Highlight); menu.Advance(120_000); Assert.Null(menu.Highlight);
        Assert.Equal(DvdMenuOutcome.Inactive, menu.Painted(new(DvdMenuInputKind.Activate)).Outcome); Assert.Null(menu.Pending);
        menu.Advance(135_000); Assert.Null(menu.Highlight); Assert.Equal(DvdMenuOutcome.Inactive, menu.Painted().Outcome);
    }, menu: true);

    [Fact]
    public void UserProhibitionsBlockInputWhileAuthoredForcedActivationStillRunsOnce() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c); var context = DvdMovingMenuFixture.Context(c);
        var menu = new DvdMovingMenu(media.Units, context, c.State.Registers, 1u << 17); menu.Advance(100_000);
        Assert.Equal(DvdMenuOutcome.Prohibited, menu.Painted(new(DvdMenuInputKind.Activate)).Outcome); Assert.Null(menu.Pending);
        media = DvdMovingMenuFixture.Prepare(c, forced: true); menu = new(media.Units, context, c.State.Registers, 1u << 17); menu.Advance(100_000);
        menu.Painted(); Assert.NotNull(menu.Pending); Assert.Equal(8, menu.Registers.General[0]);
        menu.Complete(menu.Pending!.Ticket); menu.Painted(); Assert.Null(menu.Pending); Assert.Equal(8, menu.Registers.General[0]);
    }, menu: true);

    [Fact]
    public void LocalCommandCommitsOnceFromItsInputAndDoesNotRunCellCompletion() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c); var menu = media.MovingSubpictures!.Menu!; menu.Advance(100_000);
        menu.Painted(new(DvdMenuInputKind.Activate)); var request = menu.Pending!;
        Assert.Equal(7, request.Action.InputState!.General[0]); Assert.Equal(8, menu.Registers.General[0]);
        var committed = c.Playback.MovingMenuCommand(request); Assert.Equal(DvdCellPlaybackStatus.Ready, committed.Status);
        Assert.Equal(8, committed.State.Registers.General[0]); Assert.Equal(c.State.Position, committed.State.Position);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.MovingMenuCommand(request).Status); Assert.Equal(8, c.Playback.State.Registers.General[0]);
        menu.Complete(request.Ticket); Assert.Throws<InvalidOperationException>(() => menu.Complete(request.Ticket));
    }, menu: true, commands: [0x73000000000a0000]);

    [Theory]
    [InlineData(0x2004000000000002UL, false)] [InlineData(0x3002000000010000UL, true)]
    public void MovingBranchResolvesAtObservedNavAndDiscardsUnfinishedCell(ulong command, bool title) => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, command, two: true); var menu = media.MovingSubpictures!.Menu!;
        menu.Advance(120_000); menu.Painted(new(DvdMenuInputKind.Activate));
        Assert.Equal(15u, menu.Pending!.Read.Unit!.Navigation.Sector);
        var committed = c.Playback.MovingMenuCommand(menu.Pending); Assert.Equal(DvdCellPlaybackStatus.Advanced, committed.Status);
        Assert.True(committed.State.Epoch > c.State.Epoch); Assert.Equal(7, committed.State.Registers.General[0]);
        Assert.Equal(title ? DvdPlaybackDomain.Title : DvdPlaybackDomain.ManagerMenu, committed.State.Position.Chain.Domain);
        Assert.Throws<InvalidOperationException>(() => menu.Complete(menu.Pending!.Ticket));
    }, menu: true, commands: [0x73000000000a0000]);

    [Theory]
    [InlineData("epoch")] [InlineData("catalog")] [InlineData("registers")] [InlineData("held")]
    public void StaleObservationsCannotCommitCommands(string fault) => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c); var menu = media.MovingSubpictures!.Menu!; menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Activate));
        var request = menu.Pending!;
        if (fault is "epoch" or "catalog") request = request with { Read = request.Read with { State = request.Read.State with {
            Epoch = fault == "epoch" ? 1 : 0, CatalogIdentity = fault == "catalog" ? Guid.NewGuid() : c.Catalog.Identity } } };
        if (fault == "registers") request = request with { Action = request.Action with { InputState = request.Action.InputState!.WithGeneral(0, 100) } };
        if (fault == "held") c.Playback.Queue(c.Read);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.MovingMenuCommand(request).Status); Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, menu: true);

    [Fact]
    public void UnsupportedCommandRollsBackDomainStateAndClockCannotReverse() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, 0xe000000000000000); var menu = media.MovingSubpictures!.Menu!;
        menu.Advance(100_000); Assert.Throws<NotSupportedException>(() => menu.Advance(99_999)); menu.Painted(new(DvdMenuInputKind.Activate));
        var result = c.Playback.MovingMenuCommand(menu.Pending!); Assert.Equal(DvdCellPlaybackStatus.CompatibilityRequired, result.Status);
        Assert.Same(c.State, result.State); Assert.Equal(7, menu.Registers.General[0]);
    }, menu: true);

    [Fact]
    public void MenuWithoutSpuStillHasTimedVideoButtonsAndRejectsWrongContext() => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c, pictures: false); Assert.False(media.Subpictures.HasPictures);
        Assert.NotNull(media.MovingSubpictures!.Menu); var context = DvdMovingMenuFixture.Context(c) with { ProgramChain = 2 };
        Assert.Throws<ArgumentException>(() => new DvdMovingMenu(media.Units, context, c.State.Registers));
    }, menu: true);

    [Fact]
    public void NoOpTicketsCannotBeReplayedEvenWhenRegistersDoNotChange() => DvdCompletionFixture.With(c =>
    {
        var menu = DvdMovingMenuFixture.Prepare(c, command: 0).MovingSubpictures!.Menu!; menu.Advance(100_000);
        menu.Painted(new(DvdMenuInputKind.Activate)); var first = menu.Pending!; Assert.Equal(DvdCellPlaybackStatus.Ready, c.Playback.MovingMenuCommand(first).Status); menu.Complete(first.Ticket);
        menu.Painted(new(DvdMenuInputKind.Activate)); Assert.Equal(DvdCellPlaybackStatus.Ready, c.Playback.MovingMenuCommand(menu.Pending!).Status);
        Assert.Equal(DvdCellPlaybackStatus.Stale, c.Playback.MovingMenuCommand(first).Status); Assert.Equal(7, c.Playback.State.Registers.General[0]);
    }, menu: true);

    [Theory]
    [InlineData(720, 40)] [InlineData(50, 576)]
    public void ButtonCoordinatesBeyondDvdCanvasRequireCompatibility(int right, int bottom) => DvdCompletionFixture.With(c =>
    {
        var media = DvdMovingMenuFixture.Prepare(c); var read = media.Units[0]; var bytes = read.Unit!.Bytes.ToArray();
        var slot = DvdNavFixture.ButtonOffset(0); var x = (1 << 22) | (10 << 12) | right; var y = (20 << 12) | bottom;
        bytes[slot] = (byte)(x >> 16); bytes[slot + 1] = (byte)(x >> 8); bytes[slot + 2] = (byte)x;
        bytes[slot + 3] = (byte)(y >> 16); bytes[slot + 4] = (byte)(y >> 8); bytes[slot + 5] = (byte)y;
        read = read with { Unit = new(DvdNavigationPacket.Read(bytes.AsSpan(0, 2048)), bytes, read.Unit.Cell, read.Unit.Boundary) };
        Assert.Throws<NotSupportedException>(() => new DvdMovingMenu(new[] { read }, DvdMovingMenuFixture.Context(c), c.State.Registers));
    }, menu: true);
}

public sealed class DvdMenuViewportTests
{
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void InvertsTheSameFittedAndRotatedPictureForMouseCoordinates(int rotation)
    {
        var aspect = rotation is 90 or 270 ? .75 : 4d / 3;
        var width = (int)Math.Round(Math.Min(600, 800 * aspect)); var height = (int)Math.Round(width / aspect);
        double u = .25, v = .375;
        (u, v) = rotation switch { 90 => (1 - v, u), 180 => (1 - u, 1 - v), 270 => (v, 1 - u), _ => (u, v) };
        var point = DvdMenuViewport.Point(600, 800, aspect, rotation, 480, (600 - width) / 2 + (int)(width * u), (800 - height) / 2 + (int)(height * v));
        Assert.NotNull(point); Assert.InRange(point.Value.X, 178, 182); Assert.InRange(point.Value.Y, 178, 182);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(300, 0)] [InlineData(300, 799)] [InlineData(-1, 400)] [InlineData(600, 400)]
    public void LetterboxesAndOutsidePixelsCannotActivatePreviousSelection(int x, int y)
    { Assert.Null(DvdMenuViewport.Point(600, 800, 4d / 3, 0, 480, x, y)); }
    [Fact]
    public void PalAndUnavailableCanvasUseExplicitCoordinateBounds()
    {
        var point = DvdMenuViewport.Point(720, 576, 1.25, 0, 576, 360, 288); Assert.Equal((360, 288), point);
        Assert.Null(DvdMenuViewport.Point(720, 576, double.NaN, 0, 576, 360, 288));
        Assert.Null(DvdMenuViewport.Point(720, 576, 1.25, 0, 1080, 360, 288));
    }
}
