using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class DvdDiscInspectionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OwnReaderResolvesTitlesChaptersAndPhysicalCellsWithoutMediaOrVlc(int sourceKind) => WithDisc(root =>
    {
        DvdFixture.Write(root);
        var original = Directory.GetFiles(Path.Combine(root, "VIDEO_TS")).ToDictionary(path => path, File.ReadAllBytes);
        var source = sourceKind == 0 ? root : sourceKind == 1 ? Path.Combine(root, "VIDEO_TS") : Path.Combine(root, "VIDEO_TS", "VIDEO_TS.IFO");
        Assert.True(DvdDiscInspection.IsDvdSource(source));
        var report = DvdDiscInspection.Read(source);
        Assert.False(report.PlaybackVerified || report.MainTitleSelected);
        var title = Assert.Single(report.Titles);
        Assert.Equal(1, title.Number); Assert.Equal(1, title.TitleSet); Assert.Equal(2, title.DeclaredChapters);
        Assert.Null(title.Error); Assert.Null(title.CompatibilityReason);
        var map = Assert.IsType<DvdLinearTitleMap>(title.LinearMap);
        Assert.Equal(30_000, map.DurationMilliseconds);
        Assert.Equal(new DvdChapterTarget(1, 1, 100, 0), map.SelectChapter(1));
        Assert.Equal(new DvdChapterTarget(2, 3, 300, 1_800_000), map.SelectChapter(2));
        Assert.Equal(new uint[] { 199, 299, 399 }, map.Cells.Select(cell => cell.LastSector));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SelectChapter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SelectChapter(3));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        Assert.Equal(300u, json.RootElement.GetProperty("Titles")[0].GetProperty("LinearMap").GetProperty("Chapters")[1].GetProperty("Sector").GetUInt32());
        foreach (var entry in original) Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
    });

    [Fact]
    public void BadPrimaryInformationFilesUseCompleteIndependentBackups() => WithDisc(root =>
    {
        DvdFixture.Write(root, backups: true);
        DvdFixture.Edit(root, "VIDEO_TS.IFO", data => data[0] = 0);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data => DvdFixture.U32(data, 0xc8, uint.MaxValue));
        var report = DvdDiscInspection.Read(root);
        Assert.Equal("VIDEO_TS.BUP", report.ManagerFile);
        var title = Assert.Single(report.Titles);
        Assert.Equal("VTS_01_0.BUP", title.MetadataFile);
        Assert.NotNull(title.LinearMap);
    });

    [Theory]
    [InlineData("angle")]
    [InlineData("vm")]
    [InlineData("parental")]
    [InlineData("uop")]
    [InlineData("hold")]
    [InlineData("random")]
    [InlineData("interleaved")]
    [InlineData("angle-cell")]
    [InlineData("duration")]
    public void UnimplementedAuthoredNavigationHasMetadataButNoLinearMap(string feature) => WithDisc(root =>
    {
        DvdFixture.Write(root);
        if (feature is "angle" or "vm" or "parental") DvdFixture.Edit(root, "VIDEO_TS.IFO", data =>
        {
            if (feature == "angle") data[DvdFixture.TitleTable + 9] = 2;
            if (feature == "vm") data[DvdFixture.TitleTable + 8] = 0x10;
            if (feature == "parental") DvdFixture.U16(data, DvdFixture.TitleTable + 12, 1);
        });
        else DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            if (feature == "uop") DvdFixture.U32(data, DvdFixture.Chain + 8, 2);
            if (feature == "hold") data[DvdFixture.Chain + DvdFixture.Playback + 2] = 255;
            if (feature == "random") data[DvdFixture.Chain + 162] = 1;
            if (feature == "interleaved") data[DvdFixture.Chain + DvdFixture.Playback] = 4;
            if (feature == "angle-cell") data[DvdFixture.Chain + DvdFixture.Playback] = 0x50;
            if (feature == "duration") data[DvdFixture.Chain + 6] = 0x40;
        });
        var title = Assert.Single(DvdDiscInspection.Read(root).Titles);
        Assert.Null(title.Error);
        Assert.Equal(2, title.Chapters.Count);
        Assert.Null(title.LinearMap);
        Assert.NotNull(title.CompatibilityReason);
    });

    [Theory]
    [InlineData("pointer")]
    [InlineData("table-length")]
    [InlineData("program")]
    [InlineData("pgc")]
    [InlineData("cell-range")]
    [InlineData("bcd")]
    [InlineData("chapter-count")]
    public void MalformedTitleMetadataIsReportedWithoutInventingAPlaybackMap(string fault) => WithDisc(root =>
    {
        DvdFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            if (fault == "pointer") DvdFixture.U32(data, 0xcc, uint.MaxValue);
            if (fault == "table-length") DvdFixture.U32(data, DvdFixture.PartTable + 4, uint.MaxValue);
            if (fault == "program") DvdFixture.U16(data, DvdFixture.PartTable + 18, 3);
            if (fault == "pgc") DvdFixture.U16(data, DvdFixture.PartTable + 16, 2);
            if (fault == "cell-range") DvdFixture.U32(data, DvdFixture.Chain + DvdFixture.Playback + 20, 50);
            if (fault == "bcd") data[DvdFixture.Chain + DvdFixture.Playback + 6] = 0x1a;
            if (fault == "chapter-count") DvdFixture.U32(data, DvdFixture.PartTable + 4, 15);
        });
        var title = Assert.Single(DvdDiscInspection.Read(root).Titles);
        Assert.Null(title.LinearMap);
        Assert.NotNull(title.Error);
    });

    [Fact]
    public void MissingTitleSetAndCancellationAreExplicit() => WithDisc(root =>
    {
        DvdFixture.Write(root);
        File.Delete(Path.Combine(root, "VIDEO_TS", "VTS_01_0.IFO"));
        Assert.NotNull(Assert.Single(DvdDiscInspection.Read(root).Titles).Error);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DvdDiscInspection.Read(root, cancelled.Token));
    });

    [Fact]
    public void VmCommandTablesAreRecognizedButNeverExecutedOrFlattened() => WithDisc(root =>
    {
        DvdFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            DvdFixture.U32(data, DvdFixture.ChainTable + 4, 355);
            DvdFixture.U16(data, DvdFixture.Chain + 228, 324);
            DvdFixture.U16(data, DvdFixture.Chain + 324, 1);
            DvdFixture.U16(data, DvdFixture.Chain + 330, 15);
        });
        var title = Assert.Single(DvdDiscInspection.Read(root).Titles);
        Assert.Null(title.Error);
        Assert.Null(title.LinearMap);
        Assert.Contains("VM commands", title.CompatibilityReason);
    });

    [Fact]
    public void ChaptersSpanningProgramChainsKeepTheirDistinctAddresses() => WithDisc(root =>
    {
        DvdFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            var chain = data.AsSpan(DvdFixture.Chain, 324).ToArray();
            DvdFixture.U16(data, DvdFixture.ChainTable, 2);
            DvdFixture.U32(data, DvdFixture.ChainTable + 4, 671);
            DvdFixture.U32(data, DvdFixture.ChainTable + 12, 24);
            DvdFixture.U32(data, DvdFixture.ChainTable + 20, 348);
            chain.CopyTo(data, DvdFixture.ChainTable + 24);
            chain.CopyTo(data, DvdFixture.ChainTable + 348);
            DvdFixture.U16(data, DvdFixture.PartTable + 16, 2);
            DvdFixture.U16(data, DvdFixture.PartTable + 18, 1);
        });
        var title = Assert.Single(DvdDiscInspection.Read(root).Titles);
        Assert.Null(title.Error);
        Assert.Equal(new[] { new DvdChapter(1, 1, 1, 1), new DvdChapter(2, 2, 1, 1) }, title.Chapters);
        Assert.Null(title.LinearMap);
        Assert.Contains("one sequential", title.CompatibilityReason);
    });

    [Fact]
    public void OverlappingProgramChainSectionsAreRejected() => WithDisc(root =>
    {
        DvdFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data => DvdFixture.U16(data, DvdFixture.Chain + 234, DvdFixture.Playback));
        var title = Assert.Single(DvdDiscInspection.Read(root).Titles);
        Assert.Null(title.LinearMap);
        Assert.Contains("overlap", title.Error);
    });

    [Fact]
    public void NtscCellDurationsAccumulateBeforeConvertingToMilliseconds() => WithDisc(root =>
    {
        DvdFixture.Write(root);
        DvdFixture.Edit(root, "VTS_01_0.IFO", data =>
        {
            new byte[] { 0, 0, 0, 0xc3 }.CopyTo(data, DvdFixture.Chain + 4);
            for (var index = 0; index < 3; index++)
                new byte[] { 0, 0, 0, 0xc1 }.CopyTo(data, DvdFixture.Chain + DvdFixture.Playback + index * 24 + 4);
        });
        var map = Assert.IsType<DvdLinearTitleMap>(Assert.Single(DvdDiscInspection.Read(root).Titles).LinearMap);
        Assert.Equal(9009, map.Duration90Khz);
        Assert.Equal(100, map.DurationMilliseconds);
        Assert.Equal(6006, map.SelectChapter(2).Time90Khz);
    });

    [Theory]
    [InlineData("https://example.com/disc")]
    [InlineData("VIDEO_TS")]
    [InlineData("\\\\server\\disc")]
    public void OnlyExplicitLocalSourcesAreRead(string path) => Assert.Throws<ArgumentException>(() => DvdDiscInspection.Read(path));

    [Theory]
    [InlineData(0x00, 0x00, 0x01, 0x41, 93_600L)]
    [InlineData(0x00, 0x00, 0x01, 0xc1, 93_003L)]
    [InlineData(0x01, 0x23, 0x45, 0x40, 452_250_000L)]
    public void IfoTimesUseBcdAndExactPalOrNtscTicks(byte hours, byte minutes, byte seconds, byte frames, long expected)
        => Assert.Equal(expected, DvdIfoReader.ReadTime(new[] { hours, minutes, seconds, frames }));

    [Theory]
    [InlineData(0x6a, 0x40)]
    [InlineData(0x60, 0x40)]
    [InlineData(0x01, 0x65)]
    [InlineData(0x01, 0xde)]
    [InlineData(0x01, 0x00)]
    public void InvalidBcdAndUnsupportedFrameRatesAreRejected(byte seconds, byte frames) =>
        Assert.Throws<InvalidDataException>(() => DvdIfoReader.ReadTime(new byte[] { 0, 0, seconds, frames }));

    private static void WithDisc(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDvdMetadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("NoirDvdMetadata-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(root, recursive: true);
        }
    }
}
