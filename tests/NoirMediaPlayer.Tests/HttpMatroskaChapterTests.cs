using System.IO;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.MatroskaFixture;

namespace NoirMediaPlayer.Tests;

public sealed class HttpMatroskaChapterTests
{
    private static byte[] Metadata(byte[]? chapters, byte[]? infoExtras = null) =>
        Group(0x1A45DFA3, Text(0x4282, "matroska")).Concat(Group(0x18538067,
            Group(0x1549A966, UInt(0x2AD7B1, 1_000_000), infoExtras ?? []),
            Element(0x1F43B675, new byte[4 * 1024 * 1024]), chapters ?? [])).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpMatroskaChapters_ValidateTailChaptersAndChapterlessFilesWithoutReadingMedia(bool chapters)
    {
        await using var server = new LoopbackMediaServer(Metadata(chapters ? SimpleChapters() : null), ".mkv", "video/x-matroska");
        var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url + "?token=AbC", new WindowsPlaybackPlan([])));
        Assert.NotNull(plan?.Chapters);
        Assert.Equal(chapters ? new long[] { 0, 4000, 8000 } : [], plan.Chapters.Starts);
        Assert.Equal(chapters ? 2 : 1, server.RangeRequests);
        Assert.True(server.BytesServed < 300_000);
        if (chapters) Assert.Equal("\"noir-test\"", server.LastIfRange);
    }

    public static IEnumerable<object[]> UnsupportedChapters() => ChapterTests.UnsupportedChapters();

    [Theory]
    [MemberData(nameof(UnsupportedChapters))]
    public async Task HttpMatroskaChapters_ComplexTimelinesCannotStartNativePlayback(byte[] chapters)
    {
        await using var server = new LoopbackMediaServer(Metadata(chapters), ".mkv", "video/x-matroska");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData(0x3CB923)]
    [InlineData(0x3EB923)]
    [InlineData(0x6924)]
    public async Task HttpMatroskaChapters_LinkedSegmentsAndChapterCodecsRequireCompatibility(uint id)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters(), Element(id, new byte[16])), ".mkv", "video/x-matroska");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData(LoopbackResponse.IgnoreRanges)]
    [InlineData(LoopbackResponse.NotFound)]
    [InlineData(LoopbackResponse.WrongRange)]
    [InlineData(LoopbackResponse.Truncated)]
    [InlineData(LoopbackResponse.Compressed)]
    [InlineData(LoopbackResponse.UnknownLength)]
    [InlineData(LoopbackResponse.ExcessChunked)]
    [InlineData(LoopbackResponse.StallHeaders)]
    [InlineData(LoopbackResponse.StallBody)]
    public async Task HttpMatroskaChapters_UnavailableRangesCannotAdmitAnUnvalidatedTimeline(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters()), ".mkv", "video/x-matroska") { Response = response };
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(LoopbackResponse.ChangedEntity)]
    [InlineData(LoopbackResponse.ChangedLength)]
    public async Task HttpMatroskaChapters_ChangedSourceCannotProduceAPartialMap(LoopbackResponse change)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters()), ".mkv", "video/x-matroska");
        using var input = new WindowsSnapshotHttpStream(server.Url);
        server.Response = change;
        using var stream = input.AsReadStream();
        Assert.Null(MatroskaChapters.TryRead(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpMatroskaChapters_MalformedOrTruncatedMetadataCannotAdmitPlayback(bool truncated)
    {
        var bytes = truncated ? Metadata(SimpleChapters())[..^1] : new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0xFF };
        await using var server = new LoopbackMediaServer(bytes, ".mkv", "video/x-matroska");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }
}
