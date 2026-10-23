using System.Buffers.Binary;
using System.IO;
using NoirMediaPlayer.Services;
using NoirMediaPlayer.Models;
using Xunit;
using static NoirMediaPlayer.Tests.Mp4Fixture;

namespace NoirMediaPlayer.Tests;

public sealed class Mp4ChapterTests
{
    private static NativeChapterMap? Read(byte[] movie, byte[]? before = null, byte[]? after = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirChapters-" + Guid.NewGuid() + ".mp4");
        try
        {
            File.WriteAllBytes(path, (before ?? []).Concat(movie).Concat(after ?? []).ToArray());
            return Mp4Chapters.TryRead(path);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Nero_ConvertsTimesSortsMarkersAndSkipsUnicodeTitles()
    {
        var map = Read(MetadataMovie(Nero(8000, 0, 4000)));
        Assert.NotNull(map);
        Assert.Equal(new long[] { 0, 4000, 8000 }, map.Starts);
        Assert.Equal(8000, map.Navigate(5500, true));
        Assert.Equal(0, map.Navigate(5500, false));
        Assert.Empty(Read(MetadataMovie())!.Starts);
        Assert.Empty(Read(MetadataMovie(Nero()))!.Starts);
    }

    [Fact]
    public void HiLight_UsesMillisecondsAndNeroTakesPrecedence()
    {
        Assert.Equal(new long[] { 1000, 4000, 8000 }, Read(MetadataMovie(Highlights(1000, 4000, 8000)))!.Starts);
        Assert.Equal(4000, Read(MetadataMovie(Highlights(1000, 4000, 8000)))!.Navigate(500, true));
        Assert.Null(Read(MetadataMovie(Highlights(1000, 4000, 8000)))!.Navigate(500, false));
        Assert.Equal(new long[] { 0, 4000, 8000 }, Read(MetadataMovie(Nero(0, 4000, 8000).Concat(Highlights(1000, 6000)).ToArray()))!.Starts);
        Assert.Equal(new long[] { 1000, 6000 }, Read(MetadataMovie(Nero().Concat(Highlights(1000, 6000)).ToArray()))!.Starts);
    }

    [Fact]
    public void ExtendedBoxesLargeMediaAndEndOfFileBoxes_DoNotRequireReadingMedia()
    {
        var movie = MetadataMovie(Nero(0, 4000, 8000));
        var extended = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(extended, 1);
        "moov"u8.CopyTo(extended.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), (ulong)(movie.Length + 8));
        Assert.Equal(3, Read(extended.Concat(movie[8..]).ToArray(), Atom("mdat", new byte[1024 * 1024]))!.Starts.Count);
        var sizeZero = Atom("mdat", "moovchapchpl"u8.ToArray());
        Array.Clear(sizeZero, 0, 4);
        Assert.Equal(3, Read(movie, after: sizeZero)!.Starts.Count);

        var path = Path.Combine(Path.GetTempPath(), "NoirChapters-" + Guid.NewGuid() + ".mp4");
        try
        {
            using (var file = File.Create(path))
            {
                Assert.True(MarkSparse(file.SafeFileHandle, 0x900C4, 0, 0, 0, 0, out _, 0));
                BinaryPrimitives.WriteUInt64BigEndian(extended.AsSpan(8), 5ul * 1024 * 1024 * 1024);
                "mdat"u8.CopyTo(extended.AsSpan(4));
                file.Write(extended);
                file.Position = 5L * 1024 * 1024 * 1024;
                file.Write(movie);
            }
            Assert.Equal(new long[] { 0, 4000, 8000 }, Mp4Chapters.TryRead(path)!.Starts);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MovieTimescaleAnd64BitDuration_AreAppliedWithoutOverflow()
    {
        Assert.Equal(3, Read(MetadataMovie(Nero(0, 4000, 8000), header: MovieHeader(true, 90_000, 1_800_000)))!.Starts.Count);
        Assert.Null(Read(MetadataMovie(Nero(0, 4000, 8000), header: MovieHeader(false, 90_000, 90_000))));
        Assert.Null(Read(MetadataMovie(Nero(0), header: MovieHeader(scale: 0))));
        Assert.Null(Read(MetadataMovie(Nero(0), header: MovieHeader(duration: 0))));
        Assert.Null(Read(MetadataMovie(Nero(0), header: MovieHeader(true, 1, ulong.MaxValue))));
        Assert.Null(Read(MetadataMovie(Nero(0), header: MovieHeader(true, 1, (ulong)long.MaxValue))));
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".m4a")]
    [InlineData(".mov")]
    [InlineData(".3gp")]
    public void LocalMp4FamilyPlanning_CarriesValidatedChaptersAndKeepsUnknownLayoutsForHandoff(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirPlan-" + Guid.NewGuid() + extension);
        var item = new PlaylistItem { Source = path, Title = "Chapters" };
        try
        {
            File.WriteAllBytes(path, MetadataMovie(Nero(0, 4000, 8000)));
            Assert.Equal(new long[] { 0, 4000, 8000 }, WindowsPlaybackPolicy.CreatePlan(item, new PlayerSettings(), 0, null)!.Chapters!.Starts);
            File.WriteAllBytes(path, MetadataMovie(trackExtras: Atom("tref", Atom("chap", UInt(2)))));
            var plan = WindowsPlaybackPolicy.CreatePlan(item, new PlayerSettings(), 0, null);
            Assert.NotNull(plan);
            Assert.Null(plan.Chapters); // A chapter click must hand the unknown format to VLC.
        }
        finally { File.Delete(path); }
    }

    public static IEnumerable<object[]> UnsupportedMovies()
    {
        yield return [MetadataMovie(Nero(0, 0))];
        yield return [MetadataMovie(Nero(20_000))];
        yield return [MetadataMovie(Highlights(0, 0))];
        yield return [MetadataMovie(Highlights(4000, 1000))];
        yield return [MetadataMovie(Atom("HMMT", UInt(101), new byte[404]))];
        yield return [MetadataMovie(Atom("HMMT", UInt(uint.MaxValue)))];
        yield return [MetadataMovie(Atom("HMMT", UInt(3), UInt(0)))];
        yield return [MetadataMovie(Nero(0).Concat(Nero(4000)).ToArray())];
        yield return [MetadataMovie(Highlights(0).Concat(Highlights(4000)).ToArray())];
        yield return [MetadataMovie(Nero(0), Atom("tref", Atom("chap", UInt(2))))];
        yield return [MetadataMovie(trackExtras: Atom("tref", Atom("chap", UInt(2))))];
        yield return [Atom("moov", MovieHeader(), Atom("trak"), Atom("mvex"))];
        yield return [Atom("moov", MovieHeader(), Atom("trak"), Atom("cmov"))];
        yield return [MetadataMovie().Concat(Atom("moof")).ToArray()];
        yield return [MetadataMovie().Concat(MetadataMovie()).ToArray()];
        yield return [Atom("moov", MovieHeader(), MovieHeader(), Atom("trak"))];
        yield return [Atom("moov", MovieHeader(), Atom("trak"), Atom("udta"), Atom("udta"))];
        yield return [Atom("moov", MovieHeader())];
        yield return [Atom("moov", Atom("trak"))];
        var versionZero = Nero(0); versionZero[8] = 0;
        yield return [MetadataMovie(versionZero)];
        var flags = Nero(0); flags[11] = 1;
        yield return [MetadataMovie(flags)];
        var truncation = Nero(0); truncation[16] = 2;
        yield return [MetadataMovie(truncation)];
        var titleLength = Nero(0); titleLength[25] = 255;
        yield return [MetadataMovie(titleLength)];
        var fractionalDuplicate = Nero(0, 1); BinaryPrimitives.WriteUInt64BigEndian(fractionalDuplicate.AsSpan(33), 9999);
        yield return [MetadataMovie(fractionalDuplicate)];
        yield return [MetadataMovie(Nero(0)[..^1])];
        yield return [MetadataMovie(Atom("chpl", new byte[70_001]))];
        yield return [Atom("moov", MovieHeader(), Atom("trak"), new byte[] { 0, 0, 0, 4, 99, 104, 112, 108 })];
    }

    [Theory]
    [MemberData(nameof(UnsupportedMovies))]
    public void UnsupportedOrMalformedChapters_CannotBecomeAnEmptyNativeMap(byte[] movie) => Assert.Null(Read(movie));

    [Fact]
    public void TruncationsAndBoxLimits_DoNotYieldPartialMaps()
    {
        var movie = MetadataMovie(Nero(0, 4000, 8000));
        for (var length = 0; length < movie.Length; length++) Assert.Null(Read(movie[..length]));
        Assert.Null(Read(movie, after: new byte[] { 1, 2, 3 }));
        var oversized = new byte[] { 0, 0, 0, 1, 109, 100, 97, 116, 255, 255, 255, 255, 255, 255, 255, 255 };
        Assert.Null(Read(movie, before: oversized));
        Assert.Null(Read(movie, before: Enumerable.Range(0, 100_001).SelectMany(_ => Atom("free")).ToArray()));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MarkSparse(Microsoft.Win32.SafeHandles.SafeFileHandle file, uint code,
        nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);
}
