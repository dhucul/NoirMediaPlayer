using System.Buffers.Binary;
using System.IO;
using System.Text;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.Mp4Fixture;

namespace NoirMediaPlayer.Tests;

public sealed class QuickTimeChapterTests
{
    private static void WithFile(Action<string> test, bool fixedSamples = false, byte[]? movieChapters = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirQuickTime-" + Guid.NewGuid() + ".mp4");
        try { WriteQuickTime(path, fixedSamples: fixedSamples, movieChapters: movieChapters); test(path); }
        finally { File.Delete(path); }
    }

    private static void AssertMarkers(string path) => Assert.Equal(new long[] { 0, 4000, 8000 }, Mp4Chapters.TryRead(path)?.Starts);
    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
    private static byte[] Changed(byte[] box, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(offset), value);
        return box;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextSampleTimingAndFixedOrVariableSizes_AreRead(bool fixedSamples) => WithFile(AssertMarkers, fixedSamples);

    [Fact]
    public void NativeChapterPrecedence_PreservesNeroAndHiLightOverQuickTime()
    {
        WithFile(path => Assert.Equal(new long[] { 0, 6000, 12000 }, Mp4Chapters.TryRead(path)?.Starts), movieChapters: Nero(0, 6000, 12000));
        WithFile(path => Assert.Equal(new long[] { 1000, 6000 }, Mp4Chapters.TryRead(path)?.Starts), movieChapters: Highlights(1000, 6000));
        WithFile(AssertMarkers, movieChapters: Nero().Concat(Highlights()).ToArray());
    }

    [Fact]
    public void ClassicTextDescriptionAndZeroCompositionOffsets_AreSupported() => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/minf/stbl/stsd", _ =>
        {
            var description = new byte[52]; description[7] = 1;
            return Atom("stsd", UInt(0), UInt(1), Atom("text", description));
        });
        RewriteQuickTime(path, "mdia/minf/stbl", box => Atom("stbl", box[8..], Atom("ctts", UInt(0), UInt(1), UInt(3), UInt(0))));
        AssertMarkers(path);
    });

    [Fact]
    public void VersionOneHeadersAnd64BitChunkOffsets_AreSupported() => WithFile(path =>
    {
        RewriteQuickTime(path, "tkhd", box =>
        {
            var header = new byte[96]; header[0] = 1; header[3] = 2;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), 3);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(28), BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(28)));
            return Atom("tkhd", header);
        });
        RewriteQuickTime(path, "mdia/mdhd", _ => Atom("mdhd", UInt(0x01000000), new byte[16], UInt(1000), U64(20_000), new byte[4]));
        RewriteQuickTime(path, "mdia/minf/stbl/stco", box => Atom("co64", UInt(0), UInt(1), U64(BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(16)))));
        AssertMarkers(path);
    });

    [Fact]
    public void MultipleChunksAndRunLengthTiming_KeepTheSameMarkers() => WithFile(path =>
    {
        uint[] sizes = [];
        RewriteQuickTime(path, "mdia/minf/stbl/stsz", box =>
        {
            sizes = Enumerable.Range(0, 3).Select(index => BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(20 + index * 4))).ToArray();
            return box;
        });
        RewriteQuickTime(path, "mdia/minf/stbl/stco", box =>
        {
            var start = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(16));
            return Atom("stco", UInt(0), UInt(3), UInt(start), UInt(start + sizes[0]), UInt(start + sizes[0] + sizes[1]));
        });
        RewriteQuickTime(path, "mdia/minf/stbl/stsc", _ => Atom("stsc", UInt(0), UInt(2), UInt(1), UInt(1), UInt(1), UInt(2), UInt(1), UInt(1)));
        RewriteQuickTime(path, "mdia/minf/stbl/stts", _ => Atom("stts", UInt(0), UInt(2), UInt(2), UInt(4000), UInt(1), UInt(12000)));
        AssertMarkers(path);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleIdentityEdits_AreSupported(bool versionOne) => WithFile(path =>
    {
        uint duration = 0;
        RewriteQuickTime(path, "tkhd", box => { duration = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(28)); return box; });
        var edit = versionOne ? Atom("elst", UInt(0x01000000), UInt(1), U64(duration), U64(0), UInt(0x10000))
            : Atom("elst", UInt(0), UInt(1), UInt(duration), UInt(0), UInt(0x10000));
        RewriteQuickTime(path, "", box => Atom("trak", box[8..], Atom("edts", edit)));
        AssertMarkers(path);
    });

    public static IEnumerable<object[]> UnsupportedTables()
    {
        yield return ["stts", Atom("stts", UInt(0), UInt(1), UInt(0), UInt(4000))];
        yield return ["stts", Atom("stts", UInt(0), UInt(1), UInt(4097), UInt(1))];
        yield return ["stts", Atom("stts", UInt(0), UInt(1), UInt(3), UInt(0))];
        yield return ["stts", Atom("stts", UInt(0), UInt(1), UInt(3), UInt(4000))];
        yield return ["stts", Atom("stts", UInt(1), UInt(1), UInt(3), UInt(4000))];
        yield return ["stsz", Atom("stsz", UInt(0), UInt(128), UInt(2))];
        yield return ["stsz", Atom("stsz", UInt(0), UInt(1), UInt(3))];
        yield return ["stsz", Atom("stsz", UInt(0), UInt(uint.MaxValue), UInt(3))];
        yield return ["stsz", Atom("stsz", UInt(0), UInt(0), UInt(3), UInt(128))];
        yield return ["stsc", Atom("stsc", UInt(0), UInt(1), UInt(2), UInt(3), UInt(1))];
        yield return ["stsc", Atom("stsc", UInt(0), UInt(1), UInt(1), UInt(0), UInt(1))];
        yield return ["stsc", Atom("stsc", UInt(0), UInt(1), UInt(1), UInt(3), UInt(2))];
        yield return ["stsc", Atom("stsc", UInt(0), UInt(1), UInt(1), UInt(4), UInt(1))];
        yield return ["stsc", Atom("stsc", UInt(0), UInt(1), UInt(1), UInt(2), UInt(1))];
        yield return ["stco", Atom("stco", UInt(0), UInt(0))];
        yield return ["stco", Atom("stco", UInt(0), UInt(1), UInt(0))];
        yield return ["stco", Atom("stco", UInt(0), UInt(1), UInt(uint.MaxValue))];
        yield return ["stco", Atom("co64", UInt(0), UInt(1), U64(ulong.MaxValue))];
        yield return ["stsd", Atom("stsd", UInt(0), UInt(2))];
        yield return ["stsd", Atom("stsd", UInt(0), UInt(1), Atom("enct", new byte[38]))];
    }

    [Theory]
    [MemberData(nameof(UnsupportedTables))]
    public void InvalidTimingSizesChunkMapsAndOffsets_RetainCompatibility(string atom, byte[] replacement) => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/minf/stbl/" + atom, _ => replacement);
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Theory]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void NonzeroCompositionOffsets_RetainCompatibility(uint offset) => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/minf/stbl", box => Atom("stbl", box[8..], Atom("ctts", UInt(0x01000000), UInt(1), UInt(3), UInt(offset))));
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Fact]
    public void ExternalReferencesVisibleTextAndUnsupportedDescriptions_RetainCompatibility() => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/minf/dinf", _ => Atom("dinf", Atom("dref", UInt(0), UInt(1), Atom("url ", UInt(0), Encoding.UTF8.GetBytes("outside.mov\0")))));
        Assert.Null(Mp4Chapters.TryRead(path));
        WriteQuickTime(path, enabled: true);
        Assert.Null(Mp4Chapters.TryRead(path, out var required));
        Assert.True(required);
        Assert.Null(WindowsPlaybackPolicy.CreatePlan(new PlaylistItem { Source = path, Title = "Visible text" }, new PlayerSettings(), 0, null));
        WriteQuickTime(path);
        RewriteQuickTime(path, "mdia/minf/stbl/stsd", box => { box[31] = 2; return box; }); // Data reference 2.
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Theory]
    [InlineData(1000u, 0x10000u)]
    [InlineData(uint.MaxValue, 0x10000u)]
    [InlineData(0u, 0x8000u)]
    public void TrimsGapsAndRateChanges_RetainCompatibility(uint start, uint rate) => WithFile(path =>
    {
        uint duration = 0;
        RewriteQuickTime(path, "tkhd", box => { duration = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(28)); return box; });
        RewriteQuickTime(path, "", box => Atom("trak", box[8..], Atom("edts", Atom("elst", UInt(0), UInt(1), UInt(duration), UInt(start), UInt(rate)))));
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(999u)]
    public void MissingAndSelfReferencingChapterTracks_RetainCompatibility(uint id) => WithFile(path =>
    {
        RewriteQuickTime(path, "tref/chap", box => Changed(box, 8, id), owner: true);
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Fact]
    public void UnknownTablesAndDuplicateHeaders_CannotProducePartialMaps() => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/minf/stbl", box => Atom("stbl", box[8..], Atom("sgpd", UInt(0))));
        Assert.Null(Mp4Chapters.TryRead(path));
        WriteQuickTime(path);
        RewriteQuickTime(path, "tkhd", box => box.Concat(box).ToArray());
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void EmptyAndTruncatedTitles_RetainCompatibility(int length) => WithFile(path =>
    {
        uint position = 0;
        RewriteQuickTime(path, "mdia/minf/stbl/stco", box => { position = BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(16)); return box; });
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.Position = position;
            var titleLength = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(titleLength, (ushort)length);
            file.Write(titleLength);
        }
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Fact]
    public void AlternateChapterListsAndDisabledOwners_RetainCompatibility() => WithFile(path =>
    {
        RewriteQuickTime(path, "tref/chap", _ => Atom("chap", UInt(3), UInt(3)), owner: true);
        Assert.Null(Mp4Chapters.TryRead(path));
        WriteQuickTime(path);
        RewriteQuickTime(path, "tkhd", box => Changed(box, 8, 2), owner: true);
        Assert.Null(Mp4Chapters.TryRead(path));
        WriteQuickTime(path);
        RewriteQuickTime(path, "tkhd", box => Changed(box, 20, 1)); // Duplicate track ID.
        Assert.Null(Mp4Chapters.TryRead(path));
    });

    [Fact]
    public void SubMillisecondDuplicateStartsAndMultipleEdits_RetainCompatibility() => WithFile(path =>
    {
        RewriteQuickTime(path, "mdia/mdhd", box => Changed(Changed(box, 20, 1_000_000), 24, 20_000_000));
        RewriteQuickTime(path, "mdia/minf/stbl/stts", _ => Atom("stts", UInt(0), UInt(3), UInt(1), UInt(1), UInt(1), UInt(1), UInt(1), UInt(19_999_998)));
        Assert.Null(Mp4Chapters.TryRead(path));
        WriteQuickTime(path);
        RewriteQuickTime(path, "", box => Atom("trak", box[8..], Atom("edts", Atom("elst", UInt(0), UInt(2), new byte[24]))));
        Assert.Null(Mp4Chapters.TryRead(path));
    });
}
