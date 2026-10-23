using System.Diagnostics;
using System.Runtime.InteropServices;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class HttpSnapshotStreamTests
{
    private const int BlockSize = 256 * 1024;
    private static readonly byte[] Media = Enumerable.Range(0, BlockSize * 6).Select(value => (byte)(value % 251)).ToArray();

    [Fact]
    public async Task HttpSnapshotStream_SeeksAcrossRangesAndCachesWithoutDownloadingTheWholeSource()
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4");
        using var input = new WindowsSnapshotHttpStream(server.Url);
        Assert.Equal(Media.Length, input.Length);
        Assert.Equal("video/mp4", input.ContentType);
        input.Stat(out var stat, 1);
        Assert.Equal(Media.Length, stat.cbSize);
        Assert.Equal(Media[..16], Read(input, 16));
        input.Seek(BlockSize - 20, 1, 0);
        Assert.Equal(Media[(BlockSize - 4)..(BlockSize + 12)], Read(input, 16));
        input.Seek(-37, 2, 0);
        Assert.Equal(Media[^37..], Read(input, 64));
        Assert.Empty(Read(input, 64));
        input.Seek(0, 0, 0);
        Assert.Equal(Media[..16], Read(input, 16));
        Assert.Equal(3, server.RangeRequests);
        Assert.Equal(BlockSize * 3, server.BytesServed);
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
    public async Task HttpSnapshotStream_DeclinesUnsupportedOrInconsistentResponses(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4") { Response = response };
        Assert.ThrowsAny<Exception>(() => new WindowsSnapshotHttpStream(server.Url));
    }

    [Theory]
    [InlineData(LoopbackResponse.ChangedEntity)]
    [InlineData(LoopbackResponse.ChangedLength)]
    public async Task HttpSnapshotStream_RejectsAChangedSourceBetweenRanges(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4");
        using var input = new WindowsSnapshotHttpStream(server.Url);
        server.Response = response;
        input.Seek(BlockSize, 0, 0);
        Assert.ThrowsAny<Exception>(() => Read(input, 16));
        Assert.Equal("\"noir-test\"", server.LastIfRange);
    }

    [Theory]
    [InlineData(LoopbackResponse.StallHeaders)]
    [InlineData(LoopbackResponse.StallBody)]
    public async Task HttpSnapshotStream_DeadlineCancelsHeadersAndBodyReads(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4") { Response = response };
        var clock = Stopwatch.StartNew();
        await Task.Run(() => Assert.ThrowsAny<OperationCanceledException>(() =>
            new WindowsSnapshotHttpStream(server.Url, TimeSpan.FromMilliseconds(250))));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.1, 3);
    }

    [Fact]
    public async Task HttpSnapshotStream_ReadsShareOneDeadlineAndByteBudget()
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4");
        using (var input = new WindowsSnapshotHttpStream(server.Url, byteLimit: BlockSize))
        {
            input.Seek(BlockSize, 0, 0);
            Assert.Throws<NotSupportedException>(() => Read(input, 1));
            Assert.Equal(BlockSize, server.BytesServed); // The rejected response body is not downloaded.
        }
        using var timed = new WindowsSnapshotHttpStream(server.Url, TimeSpan.FromMilliseconds(250));
        await Task.Delay(300);
        Assert.ThrowsAny<OperationCanceledException>(() => Read(timed, 1));
    }

    [Fact]
    public async Task HttpSnapshotStream_DisposeCancelsAnOutstandingRead()
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4");
        var input = new WindowsSnapshotHttpStream(server.Url);
        try
        {
            server.Response = LoopbackResponse.StallBody;
            input.Seek(BlockSize, 0, 0);
            var reading = Task.Run(() => Assert.ThrowsAny<Exception>(() => Read(input, 16)));
            var clock = Stopwatch.StartNew();
            while (server.Requests < 2 && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            Assert.Equal(2, server.Requests);
            await Task.Run(input.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            await reading.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Throws<ObjectDisposedException>(() => Read(input, 1));
        }
        finally { input.Dispose(); }
    }

    [Fact]
    public async Task HttpSnapshotStream_RedirectsRetainRangeValidation()
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "application/octet-stream") { Response = LoopbackResponse.Redirect };
        using var input = new WindowsSnapshotHttpStream(server.Url);
        input.Seek(-20, 2, 0);
        Assert.Equal(Media[^20..], Read(input, 20));
        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task HttpSnapshotStream_ChangedRedirectTargetCannotMixResources()
    {
        await using var server = new LoopbackMediaServer(Media, ".mp4", "video/mp4") { Response = LoopbackResponse.Redirect };
        using var input = new WindowsSnapshotHttpStream(server.Url);
        server.Response = LoopbackResponse.Normal;
        input.Seek(BlockSize, 0, 0);
        Assert.Throws<System.IO.IOException>(() => Read(input, 16));
    }

    [Theory]
    [InlineData("file:///C:/media.mp4")]
    [InlineData("rtsp://example.com/media.mp4")]
    [InlineData("https://user:secret@example.com/media.mp4")]
    [InlineData("not a URL")]
    public void HttpSnapshotStream_UnsupportedLocationsAreDeclinedBeforeConnecting(string source) =>
        Assert.Throws<NotSupportedException>(() => new WindowsSnapshotHttpStream(source));

    private static byte[] Read(WindowsSnapshotHttpStream input, int count)
    {
        var buffer = new byte[count];
        var read = Marshal.AllocHGlobal(4);
        try
        {
            input.Read(buffer, count, read);
            return buffer[..Marshal.ReadInt32(read)];
        }
        finally { Marshal.FreeHGlobal(read); }
    }
}
