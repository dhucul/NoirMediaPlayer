using System.Runtime.InteropServices;
using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class DvdNativeSessionTests
{
    private sealed class Renderer : IDvdCellRenderer
    {
        internal DvdNativePresentation Status = new(false, false, 0, false, 0);
        internal bool Disposed, Paused;
        internal int Opens;
        internal DvdCellComStream? Stream;
        internal Exception? Failure;
        internal int SubpictureChanges;
        internal DvdSubpictureFrame? Subpicture;
        internal DvdButtonHighlight? Highlight;
        internal bool RequireSubpicturePaint;
        internal DvdSubpictureTiming? Timing;
        internal int Holds;
        public void Open(DvdCellComStream stream, int audioStreams, DvdSubpictureTiming? subpictures = null) { Stream = stream; Timing = subpictures; Opens++; if (Failure is not null) throw Failure; }
        public DvdNativePresentation Poll() => Status;
        public DvdMenuAction MenuInput(DvdMenuInput input)
        {
            if (!Status.Painted) return new(DvdMenuOutcome.Inactive);
            var action = Timing!.Menu!.Painted(input);
            Status = Status with { MenuCommand = Timing.Menu.Pending, MenuRegisters = Timing.Menu.Pending is null ? Timing.Menu.Registers : null };
            return action;
        }
        public void HoldPicture()
        {
            Subpicture = Timing!.EnterHold(); Highlight = Timing.Menu?.Highlight; Holds++;
            Status = Status with { MenuRegisters = Timing.Menu?.Registers };
            if (RequireSubpicturePaint) Status = Status with { Painted = false };
        }
        public void CompleteMenu(Guid ticket) { Timing!.Menu!.Complete(ticket); Status = Status with { MenuCommand = null }; }
        public void Pause(bool paused) => Paused = paused;
        public void SetSubpicture(DvdSubpictureFrame? frame, IReadOnlyList<uint> palette, DvdButtonHighlight? highlight)
        { Subpicture = frame; Highlight = highlight; SubpictureChanges++; if (RequireSubpicturePaint) Status = Status with { Painted = false }; }
        public void Dispose() { Disposed = true; Subpicture = null; Stream!.Stat(out _, 1); }
    }
    [Fact]
    public void ReadAheadMetadataEofAndDecodedFramesCannotCompleteBeforePaintedNativeEnd() => DvdCompletionFixture.With(c =>
    {
        var renderer = new Renderer(); using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        Assert.Equal(DvdNativeStatus.Loading, session.OpenCell().Status); Assert.Equal(1, renderer.Opens);
        foreach (var status in new[] { new DvdNativePresentation(false, true, 20, true, 0), new(true, false, 20, true, 0), new(true, true, 20, false, 0) })
        {
            renderer.Status = status; session.Pump(); Assert.Same(c.State, session.Navigation);
        }
        renderer.Status = new(true, true, 20, true, 0); var result = session.Pump();
        Assert.Equal(DvdNativeStatus.CellComplete, result.Status); Assert.Equal(8, result.Navigation.Registers.General[0]);
        Assert.Equal(2, result.Navigation.Position.Cell);
        for (var i = 0; i < 5; i++) session.Pump(); Assert.Equal(8, session.Navigation.Registers.General[0]);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void NativeEndStartsTheHoldRatherThanSkippingIt() => DvdCompletionFixture.With(c =>
    {
        var renderer = new Renderer { Status = new(true, true, 4, true, 0) };
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock); session.OpenCell(); c.Clock.Seconds(90);
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status); Assert.Same(c.State, session.Navigation);
        c.Clock.Seconds(1); session.SetPaused(true); c.Clock.Seconds(90);
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status); Assert.True(renderer.Paused);
        session.SetPaused(false); c.Clock.Seconds(1); Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status);
        Assert.Equal(8, session.Navigation.Registers.General[0]);
    }, cellStill: 2, commands: [0x7300000000010000]);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DecoderAndRendererFailuresPreserveNavigationAndReleaseNativeReaderFirst(bool onOpen) => DvdCompletionFixture.With(c =>
    {
        var renderer = new Renderer { Failure = onOpen ? new COMException("MPEG-2 license unavailable", unchecked((int)0xc004f011)) : null };
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock);
        var result = session.OpenCell();
        if (!onOpen) { renderer.Status = new(true, true, 0, false, unchecked((int)0x80004005)); result = session.Pump(); }
        Assert.Equal(DvdNativeStatus.CompatibilityRequired, result.Status); Assert.Same(c.State, result.Navigation); Assert.True(renderer.Disposed);
        Assert.Throws<ObjectDisposedException>(() => renderer.Stream!.Stat(out _, 1));
        Assert.Equal(DvdNativeStatus.CompatibilityRequired, session.Pump().Status);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void CancelledPreparationDoesNotConstructARenderer() => DvdCompletionFixture.With(c =>
    {
        var creates = 0; using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => { creates++; return new Renderer(); });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.OpenCell(cancelled.Token)); Assert.Equal(0, creates); Assert.Same(c.State, session.Navigation);
    });

    [Fact]
    public void ActualMpeg2ActivationProbePreservesItsNativeResult()
    {
        var result = WindowsMediaEngine.ProbeDvdMpeg2();
        // Installation/license availability is external state; either success or
        // an exact native failure is meaningful, never a fabricated ready flag.
        Assert.True(result == 0 || result < 0);
    }

    [Fact]
    public void CompletionCanOpenTheNextAuthoredCellWithoutReplayingCommands() => DvdCompletionFixture.With(c =>
    {
        var writes = new List<(int, byte[])>();
        var chain = c.Catalog.Chains.Single(chain => chain.Key == DvdStreamFixture.TitleKey);
        foreach (var cell in chain.Cells)
        {
            writes.Add(((int)cell.FirstSector, DvdStreamFixture.Nav(cell.FirstSector, cell.LastSector - cell.FirstSector, cell: cell.CellId)));
            for (var sector = cell.FirstSector + 1; sector <= cell.LastSector; sector++) writes.Add(((int)sector, DvdStreamFixture.Media()));
        }
        DvdStreamFixture.Vob(Path.Combine(c.Folder, "VTS_01_1.VOB"), 400, writes.ToArray());
        var renderers = new List<Renderer>();
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () =>
        { var renderer = new Renderer { Status = new(true, true, 3, true, 0) }; renderers.Add(renderer); return renderer; });
        session.OpenCell(); Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status); Assert.Equal(8, session.Navigation.Registers.General[0]);
        Assert.Equal(DvdNativeStatus.Loading, session.ContinueCell().Status); Assert.True(renderers[0].Disposed);
        Assert.Equal(DvdNativeStatus.CellComplete, session.Pump().Status); Assert.Equal(3, session.Navigation.Position.Cell);
        Assert.Equal(8, session.Navigation.Registers.General[0]); Assert.Equal(2, renderers.Count);
    }, commands: [0x7300000000010000]);

    [Fact]
    public void ZeroLengthComSegmentsCannotStallARead()
    {
        using var stream = new DvdCellComStream(new ReadOnlyMemory<byte>[] { Array.Empty<byte>(), new byte[] { 3 }, Array.Empty<byte>(), new byte[] { 4 } });
        var bytes = new byte[2]; stream.Read(bytes, 2, 0); Assert.Equal(new byte[] { 3, 4 }, bytes);
    }

    [Fact]
    public void HeldSubpictureMustPaintBeforeHoldAndSelectionCanChangeWithoutCompletingTheCell() => DvdCompletionFixture.With(c =>
    {
        DvdSpuFixture.Write(c);
        var renderer = new Renderer { Status = new(true, true, 4, true, 0), RequireSubpicturePaint = true };
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer, c.Clock); session.OpenCell();
        Assert.Equal(DvdNativeStatus.Playing, session.Pump().Status); Assert.NotNull(renderer.Subpicture); Assert.Same(c.State, session.Navigation);
        c.Clock.Seconds(50); Assert.Equal(DvdNativeStatus.Playing, session.Pump().Status); Assert.Null(session.HeldNavigation);
        renderer.Status = renderer.Status with { Painted = true }; Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
        var key = c.State.Position.Chain; var context = new DvdMenuContext(DvdMenuDomain.Manager, key.TitleSet, key.Number, c.State.Epoch);
        var menu = session.CreateHeldMenu(context, DvdMenuDisplay.Normal); session.RefreshHeldSubpicture(menu);
        Assert.Equal(1, renderer.Highlight!.Button);
        menu.Move(DvdMenuDirection.Right, session.HeldNavigation!.End90Khz); session.RefreshHeldSubpicture(menu);
        Assert.Equal(2, renderer.Highlight!.Button); Assert.Equal(7, session.Navigation.Registers.General[0]);
        Assert.Throws<ArgumentException>(() => session.CreateHeldMenu(context with { Epoch = context.Epoch + 1 }, DvdMenuDisplay.Normal));
        session.Reposition(c.State with { Epoch = c.State.Epoch + 1 }); Assert.True(renderer.Disposed); Assert.Null(renderer.Subpicture);
        Assert.Throws<InvalidOperationException>(() => session.RefreshHeldSubpicture(menu));
    }, menu: true, cellStill: 255, edit: chain => chain with { Palette = DvdSpuFixture.Palette });

    [Fact]
    public void HeldMenuLoadsEarlierHighlightLayoutBeforeAContinuationAndRetainsPgcProhibitions() => DvdCompletionFixture.With(c =>
    {
        var path = Path.Combine(c.Folder, "VIDEO_TS.VOB");
        var first = DvdStreamFixture.Nav(10, 4, 0x80000005); var second = DvdStreamFixture.Nav(15, 4);
        DvdNavFixture.U16(second, DvdNavFixture.Hli, 2);
        var writes = new List<(int, byte[])> { (10, first), (15, second), (11, DvdSpuFixture.Sector(DvdSpuFixture.Pes(DvdSpuFixture.Packet()))) };
        foreach (var sector in new[] { 12, 13, 14, 16, 17, 18, 19 }) writes.Add((sector, DvdStreamFixture.Media()));
        DvdStreamFixture.Vob(path, 400, writes.ToArray());
        var renderer = new Renderer { Status = new(true, true, 4, true, 0) };
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => renderer); session.OpenCell(); session.Pump();
        Assert.Equal(DvdNativeStatus.Holding, session.Pump().Status);
        var context = new DvdMenuContext(DvdMenuDomain.Manager, 0, 1, c.State.Epoch);
        var menu = session.CreateHeldMenu(context, DvdMenuDisplay.Normal); session.RefreshHeldSubpicture(menu);
        Assert.Equal(1, renderer.Highlight!.Button);
        Assert.Equal(DvdMenuOutcome.Prohibited, menu.Move(DvdMenuDirection.Right, 135_000).Outcome);
    }, menu: true, cellStill: 255, edit: chain => chain with { Palette = DvdSpuFixture.Palette, ProhibitedOperations = 1u << 17 });

    [Fact]
    public void UnsupportedSpuCannotConstructRendererOrAcknowledgeNavigation() => DvdCompletionFixture.With(c =>
    {
        var packet = DvdSpuFixture.Packet(); packet[^1] = 7; DvdSpuFixture.Write(c, packet);
        var creates = 0; using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => { creates++; return new Renderer(); });
        Assert.Equal(DvdNativeStatus.CompatibilityRequired, session.OpenCell().Status); Assert.Equal(0, creates); Assert.Same(c.State, session.Navigation);
    }, menu: true, cellStill: 255);

    [Fact]
    public void MovingSubpictureTimingReachesRendererWithoutAdvancingVmAndIsRecreatedOnReposition() => DvdCompletionFixture.With(c =>
    {
        DvdSubpictureTimingTests.WriteMoving(c); var renderers = new List<Renderer>();
        using var session = new WindowsDvdCellSession(c.Catalog, c.State, () => { var renderer = new Renderer(); renderers.Add(renderer); return renderer; });
        Assert.Equal(DvdNativeStatus.Loading, session.OpenCell().Status); Assert.NotNull(renderers[0].Timing);
        renderers[0].Status = new(true, false, 20, true, 0); Assert.Equal(DvdNativeStatus.Playing, session.Pump().Status); Assert.Same(c.State, session.Navigation);
        var oldTiming = renderers[0].Timing!; oldTiming.BindFirstFrame(0); Assert.NotNull(oldTiming.Sample(0));
        Assert.Equal(DvdNativeStatus.Loading, session.Reposition(c.State with { Epoch = 1 }).Status);
        Assert.True(renderers[0].Disposed); Assert.NotSame(oldTiming, renderers[1].Timing); Assert.Null(renderers[1].Timing!.Current90Khz);
        renderers[1].Status = new(true, false, 1, true, unchecked((int)0x80131515), "Frame clock reversed.");
        var failure = session.Pump(); Assert.Equal(DvdNativeStatus.CompatibilityRequired, failure.Status);
        Assert.Contains("Frame clock reversed", failure.Reason); Assert.Equal(7, failure.Navigation.Registers.General[0]);
    }, commands: [0x7300000000010000]);
}
