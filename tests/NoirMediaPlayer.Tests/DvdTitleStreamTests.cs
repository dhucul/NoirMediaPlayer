using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdTitleStreamTests
{
    [Fact]
    public void TitleReadsCrossFileBoundariesSkipUnusedSectorsAndFollowAuthoredCellOrder() => WithFiles(folder =>
    {
        WritePart(folder, 1, 0, 4); WritePart(folder, 2, 4, 4);
        var map = Map(new DvdCell(1, 2, 5, 90_000, 1, 1, 0, 0, 0), new DvdCell(2, 0, 0, 90_000, 1, 2, 0, 0, 0));
        using var stream = new DvdTitleStream(folder, 1, map);
        Assert.Equal(5 * 2048, stream.Length);
        var pixels = new byte[5 * 2048];
        stream.ReadExactly(pixels);
        Assert.Equal(new byte[] { 2, 3, 4, 5, 0 }, Enumerable.Range(0, 5).Select(index => pixels[index * 2048]));
        Assert.Equal(0, stream.Read(pixels));
        Assert.Equal(2u, stream.SelectChapter(1).Sector);
        Assert.Equal(2, stream.ReadByte());
        stream.SelectChapter(2);
        Assert.Equal(4 * 2048, stream.Position);
        Assert.Equal(0, stream.ReadByte());
        stream.Seek(-1, SeekOrigin.End);
        Assert.Equal(0, stream.ReadByte());
        Assert.Equal(stream.Length, stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.SelectChapter(3));
    });

    [Theory]
    [InlineData("missing")]
    [InlineData("gap")]
    [InlineData("truncated")]
    [InlineData("outside")]
    public void MissingOrInvalidVobRangesCannotBecomeTitleStreams(string fault) => WithFiles(folder =>
    {
        if (fault != "missing") WritePart(folder, 1, 0, 4);
        if (fault == "gap") WritePart(folder, 3, 4, 4);
        if (fault == "truncated") File.WriteAllBytes(Path.Combine(folder, "VTS_01_1.VOB"), [1, 2, 3]);
        var last = fault == "outside" ? 100u : 2u;
        var map = Map(new DvdCell(1, 0, last, 90_000, 1, 1, 0, 0, 0));
        if (fault == "missing") Assert.Throws<FileNotFoundException>(() => new DvdTitleStream(folder, 1, map));
        else Assert.Throws<InvalidDataException>(() => new DvdTitleStream(folder, 1, map));
    });

    [Fact]
    public void DisposalReleasesFilesAndReadsNeverChangeStoredSectorBytes() => WithFiles(folder =>
    {
        WritePart(folder, 1, 0, 4);
        var path = Path.Combine(folder, "VTS_01_1.VOB");
        var before = File.ReadAllBytes(path);
        var stream = new DvdTitleStream(folder, 1, Map(new DvdCell(1, 0, 3, 90_000, 1, 1, 0, 0, 0)));
        Assert.Throws<IOException>(() => { using var blocked = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None); });
        stream.Position = 2048; Assert.Equal(1, stream.ReadByte());
        stream.Dispose();
        Assert.False(stream.CanRead || stream.CanSeek || stream.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(before, File.ReadAllBytes(path));
    });

    private static DvdLinearTitleMap Map(params DvdCell[] cells) => new(cells,
        cells.Select((cell, index) => new DvdChapter(index + 1, 1, index + 1, index + 1)).ToArray());

    private static void WritePart(string folder, int part, int firstSector, int count)
    {
        using var file = File.Create(Path.Combine(folder, $"VTS_01_{part}.VOB"));
        for (var sector = firstSector; sector < firstSector + count; sector++) file.Write(Enumerable.Repeat((byte)sector, 2048).ToArray());
    }

    private static void WithFiles(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDvdSectors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("NoirDvdSectors-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(root, recursive: true);
        }
    }
}
