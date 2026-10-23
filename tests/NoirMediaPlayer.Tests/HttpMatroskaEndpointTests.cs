using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.MatroskaFixture;

namespace NoirMediaPlayer.Tests;

public sealed class HttpMatroskaEndpointTests
{
    private static byte[] Metadata(byte[]? chapters, string documentType = "matroska", byte[]? infoExtras = null) =>
        Group(0x1A45DFA3, Text(0x4282, documentType)).Concat(Group(0x18538067,
            Group(0x1549A966, UInt(0x2AD7B1, 1_000_000), infoExtras ?? []),
            Element(0x1F43B675, new byte[4 * 1024 * 1024]), chapters ?? [])).ToArray();

    [Theory]
    [InlineData(false, "video/x-matroska")]
    [InlineData(true, "video/x-matroska")]
    [InlineData(false, "audio/x-matroska")]
    [InlineData(true, "audio/x-matroska")]
    [InlineData(false, "application/octet-stream")]
    [InlineData(true, "application/octet-stream")]
    public async Task HttpMatroskaEndpoints_ValidatePhysicalTimelineAndSkipMedia(bool chapters, string mime)
    {
        await using var server = new LoopbackMediaServer(Metadata(chapters ? SimpleChapters() : null), ".php", mime);
        var original = new WindowsPlaybackPlan([]);
        var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url + "?signature=KeepCase", original));
        Assert.NotNull(plan?.Chapters);
        Assert.Equal(chapters ? new long[] { 0, 4000, 8000 } : [], plan.Chapters.Starts);
        Assert.Equal("video/x-matroska", plan.DetectedContentType);
        Assert.Same(original.AutomaticSubtitles, plan.AutomaticSubtitles);
        Assert.Null(original.Chapters);
        Assert.Null(original.DetectedContentType);
        Assert.Equal(chapters ? 2 : 1, server.RangeRequests);
        Assert.True(server.BytesServed < 300_000);
        if (chapters) Assert.Equal("\"noir-test\"", server.LastIfRange);
    }

    public static IEnumerable<object[]> UnsupportedChapters() => ChapterTests.UnsupportedChapters();

    [Theory]
    [MemberData(nameof(UnsupportedChapters))]
    public async Task HttpMatroskaEndpoints_VirtualOrUnsupportedTimelinesRetainCompatibility(byte[] chapters)
    {
        await using var server = new LoopbackMediaServer(Metadata(chapters), "", "application/octet-stream");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData(0x3CB923)]
    [InlineData(0x3EB923)]
    [InlineData(0x6924)]
    public async Task HttpMatroskaEndpoints_LinkedSegmentsRetainCompatibility(uint id)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters(), infoExtras: Element(id, new byte[16])), ".bin", "video/x-matroska");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData("webm")]
    [InlineData("MATROSKA")]
    [InlineData("unknown")]
    public async Task HttpMatroskaEndpoints_OtherEbmlDocumentsRetainCompatibility(string documentType)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters(), documentType), "", "video/x-matroska");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("video/mp4")]
    [InlineData("application/vnd.apple.mpegurl")]
    public async Task HttpMatroskaEndpoints_ContradictoryMimeTypesRetainCompatibility(string mime)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters()), "", mime);
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
        Assert.Equal(1, server.RangeRequests);
    }

    [Theory]
    [InlineData(LoopbackResponse.ChangedEntity)]
    [InlineData(LoopbackResponse.ChangedLength)]
    public async Task HttpMatroskaEndpoints_InconsistentSourceCannotAdmitPartialTimeline(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Metadata(SimpleChapters()), ".download", "application/octet-stream")
        {
            Response = response,
            ResetResponseAfterRequest = true
        };
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
        // An inconsistent initial length can contradict the segment boundary
        // immediately; an entity change is detected at the tail range.
        Assert.Equal(response == LoopbackResponse.ChangedLength ? 1 : 2, server.RangeRequests);
    }
}
