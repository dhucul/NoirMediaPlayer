using System.IO;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.MatroskaFixture;

namespace NoirMediaPlayer.Tests;

public sealed class ChapterTests
{
    private static NativeChapterMap? Read(byte[]? chapters, byte[]? info = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirChapters-" + Guid.NewGuid() + ".mkv");
        try { Write(path, false, chapters, info); return MatroskaChapters.TryRead(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SimpleChapters_UseNanosecondsAndKeepNavigationBoundaries()
    {
        var map = Read(SimpleChapters());
        Assert.NotNull(map);
        Assert.Equal(new long[] { 0, 4000, 8000 }, map.Starts);
        Assert.Equal(4000, map.Navigate(1500, true));
        Assert.Equal(8000, map.Navigate(5500, true));
        Assert.Equal(0, map.Navigate(5500, false));
        Assert.Equal(4000, map.Navigate(8000, false));
        Assert.Null(map.Navigate(1500, false));
        Assert.Null(map.Navigate(8000, true));
        Assert.Empty(Read(null)!.Starts);
    }

    public static IEnumerable<object[]> UnsupportedChapters()
    {
        byte[] Atom(params byte[][] more) => Group(0xB6, new[] { UInt(0x73C4, 1), UInt(0x91, 0) }.Concat(more).ToArray());
        byte[] Edition(params byte[][] more) => Group(0x45B9, new[] { Atom() }.Concat(more).ToArray());
        yield return [Group(0x1043A770, Edition(UInt(0x45DD, 1)))];
        yield return [Group(0x1043A770, Edition(UInt(0x45BD, 1)))];
        yield return [Group(0x1043A770, Edition(), Edition())];
        foreach (var feature in new[]
        {
            UInt(0x98, 1), UInt(0x4598, 0), Element(0x6E67, new byte[16]), UInt(0x6EBC, 2),
            Group(0x6944), Group(0x8F, UInt(0x89, 1)), Atom(), UInt(0x91, 100), UInt(0x4588, 1)
        }) yield return [Group(0x1043A770, Group(0x45B9, Atom(feature)))];
        yield return [Group(0x1043A770, Group(0x45B9, Group(0xB6, UInt(0x73C4, 1))))];
        yield return [Group(0x1043A770, Group(0x45B9, Atom(), Atom()))];
        yield return [Group(0x1043A770, Group(0x45B9))];
    }

    [Theory]
    [MemberData(nameof(UnsupportedChapters))]
    public void ComplexOrMalformedChapters_RetainCompatibility(byte[] chapters) => Assert.Null(Read(chapters));

    [Theory]
    [InlineData(0x3CB923)]
    [InlineData(0x3EB923)]
    [InlineData(0x6924)]
    public void LinkedSegmentsAndChapterCodecs_RetainCompatibility(uint id) =>
        Assert.Null(Read(SimpleChapters(), Element(id, new byte[16])));

    [Fact]
    public void TruncatedAndUnknownSizedMetadata_CannotProducePartialChapterMaps()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirChapters-" + Guid.NewGuid() + ".mkv");
        try
        {
            Write(path, false, SimpleChapters());
            var file = File.ReadAllBytes(path);
            File.WriteAllBytes(path, file[..^1]);
            Assert.Null(MatroskaChapters.TryRead(path));
            File.WriteAllBytes(path, file[..100]);
            Assert.Null(MatroskaChapters.TryRead(path));
            File.WriteAllBytes(path, [0x1A, 0x45, 0xDF, 0xA3, 0xFF]);
            Assert.Null(MatroskaChapters.TryRead(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChapterMap_DoesNotRetainTheCallersMutableArray()
    {
        long[] starts = [0, 4000];
        var map = new NativeChapterMap(starts);
        starts[1] = 8000;
        Assert.Equal(4000, map.Navigate(0, true));
    }

    [Fact]
    public void ChaptersAfterMediaAndUnknownSizeSegments_AreReadWithoutDecodingMedia()
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirChapters-" + Guid.NewGuid() + ".mkv");
        try
        {
            Write(path, false, SimpleChapters(), chaptersAtEnd: true);
            Assert.Equal(new long[] { 0, 4000, 8000 }, MatroskaChapters.TryRead(path)!.Starts);
            var file = File.ReadAllBytes(path);
            var segment = file.AsSpan().IndexOf(new byte[] { 0x18, 0x53, 0x80, 0x67 });
            Assert.True(segment > 0);
            var size = segment + 4;
            var width = 1;
            var marker = 0x80;
            while ((file[size] & marker) == 0) { width++; marker >>= 1; }
            file[size] = (byte)(marker | (marker - 1));
            Array.Fill(file, (byte)0xFF, size + 1, width - 1);
            File.WriteAllBytes(path, file);
            Assert.Equal(new long[] { 0, 4000, 8000 }, MatroskaChapters.TryRead(path)!.Starts);
        }
        finally { File.Delete(path); }
    }
}
