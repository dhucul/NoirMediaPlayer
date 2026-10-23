using System.IO;
using NoirMediaPlayer.Services;
using Xunit;
using static NoirMediaPlayer.Tests.Mp4Fixture;

namespace NoirMediaPlayer.Tests;

public sealed class HttpChapterTests
{
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".m4v")]
    [InlineData(".m4a")]
    [InlineData(".mov")]
    [InlineData(".3gp")]
    public async Task HttpChapters_ReadTailMetadataWithoutDownloadingMedia(string extension)
    {
        var media = Atom("mdat", new byte[4 * 1024 * 1024]).Concat(MetadataMovie(Nero(0, 4000, 8000))).ToArray();
        await using var server = new LoopbackMediaServer(media, extension, "application/octet-stream");
        var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url + "?token=AbC", new WindowsPlaybackPlan([])));
        Assert.Equal(new long[] { 0, 4000, 8000 }, plan?.Chapters?.Starts);
        Assert.Equal(2, server.RangeRequests);
        Assert.True(server.BytesServed < 300_000);
        Assert.Equal("\"noir-test\"", server.LastIfRange);
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
    public async Task HttpChapters_UnavailableMetadataRetainsExistingChapterFallback(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(MetadataMovie(Nero(0, 4000, 8000)), ".mp4", "video/mp4") { Response = response };
        var original = new WindowsPlaybackPlan([]);
        var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, original)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(original, plan);
        Assert.Null(plan!.Chapters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpChapters_ChapterlessAndUnsupportedLayoutsStayDistinct(bool unsupported)
    {
        var movie = MetadataMovie(unsupported ? Highlights(Enumerable.Range(0, 101).Select(i => (uint)i).ToArray()) : null);
        await using var server = new LoopbackMediaServer(movie, ".mp4", "video/mp4");
        var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([])));
        Assert.NotNull(plan);
        if (unsupported) Assert.Null(plan.Chapters);
        else Assert.Empty(plan.Chapters!.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpChapters_QuickTimeTrackValidationRetainsVisibleText(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpChapter-" + Guid.NewGuid() + ".mp4");
        try
        {
            WriteQuickTime(path, enabled: enabled);
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".mov", "video/quicktime");
            var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([])));
            if (enabled) Assert.Null(plan);
            else Assert.Equal(new long[] { 0, 4000, 8000 }, plan?.Chapters?.Starts);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(LoopbackResponse.ChangedEntity)]
    [InlineData(LoopbackResponse.ChangedLength)]
    public async Task HttpChapters_ChangedRangesCannotProduceAChapterMap(LoopbackResponse change)
    {
        var media = Atom("mdat", new byte[1024 * 1024]).Concat(MetadataMovie(Nero(0, 4000, 8000))).ToArray();
        await using var server = new LoopbackMediaServer(media, ".mp4", "video/mp4");
        using var input = new WindowsSnapshotHttpStream(server.Url);
        server.Response = change;
        using var stream = input.AsReadStream();
        Assert.Null(Mp4Chapters.TryRead(stream, out _));
        Assert.Equal("\"noir-test\"", server.LastIfRange);
    }
}
