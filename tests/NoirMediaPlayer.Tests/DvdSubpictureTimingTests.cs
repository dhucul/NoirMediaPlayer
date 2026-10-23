using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdSubpictureTimingTests
{
    internal static long Hns(long pts) => (pts * 1000 + 4) / 9;
    internal static DvdSubpictureTiming Timing(long video = 90_000, long spu = 135_000, uint start = 90_000, uint end = 270_000)
    {
        var stream = new DvdSubpictureStream(); stream.AddPes(DvdSpuFixture.Pes(DvdSpuFixture.Packet(stop: 2), spu));
        var timing = new DvdSubpictureTiming(stream, DvdSpuFixture.Palette, new DvdVideoClock(new[] { video }, start, end));
        timing.BindFirstFrame(Hns(video)); return timing;
    }

    [Fact]
    public void FirstSpuDoesNotDefineVideoZeroAndStopIsDrivenOnlyByPresentedFrames()
    {
        var timing = Timing(); Assert.Null(timing.Sample(Hns(90_000))); Assert.Null(timing.Sample(Hns(134_999)));
        var frame = timing.Sample(Hns(135_000)); Assert.NotNull(frame); Assert.Equal(135_000, timing.Current90Khz);
        for (var i = 0; i < 100; i++) Assert.Same(frame, timing.Sample(Hns(135_000)));
        Assert.Same(frame, timing.Sample(Hns(137_047))); Assert.Null(timing.Sample(Hns(137_048)));
    }

    [Theory]
    [InlineData(0L)] [InlineData(4_294_967_000L)] [InlineData(8_589_934_000L)]
    public void Full33BitClockAndWrapArePreservedWithoutFloatingPointDrift(long start)
    {
        var video = start + 20; var spu = (start + 900) & DvdSubpicture.ClockMask;
        var timing = Timing(video, spu, (uint)start, unchecked((uint)(start + 3000)));
        Assert.Null(timing.Sample(Hns(video)));
        Assert.NotNull(timing.Sample(Hns(start + 900)));
        Assert.Null(timing.Sample(Hns(start + 2948))); Assert.Equal((start + 2948) & DvdSubpicture.ClockMask, timing.Current90Khz);
    }

    [Theory]
    [InlineData(-1L)] [InlineData(0L)] [InlineData(9_000_000L)] [InlineData(31_000_000L)] [InlineData(long.MaxValue)]
    public void UnmappedNegativeOrOutsideFramesRequireCompatibility(long timestamp)
    { Assert.Throws<NotSupportedException>(() => Timing().Sample(timestamp)); }

    [Fact]
    public void DecoderReorderIsAllowedButReversedPresentationClocksAreRejected()
    {
        var clock = new DvdVideoClock(new long[] { 90_000, 99_000, 96_000 }, 90_000, 135_000); clock.BindFirstFrame(Hns(90_000));
        Assert.Equal(90_000, clock.Accept(Hns(90_000))); Assert.Equal(99_000, clock.Accept(Hns(99_000)));
        Assert.Throws<NotSupportedException>(() => clock.Accept(Hns(96_000)));
    }

    [Fact]
    public void ClockNeedsExplicitFirstFrameBindingAndCannotBeRebound()
    {
        var clock = new DvdVideoClock(new long[] { 90_000, 93_000, 96_000 }, 90_000, 135_000);
        Assert.Throws<InvalidOperationException>(() => clock.Accept(0)); clock.BindFirstFrame(500);
        Assert.Equal(90_000, clock.Accept(500)); Assert.Equal(96_000, clock.Accept(500 + Hns(6000)));
        Assert.Throws<InvalidOperationException>(() => clock.BindFirstFrame(600));
        Assert.Throws<NotSupportedException>(() => new DvdVideoClock(new long[] { 99_000, 90_000 }, 90_000, 135_000));
    }

    [Theory]
    [InlineData(0L, 0u, 0u)] [InlineData(90_000L, 90_000u, 0u)] [InlineData(89_999L, 90_000u, 135_000u)]
    [InlineData(135_001L, 90_000u, 135_000u)] [InlineData(-1L, 90_000u, 135_000u)]
    public void InvalidAuthoredTimeRangesCannotCreateAClock(long pts, uint start, uint end)
    {
        if (pts < 0) Assert.Throws<InvalidDataException>(() => new DvdVideoClock(new[] { pts }, start, end));
        else Assert.Throws<NotSupportedException>(() => new DvdVideoClock(new[] { pts }, start, end));
    }

    [Fact]
    public void MissingAndExcessiveTimestampInventoriesAreBounded()
    {
        Assert.Throws<NotSupportedException>(() => new DvdVideoClock(Array.Empty<long>(), 90_000, 135_000));
        Assert.Throws<NotSupportedException>(() => new DvdVideoClock(new long[65537], 0, 135_000));
    }

    [Fact]
    public void NativeClockOriginCanDifferFromSourceWithoutShiftingSubtitleEvents()
    {
        foreach (var nativeOrigin in new long[] { 0, 333222, 10_000_000_000 })
        {
            var stream = new DvdSubpictureStream(); stream.AddPes(DvdSpuFixture.Pes(DvdSpuFixture.Packet(), 9_135_000));
            var timing = new DvdSubpictureTiming(stream, DvdSpuFixture.Palette, new DvdVideoClock(new long[] { 9_090_000 }, 9_090_000, 9_270_000));
            timing.BindFirstFrame(nativeOrigin); Assert.Null(timing.Sample(nativeOrigin));
            Assert.Null(timing.Sample(nativeOrigin + Hns(44_999))); Assert.NotNull(timing.Sample(nativeOrigin + Hns(45_000)));
            Assert.Equal(9_135_000, timing.Current90Khz);
        }
    }

    [Theory]
    [InlineData("valid")] [InlineData("reversed")] [InlineData("overlap")] [InlineData("end")]
    public void MovingTimelineValidatesEveryNavIntervalAcrossVobus(string fault) => DvdCompletionFixture.With(c =>
    {
        WriteMoving(c); var path = Path.Combine(c.Folder, "VTS_01_1.VOB"); var bytes = File.ReadAllBytes(path);
        var first = DvdStreamFixture.Nav(100, 49, 0x80000032); var second = DvdStreamFixture.Nav(150, 49);
        DvdNavFixture.U32(first, DvdNavFixture.Pci + 16, 112_500);
        DvdNavFixture.U32(second, DvdNavFixture.Pci + 12, fault == "reversed" ? 80_000u : fault == "overlap" ? 100_000u : 112_500u);
        if (fault == "end") DvdNavFixture.U32(second, DvdNavFixture.Pci + 16, 90_000);
        first.CopyTo(bytes, 100 * 2048); second.CopyTo(bytes, 150 * 2048); File.WriteAllBytes(path, bytes);
        if (fault == "valid") Assert.NotNull(DvdCellMedia.Prepare(c.Catalog, c.State).MovingSubpictures);
        else Assert.Throws<NotSupportedException>(() => DvdCellMedia.Prepare(c.Catalog, c.State));
        Assert.Same(c.State, c.Playback.State);
    });

    [Fact]
    public void MovingCellPreparationPassesTimingAndFiltersSpuWithoutTouchingSource() => DvdCompletionFixture.With(c =>
    {
        WriteMoving(c); var path = Path.Combine(c.Folder, "VTS_01_1.VOB"); var original = File.ReadAllBytes(path);
        var media = DvdCellMedia.Prepare(c.Catalog, c.State); Assert.NotNull(media.MovingSubpictures);
        media.MovingSubpictures.BindFirstFrame(0);
        Assert.NotNull(media.MovingSubpictures.Sample(0));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Same(c.State, c.Playback.State);
        using var stream = media.OpenStream(); var bytes = new byte[(int)media.ByteCount]; stream.Read(bytes, bytes.Length, 0);
        Assert.Equal(0xbe, bytes[2048 + 17]); Assert.Equal(0xe0, bytes[4096 + 17]);
    });

    [Theory]
    [InlineData("missing")] [InlineData("late")] [InlineData("outside")] [InlineData("bad-pts")]
    public void UnreadyMovingClocksRejectBeforeOpeningTheDecoder(string fault) => DvdCompletionFixture.With(c =>
    {
        WriteMoving(c, fault);
        if (fault == "bad-pts") Assert.Throws<InvalidDataException>(() => DvdCellMedia.Prepare(c.Catalog, c.State));
        else Assert.Throws<NotSupportedException>(() => DvdCellMedia.Prepare(c.Catalog, c.State));
        Assert.Same(c.State, c.Playback.State);
    });

    internal static void WriteMoving(DvdCompletionCase c, string? fault = null)
    {
        DvdSpuFixture.Write(c); var path = Path.Combine(c.Folder, "VTS_01_1.VOB"); var bytes = File.ReadAllBytes(path);
        var pes = DvdSpuFixture.Pes([1, 2, 3], fault == "missing" ? null : fault == "outside" ? 200_000 : 90_000);
        pes[3] = 0xe0;
        if (fault == "bad-pts") pes[11] &= 0xfe;
        var sector = c.Read.Unit!.Cell.FirstSector + (fault == "late" ? 3u : 2u);
        DvdSpuFixture.Sector(pes).CopyTo(bytes, checked((int)sector * 2048)); File.WriteAllBytes(path, bytes);
    }
}
