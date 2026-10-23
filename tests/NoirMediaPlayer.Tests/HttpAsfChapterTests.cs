using System.IO;
using System.Runtime.InteropServices;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class HttpAsfChapterTests
{
    [Theory]
    [InlineData(".wma", false)]
    [InlineData(".wmv", true)]
    [InlineData(".asf", true)]
    public async Task HttpAsfChapters_WindowsReadsMarkersOverRangesWithoutChangingSource(string extension, bool video)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpAsf-" + Guid.NewGuid() + extension);
        try
        {
            AsfFixture.Write(path, video, 0, 4000, 8000);
            var original = File.ReadAllBytes(path);
            await using var server = new LoopbackMediaServer(original, extension, "application/octet-stream");
            var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url + "?token=AbC", new WindowsPlaybackPlan([])));
            Assert.Equal(new long[] { 0, 4000, 8000 }, plan?.Chapters?.Starts);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(server.RangeRequests >= 1);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(".wma")]
    [InlineData(".wmv")]
    [InlineData(".asf")]
    public async Task HttpAsfChapters_ValidatedChapterlessSourcesKeepAnEmptyMap(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpAsf-" + Guid.NewGuid() + extension);
        try
        {
            AsfFixture.Write(path, extension != ".wma");
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), extension, "application/octet-stream");
            var chapters = await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url));
            Assert.NotNull(chapters);
            Assert.Empty(chapters.Starts);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("long-name")]
    [InlineData("too-many")]
    public async Task HttpAsfChapters_UnsupportedMarkersCannotCreateAPartialMap(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpAsf-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            AsfFixture.Edit(path, header =>
            {
                if (kind == "duplicate") AsfFixture.Marker(header, 40_000_000);
                if (kind == "long-name") AsfFixture.Marker(header, 60_000_000, new string('A', 4097));
                if (kind == "too-many") for (var index = 0; index < 4097; index++) AsfFixture.Marker(header, 100_000_000);
            });
            var original = File.ReadAllBytes(path);
            await using var server = new LoopbackMediaServer(original, ".wma", "audio/x-ms-wma");
            Assert.Null(await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url)));
            Assert.Equal(original, File.ReadAllBytes(path));
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
    public async Task HttpAsfChapters_UnavailableRangesKeepTheExistingChapterFallback(LoopbackResponse response)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpAsf-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".wma", "audio/x-ms-wma") { Response = response };
            var plan = await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(plan);
            Assert.Null(plan.Chapters);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("rtsp://example.com/video.wmv")]
    [InlineData("https://user:secret@example.com/audio.wma")]
    [InlineData("https://example.com/playlist.asx")]
    [InlineData("not a URL")]
    public void HttpAsfChapters_UnsupportedSourcesAreDeclinedBeforeConnecting(string source) =>
        Assert.Null(WindowsAsfChapters.TryReadHttp(source));

    [Fact]
    public async Task HttpAsfChapters_InvalidAsfCannotProduceAMap()
    {
        await using var server = new LoopbackMediaServer("Not an ASF header"u8.ToArray(), ".wmv", "video/x-ms-wmv");
        Assert.Null(await Task.Run(() => WindowsAsfChapters.TryReadHttp(server.Url)));
    }

    [Theory]
    [InlineData(LoopbackResponse.Normal)]
    [InlineData(LoopbackResponse.ChangedEntity)]
    [InlineData(LoopbackResponse.ChangedLength)]
    [InlineData(LoopbackResponse.StallHeaders)]
    [InlineData(LoopbackResponse.StallBody)]
    public async Task HttpAsfChapters_WindowsReaderRespectsSourceIdentityAndTheSharedDeadline(LoopbackResponse response)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirHttpAsf-" + Guid.NewGuid() + ".wma");
        try
        {
            AsfFixture.Write(path, false, 0, 4000, 8000);
            AsfFixture.Edit(path, AddLargeMetadata);
            var media = File.ReadAllBytes(path);
            Assert.True(media.Length > 256 * 1024);
            await using var server = new LoopbackMediaServer(media, ".wma", "audio/x-ms-wma");
            using var input = new WindowsSnapshotHttpStream(server.Url, TimeSpan.FromSeconds(2));
            server.Response = response;
            var chapters = await Task.Run(() => WindowsAsfChapters.TryRead(input)).WaitAsync(TimeSpan.FromSeconds(5));
            if (response == LoopbackResponse.Normal) Assert.Equal(new long[] { 0, 4000, 8000 }, chapters?.Starts);
            else Assert.Null(chapters);
            Assert.True(server.RangeRequests >= 2);
            Assert.Equal("\"noir-test\"", server.LastIfRange);
        }
        finally { File.Delete(path); }
    }

    private static unsafe void AddLargeMetadata(nint header)
    {
        var padding = new byte[60_000];
        fixed (byte* data = padding)
            for (var index = 0; index < 6; index++)
            {
                var name = "NOIR Metadata Padding " + index;
                fixed (char* key = name)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, ushort, char*, int, byte*, ushort, int>)(*(nint**)header)[6])(
                        header, 0, key, 2, data, (ushort)padding.Length));
            }
    }
}
