using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class DvdNativeSessionTests
{
    [Fact]
    public void MovingCommandWaitsForPaintThenLocalEffectsSurviveTheCellCommandExactlyOnce() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c); var renderer = new Renderer(); using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer);
        session.OpenCell(); Assert.True(session.HasMovingMenu); var menu = renderer.Timing!.Menu!;
        menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Activate));
        renderer.Status = new(true, false, 20, false, 0, MenuCommand: menu.Pending);
        session.Pump(); Assert.Equal(7, session.Navigation.Registers.General[0]); Assert.NotNull(menu.Pending);
        renderer.Status = renderer.Status with { Painted = true }; session.Pump();
        Assert.Equal(8, session.Navigation.Registers.General[0]); Assert.Null(menu.Pending);
        for (var i = 0; i < 4; i++) session.Pump(); Assert.Equal(8, session.Navigation.Registers.General[0]);
        renderer.Status = renderer.Status with { Ended = true, MenuRegisters = menu.Registers };
        session.Pump(); Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status);
        Assert.Equal(18, session.Navigation.Registers.General[0]); Assert.Equal(2, session.Navigation.Position.Chain.Number);
    }, menu: true, commands: [0x73000000000a0000]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MovingBranchShutsDownTheUnfinishedCellAndPreservesPauseInTheNewEngine(bool paused) => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c, 0x2004000000000002); WriteMovingTarget(c);
        var renderers = new List<Renderer>(); using var session = new WindowsDvdCellSession(c.Catalog, c.State,
            () => { var renderer = new Renderer(); renderers.Add(renderer); return renderer; });
        session.OpenCell(); var first = renderers[0]; first.Status = new(true, false, 20, true, 0);
        if (paused) session.SetPaused(true);
        var menu = first.Timing!.Menu!; menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Activate));
        first.Status = first.Status with { MenuCommand = menu.Pending };
        Assert.Equal(DvdNativeStatus.Loading, session.Pump().Status); Assert.True(first.Disposed);
        Assert.Equal(2, renderers.Count); Assert.Equal(paused, renderers[1].Paused);
        Assert.Throws<ObjectDisposedException>(() => first.Stream!.Stat(out _, 1));
        Assert.Equal(2, session.Navigation.Position.Chain.Number); Assert.True(session.Navigation.Epoch > 0);
        Assert.Equal(7, session.Navigation.Registers.General[0]);
    }, menu: true, commands: [0x73000000000a0000]);

    [Fact]
    public void MovingRequestFromAnOlderEngineCannotCommitAfterReposition() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c); var renderers = new List<Renderer>(); using var session = new WindowsDvdCellSession(c.Catalog, c.State,
            () => { var renderer = new Renderer(); renderers.Add(renderer); return renderer; });
        session.OpenCell(); var menu = renderers[0].Timing!.Menu!; menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Activate)); var old = menu.Pending;
        session.Reposition(c.State with { Epoch = 1 }); renderers[1].Status = new(true, false, 20, true, 0, MenuCommand: old);
        Assert.Equal(DvdNativeStatus.CompatibilityRequired, session.Pump().Status);
        Assert.Equal(1, session.Navigation.Epoch); Assert.Equal(7, session.Navigation.Registers.General[0]); Assert.True(renderers[1].Disposed);
    }, menu: true);

    [Fact]
    public void MovingSelectionIsCommittedForLaterCellCommandsWithoutExecutingAButton() => DvdCompletionFixture.With(c =>
    {
        DvdMovingMenuFixture.Prepare(c); var renderer = new Renderer(); using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer);
        session.OpenCell(); var menu = renderer.Timing!.Menu!; menu.Advance(100_000); menu.Painted(new(DvdMenuInputKind.Number, Number: 2));
        renderer.Status = new(true, false, 3, true, 0, MenuRegisters: menu.Registers); session.Pump();
        Assert.Equal<ushort?>(2 << 10, session.Navigation.Registers.System[8]); Assert.Equal(7, session.Navigation.Registers.General[0]);
        renderer.Status = renderer.Status with { Ended = true }; session.Pump(); session.Pump();
        Assert.Equal(2 << 10, session.Navigation.Registers.General[0]);
    }, menu: true, commands: [0x6100000000880000]);

    private static void WriteMovingTarget(DvdCompletionCase c)
    {
        var path = Path.Combine(c.Folder, "VIDEO_TS.VOB"); var bytes = File.ReadAllBytes(path);
        DvdStreamFixture.Nav(30, 9).CopyTo(bytes, 30 * 2048);
        for (var i = 31; i <= 39; i++) DvdStreamFixture.Media().CopyTo(bytes, i * 2048);
        var video = DvdSpuFixture.Pes([0, 0, 1, 0xb3], 90_000); video[3] = 0xe0;
        DvdSpuFixture.Sector(video).CopyTo(bytes, 31 * 2048); File.WriteAllBytes(path, bytes);
    }
}
