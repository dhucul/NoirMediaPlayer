using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class HttpAsfEndpointTests
{
    [Theory]
    [InlineData(true, "video/x-ms-wmv", "", true)]
    [InlineData(true, "video/x-ms-asf", ".php", true)]
    [InlineData(true, "application/vnd.ms-asf", ".bin", true)]
    [InlineData(false, "audio/x-ms-wma", "", true)]
    [InlineData(false, "application/octet-stream", ".download", true)]
    [InlineData(true, "application/octet-stream", "", true)]
    [InlineData(false, "audio/x-ms-wma", ".ashx", false)]
    [InlineData(true, "video/x-ms-asf", "", false)]
    public async Task HttpAsfEndpoints_WindowsValidatesMarkersWithoutChangingMedia(bool video, string mime, string endpoint, bool chapters)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsfEndpoint-" + Guid.NewGuid() + ".asf");
        try
        {
            AsfFixture.Write(path, video, chapters ? [0, 4000, 8000] : []);
            var original = File.ReadAllBytes(path);
            await using var server = new LoopbackMediaServer(original, endpoint, mime);
            var initial = new WindowsPlaybackPlan([]);
            var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url + "?signature=KeepCase", initial));
            Assert.NotNull(plan?.Chapters);
            Assert.Equal(chapters ? new long[] { 0, 4000, 8000 } : [], plan.Chapters.Starts);
            Assert.True(plan.UsesAsfMarkers);
            Assert.Equal("video/x-ms-asf", plan.DetectedContentType);
            Assert.Same(initial.AutomaticSubtitles, plan.AutomaticSubtitles);
            Assert.False(initial.UsesAsfMarkers);
            Assert.Null(initial.Chapters);
            Assert.Equal(original, File.ReadAllBytes(path));
            // The Windows reader may also inspect the video's index. Keep its
            // traffic bounded without depending on its internal read schedule.
            Assert.InRange(server.RangeRequests, 1, 4);
            Assert.InRange(server.BytesServed, 1, 1024 * 1024);
            Assert.True(server.BytesServed < original.Length / 2);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("long-name")]
    [InlineData("too-many")]
    public async Task HttpAsfEndpoints_UnvalidatedMarkersCannotAdmitAnUnnamedSource(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsfEndpoint-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            AsfFixture.Edit(path, header =>
            {
                if (kind == "duplicate") AsfFixture.Marker(header, 40_000_000);
                if (kind == "long-name") AsfFixture.Marker(header, 60_000_000, new string('A', 4097));
                if (kind == "too-many") for (var index = 0; index < 4097; index++) AsfFixture.Marker(header, 100_000_000);
            });
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), "", "audio/x-ms-wma");
            Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
            Assert.Null(await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("signature-only")]
    [InlineData("wrong-guid")]
    [InlineData("truncated")]
    [InlineData("wrong-mime")]
    public async Task HttpAsfEndpoints_HeaderAndMimeAloneCannotAdmitPlayback(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsfEndpoint-" + Guid.NewGuid() + ".wmv");
        try
        {
            AsfFixture.Write(path, true, 0, 4000, 8000);
            var media = File.ReadAllBytes(path);
            if (kind == "signature-only") media = media[..16];
            if (kind == "wrong-guid") media[15] ^= 1;
            if (kind == "truncated") media = media[..64];
            await using var server = new LoopbackMediaServer(media, ".php", kind == "wrong-mime" ? "video/mp4" : "application/octet-stream");
            Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
            Assert.Null(await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("matroska")]
    public async Task HttpAsfEndpoints_CompatibilityInspectionCannotAdoptAnotherContainersChapters(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsfEndpoint-" + Guid.NewGuid());
        try
        {
            if (kind == "mp4") Mp4Fixture.Write(path, Mp4Fixture.Nero(0, 4000, 8000));
            else MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), "", "application/octet-stream");
            Assert.Null(await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url)));
            Assert.Equal(1, server.RangeRequests);
        }
        finally { File.Delete(path); }
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
    public async Task HttpAsfEndpoints_UnavailableRangesRetainCompatibility(LoopbackResponse response)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirAsfEndpoint-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), "", "audio/x-ms-wma") { Response = response };
            Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { File.Delete(path); }
    }
}
