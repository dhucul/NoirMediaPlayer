using System.Buffers.Binary;
using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

internal static class DvdSpuFixture
{
    internal static uint[] Palette => Enumerable.Range(0, 16).Select(i => i == 1 ? 0x00eb8080u : i == 2 ? 0x0051f05au : 0x00108080u).ToArray();
    internal static byte[] Packet(int width = 4, int height = 2, int left = 10, int top = 20, bool forced = false,
        ushort? stop = null, byte[]? even = null, byte[]? odd = null)
    {
        even ??= Enumerable.Range(0, (height + 1) / 2).SelectMany(_ => new byte[] { 0, 1 }).ToArray();
        odd ??= Enumerable.Range(0, height / 2).SelectMany(_ => new byte[] { 0, 2 }).ToArray();
        // A one-line bitmap still carries a valid (unused) bottom-field address.
        if (odd.Length == 0) odd = [0, 2];
        var control = 4 + even.Length + odd.Length;
        var data = new List<byte>(new byte[4]); data.AddRange(even); data.AddRange(odd);
        data.AddRange(new byte[4]);
        data.AddRange([3, 0x32, 0x10, 4, 0xff, 0xff, 5]);
        var right = left + width - 1; var bottom = top + height - 1;
        data.AddRange([(byte)(left >> 4), (byte)((left << 4) | (right >> 8)), (byte)right,
            (byte)(top >> 4), (byte)((top << 4) | (bottom >> 8)), (byte)bottom, 6, 0, 4,
            (byte)((4 + even.Length) >> 8), (byte)(4 + even.Length), forced ? (byte)0 : (byte)1, 255]);
        var next = data.Count;
        if (stop.HasValue) data.AddRange([0, 0, 0, 0, 2, 255]);
        var bytes = data.ToArray(); U16(bytes, 0, bytes.Length); U16(bytes, 2, control);
        U16(bytes, control + 2, stop.HasValue ? next : control);
        if (stop.HasValue) { U16(bytes, next, stop.Value); U16(bytes, next + 2, next); }
        return bytes;
    }
    internal static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), checked((ushort)value));
    internal static byte[] Pes(byte[] payload, long? pts = 90_000, byte channel = 0x20)
    {
        var header = pts.HasValue ? 5 : 0;
        var bytes = new byte[10 + header + payload.Length]; bytes[2] = 1; bytes[3] = 0xbd;
        U16(bytes, 4, bytes.Length - 6); bytes[6] = 0x80; bytes[7] = pts.HasValue ? (byte)0x80 : (byte)0; bytes[8] = (byte)header;
        if (pts.HasValue)
        {
            var p = pts.Value; bytes[9] = (byte)(0x21 | ((p >> 29) & 14)); bytes[10] = (byte)(p >> 22);
            bytes[11] = (byte)((p >> 14 & 254) | 1); bytes[12] = (byte)(p >> 7); bytes[13] = (byte)((p << 1 & 254) | 1);
        }
        bytes[9 + header] = channel; payload.CopyTo(bytes, 10 + header); return bytes;
    }
    internal static byte[] Sector(byte[] pes)
    {
        var data = new byte[2048]; DvdStreamFixture.Media().AsSpan(0, 14).CopyTo(data);
        pes.CopyTo(data, 14); var pad = 14 + pes.Length;
        data[pad + 2] = 1; data[pad + 3] = 0xbe; U16(data, pad + 4, data.Length - pad - 6);
        data.AsSpan(pad + 6).Fill(0xff); return data;
    }
    internal static void Write(DvdCompletionCase c, byte[]? packet = null)
    {
        var path = Path.Combine(c.Folder, c.State.Position.Chain.Domain == DvdPlaybackDomain.Title ? "VTS_01_1.VOB" : "VIDEO_TS.VOB");
        var bytes = File.ReadAllBytes(path); var first = c.Read.Unit!.Cell.FirstSector;
        Sector(Pes(packet ?? Packet())).CopyTo(bytes, checked((int)(first + 1) * 2048)); File.WriteAllBytes(path, bytes);
    }
}

public sealed class DvdSubpictureTests
{
    [Theory]
    [InlineData(1, 1)] [InlineData(4, 2)] [InlineData(720, 576)] [InlineData(7, 5)]
    public void DecodesInterlacedFieldsAndInclusiveRectangles(int width, int height)
    {
        var frame = DvdSubpicture.Decode(DvdSpuFixture.Packet(width, height, 0, 0), 90_000).At(90_000)!;
        Assert.Equal(width, frame.Width); Assert.Equal(height, frame.Height); Assert.Equal(0, frame.Left); Assert.Equal(0, frame.Top);
        for (var y = 0; y < height; y++) Assert.All(frame.Indices.Slice(y * width, width).ToArray(), p => Assert.Equal(y % 2 == 0 ? 1 : 2, p));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(16)] [InlineData(64)]
    public void ReadsAllFourVariableLengthRunEncodings(int width)
    {
        byte[] field = width switch { 1 => [0x50], 4 => [0x11], 16 => [0x04, 0x10], _ => [0x01, 0x01] };
        var frame = DvdSubpicture.Decode(DvdSpuFixture.Packet(width, 2, even: field, odd: field), 0).At(0)!;
        Assert.All(frame.Indices.ToArray(), p => Assert.Equal(1, p));
    }

    [Fact]
    public void ByteAlignmentAndFieldOrderAreIndependentOfAbsoluteYParity()
    {
        var frame = DvdSubpicture.Decode(DvdSpuFixture.Packet(3, 3, top: 21, even: [0x56, 0x70, 0x95], odd: [0xd0]), 0).At(0)!;
        Assert.Equal(new byte[] { 1, 2, 3, 1, 1, 1, 1, 1, 1 }, frame.Indices.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UsesFullPtsAnd1024TickControlDelaysIncludingWrap(bool wrap)
    {
        var pts = wrap ? DvdSubpicture.ClockMask - 100 : 0x100000055;
        var picture = DvdSubpicture.Decode(DvdSpuFixture.Packet(forced: true, stop: 2), pts);
        Assert.Null(picture.At((pts - 1) & DvdSubpicture.ClockMask)); Assert.True(picture.At(pts)!.Forced);
        Assert.NotNull(picture.At((pts + 2047) & DvdSubpicture.ClockMask)); Assert.Null(picture.At((pts + 2048) & DvdSubpicture.ClockMask));
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.At(-1));
    }

    [Fact]
    public void PaletteOpacityAndButtonOverrideApplyOnlyInsideTheInclusiveButton()
    {
        var frame = DvdSubpicture.Decode(DvdSpuFixture.Packet(), 0).At(0)!;
        var overlay = frame.Colorize(DvdSpuFixture.Palette, 480, new(1, new(11, 20, 12, 20), 0x22228888, false));
        var bytes = overlay.Bgra.ToArray(); Assert.Equal(new byte[] { 255, 255, 255, 255 }, bytes[..4]);
        Assert.InRange(bytes[6], 250, 255); Assert.InRange(bytes[4], 0, 2); Assert.InRange(bytes[5], 0, 2); Assert.Equal(136, bytes[7]);
        Assert.Equal(bytes[4..8], bytes[8..12]); Assert.Equal(bytes[..4], bytes[12..16]);
        Assert.Equal(255, bytes[19]);
    }

    [Theory]
    [InlineData("size")] [InlineData("control")] [InlineData("cycle")] [InlineData("delay")] [InlineData("run")]
    [InlineData("truncated")] [InlineData("rectangle")] [InlineData("field")] [InlineData("end")] [InlineData("state")]
    public void RejectsMalformedPacketsBeforeExposingPixels(string fault)
    {
        var bytes = DvdSpuFixture.Packet(stop: 2); var control = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2));
        switch (fault)
        {
            case "size": bytes[1]--; break;
            case "control": DvdSpuFixture.U16(bytes, 2, 3); break;
            case "cycle": DvdSpuFixture.U16(bytes, bytes.Length - 4, control); break;
            case "delay": DvdSpuFixture.U16(bytes, control, 3); break;
            case "run": bytes[4] = 0xf0; break;
            case "truncated": DvdSpuFixture.U16(bytes, control + 19, control - 1); break;
            case "rectangle": bytes[control + 11] = 0xff; break;
            case "field": DvdSpuFixture.U16(bytes, control + 18, 3); break;
            case "end": bytes[^1] = 2; break;
            case "state": bytes[control + 4] = 4; break;
        }
        Assert.Throws<InvalidDataException>(() => DvdSubpicture.Decode(bytes, 0));
    }

    [Theory]
    [InlineData(7)] [InlineData(8)] [InlineData(254)]
    public void UnsupportedControlsCannotPartiallyRender(byte command)
    {
        var bytes = DvdSpuFixture.Packet(); bytes[^1] = command;
        Assert.Throws<NotSupportedException>(() => DvdSubpicture.Decode(bytes, 0));
    }

    [Fact]
    public void PaletteCanvasAndCancellationAreValidated()
    {
        var bytes = DvdSpuFixture.Packet(); var frame = DvdSubpicture.Decode(bytes, 0).At(0)!;
        Assert.Throws<NotSupportedException>(() => frame.Colorize([], 480));
        var palette = DvdSpuFixture.Palette; palette[1] |= 0x80000000;
        Assert.Throws<NotSupportedException>(() => frame.Colorize(palette, 480));
        Assert.Throws<NotSupportedException>(() => frame.Colorize(DvdSpuFixture.Palette, 1080));
        Assert.Throws<OperationCanceledException>(() => DvdSubpicture.Decode(bytes, 0, new CancellationToken(true)));
        bytes.AsSpan().Fill(0); Assert.All(frame.Indices.ToArray(), p => Assert.InRange(p, (byte)1, (byte)2));
    }

    [Fact]
    public void TimedColorAndOpacityChangesReuseTheDecodedIndices()
    {
        var first = DvdSpuFixture.Packet(); var control = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(2));
        var bytes = first.Concat(new byte[] { 0, 1, 0, 0, 3, 0x22, 0x22, 4, 0x88, 0x88, 255 }).ToArray();
        DvdSpuFixture.U16(bytes, 0, bytes.Length); DvdSpuFixture.U16(bytes, control + 2, first.Length); DvdSpuFixture.U16(bytes, first.Length + 2, first.Length);
        var picture = DvdSubpicture.Decode(bytes, 100); var before = picture.At(1123)!; var after = picture.At(1124)!;
        Assert.Equal(before.Indices, after.Indices); Assert.Equal(8, picture.PixelStorage);
        Assert.Equal(255, before.Colorize(DvdSpuFixture.Palette, 480).Bgra.Span[3]);
        Assert.Equal(136, after.Colorize(DvdSpuFixture.Palette, 480).Bgra.Span[3]);
    }

    [Fact]
    public void ControlSequenceBudgetStopsExcessiveUpdates()
    {
        var bytes = new byte[4 + 65 * 6]; DvdSpuFixture.U16(bytes, 0, bytes.Length); DvdSpuFixture.U16(bytes, 2, 4);
        for (var i = 0; i < 65; i++)
        { var offset = 4 + i * 6; DvdSpuFixture.U16(bytes, offset + 2, i == 64 ? offset : offset + 6); bytes[offset + 4] = 2; bytes[offset + 5] = 255; }
        Assert.Throws<NotSupportedException>(() => DvdSubpicture.Decode(bytes, 0));
    }

    [Fact]
    public void CompositesStraightAlphaWithTransparentPixelsAndScaledCoordinates()
    {
        var overlay = new DvdSubpictureOverlay(360, 240, 2, 2, 480, new byte[] { 100, 200, 250, 128, 255, 255, 255, 0, 0, 0, 0, 255, 0, 0, 0, 255 });
        var image = Enumerable.Repeat((byte)50, 1440 * 960 * 4).ToArray(); overlay.Composite(image, 1440, 960);
        var offset = (480 * 1440 + 720) * 4;
        Assert.Equal(new byte[] { 75, 125, 150, 50 }, image[offset..(offset + 4)]);
        Assert.Equal(50, image[offset + 8]); Assert.Equal(0, image[offset + 2 * 1440 * 4]); Assert.Equal(50, image[0]);
        Assert.Throws<ArgumentException>(() => overlay.Composite(new byte[4], 2, 2));
    }

    [Fact]
    public void ReadsTheAuthoredBigEndianPgcPalette() => DvdNavigationCatalogTests.WithDisc(root =>
    {
        DvdDomainFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", bytes =>
        { for (var i = 0; i < 16; i++) DvdNavFixture.U32(bytes, DvdFixture.Chain + 164 + i * 4, DvdSpuFixture.Palette[i]); });
        var chain = DvdNavigationCatalog.Read(root).Chains.Single(c => c.Key == DvdStreamFixture.TitleKey);
        Assert.Equal(DvdSpuFixture.Palette, chain.Palette);
    });
}

public sealed class DvdSubpictureStreamTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(17)] [InlineData(31)]
    public void ReassemblesFragmentsAcrossPesWithOnlyTheFirstPts(int split)
    {
        var bytes = DvdSpuFixture.Packet(); var stream = new DvdSubpictureStream();
        stream.AddPes(DvdSpuFixture.Pes(bytes[..split], pts: 0x100015f90));
        Assert.Null(stream.At(0x100015f90));
        stream.AddPes(DvdSpuFixture.Pes(bytes[split..], pts: null)); stream.Complete();
        Assert.NotNull(stream.At(0x100015f90)); Assert.NotNull(stream.AtNav(90_000));
    }

    [Theory]
    [InlineData("pts")] [InlineData("marker")] [InlineData("prefix")] [InlineData("length")] [InlineData("channel")]
    [InlineData("private")] [InlineData("scrambled")] [InlineData("incomplete")] [InlineData("changed")]
    [InlineData("trailing")] [InlineData("backward")] [InlineData("flags")]
    public void RejectsAmbiguousIncompleteOrUnsupportedStreams(string fault)
    {
        var bytes = DvdSpuFixture.Packet(); var stream = new DvdSubpictureStream();
        var error = Record.Exception(() =>
        {
            var pes = DvdSpuFixture.Pes(bytes);
            switch (fault)
            {
                case "pts": stream.AddPes(DvdSpuFixture.Pes(bytes, null)); break;
                case "marker": pes[11] &= 0xfe; stream.AddPes(pes); break;
                case "prefix": pes[9] |= 0x10; stream.AddPes(pes); break;
                case "length": pes[5]--; stream.AddPes(pes); break;
                case "channel": stream.AddPes(pes); stream.AddPes(DvdSpuFixture.Pes(bytes, channel: 0x21)); break;
                case "private": stream.AddPes(DvdSpuFixture.Pes(bytes, channel: 0x80)); break;
                case "scrambled": pes[6] |= 0x10; stream.AddPes(pes); break;
                case "incomplete": stream.AddPes(DvdSpuFixture.Pes(bytes[..1])); stream.Complete(); break;
                case "changed": stream.AddPes(DvdSpuFixture.Pes(bytes[..10])); stream.AddPes(DvdSpuFixture.Pes(bytes[10..], 91_000)); break;
                case "trailing": stream.AddPes(DvdSpuFixture.Pes(bytes.Concat(new byte[] { 0 }).ToArray())); break;
                case "backward": stream.AddPes(pes); stream.AddPes(DvdSpuFixture.Pes(bytes, 89_999)); break;
                case "flags": pes[7] = 0x40; stream.AddPes(pes); break;
            }
        });
        Assert.NotNull(error);
        if (fault is "marker" or "prefix" or "length" or "scrambled" or "changed" or "flags") Assert.IsType<InvalidDataException>(error);
        else Assert.IsType<NotSupportedException>(error);
    }

    [Fact]
    public void NewStoppedPictureDoesNotResurrectEarlierPixelsAndNewAssemblerClearsFragments()
    {
        var stream = new DvdSubpictureStream(); stream.AddPes(DvdSpuFixture.Pes(DvdSpuFixture.Packet(), 90_000));
        stream.AddPes(DvdSpuFixture.Pes(DvdSpuFixture.Packet(stop: 1), 100_000));
        Assert.NotNull(stream.At(99_999)); Assert.NotNull(stream.At(100_001)); Assert.Null(stream.At(101_024));
        Assert.Null(new DvdSubpictureStream().At(101_024));
        Assert.Throws<OperationCanceledException>(() => stream.AddPes(DvdSpuFixture.Pes(DvdSpuFixture.Packet()), new CancellationToken(true)));
    }

    [Fact]
    public void HeldMenuPreparationFiltersOnlyDecoderBytesAndLeavesNavigationAndSourceIntact() => DvdCompletionFixture.With(c =>
    {
        DvdSpuFixture.Write(c); var path = Path.Combine(c.Folder, "VIDEO_TS.VOB"); var original = File.ReadAllBytes(path);
        var media = DvdCellMedia.Prepare(c.Catalog, c.State);
        Assert.True(media.Subpictures.HasPictures); Assert.NotNull(media.Subpictures.AtNav(135_000));
        Assert.Equal(0xbd, media.Units[0].Unit!.Bytes.Span[2048 + 17]);
        using var decoder = media.OpenStream(); var bytes = new byte[(int)media.ByteCount]; decoder.Read(bytes, bytes.Length, 0);
        Assert.Equal(0xbe, bytes[2048 + 17]); Assert.Equal(media.Units[0].Unit!.Bytes.Span[..2048].ToArray(), bytes[..2048]);
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Same(c.State, c.Playback.State);
    }, menu: true, cellStill: 255, edit: chain => chain with { Palette = DvdSpuFixture.Palette });

    [Fact]
    public void MovingTitleSubpicturesStillRequireCompatibility() => DvdCompletionFixture.With(c =>
    {
        DvdSpuFixture.Write(c); Assert.Throws<NotSupportedException>(() => DvdCellMedia.Prepare(c.Catalog, c.State));
        Assert.Same(c.State, c.Playback.State);
    });

    [Fact]
    public void PacketBudgetIsBoundedEvenForInvisibleUpdates()
    {
        byte[] hidden = [0, 10, 0, 4, 0, 0, 0, 4, 2, 255]; var stream = new DvdSubpictureStream();
        for (var i = 0; i < 256; i++) stream.AddPes(DvdSpuFixture.Pes(hidden, i));
        Assert.Throws<NotSupportedException>(() => stream.AddPes(DvdSpuFixture.Pes(hidden, 256)));
    }
}
