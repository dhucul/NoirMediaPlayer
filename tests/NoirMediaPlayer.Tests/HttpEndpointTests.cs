using System.Buffers.Binary;
using System.IO;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed class HttpEndpointTests
{
    private static byte[] Riff(string kind, int size = 4 * 1024 * 1024)
    {
        var bytes = new byte[size];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        System.Text.Encoding.ASCII.GetBytes(kind).CopyTo(bytes, 8);
        return bytes;
    }

    [Theory]
    [InlineData("WAVE", "audio/wav")]
    [InlineData("AVI ", "video/x-msvideo")]
    [InlineData("WAVE", "application/octet-stream")]
    [InlineData("AVI ", "application/octet-stream")]
    public async Task HttpEndpoints_RiffIdentificationIsBoundedAndSkipsMedia(string kind, string mime)
    {
        await using var server = new LoopbackMediaServer(Riff(kind), "", mime);
        var original = new WindowsPlaybackPlan([]);
        var plan = await Task.Run(() => WindowsHttpMediaProbe.Inspect(server.Url, original));
        Assert.Equal(kind == "WAVE" ? "audio/wav" : "video/x-msvideo", plan?.DetectedContentType);
        Assert.Null(plan?.Chapters);
        Assert.Equal(1, server.RangeRequests);
        Assert.Equal(256 * 1024, server.BytesServed);
    }

    [Fact]
    public async Task HttpEndpoints_Mp4TailChaptersAreValidatedWithoutDownloadingMedia()
    {
        var media = Mp4Fixture.Atom("ftyp", "isom0000isom"u8.ToArray())
            .Concat(Mp4Fixture.Atom("mdat", new byte[4 * 1024 * 1024]))
            .Concat(Mp4Fixture.MetadataMovie(Mp4Fixture.Nero(0, 4000, 8000))).ToArray();
        await using var server = new LoopbackMediaServer(media, ".php", "video/mp4");
        var plan = await Task.Run(() => WindowsHttpMediaProbe.Inspect(server.Url + "?file=AbC", new WindowsPlaybackPlan([])));
        Assert.Equal(new long[] { 0, 4000, 8000 }, plan?.Chapters?.Starts);
        Assert.Equal(2, server.RangeRequests);
        Assert.True(server.BytesServed < 300_000);
        Assert.Equal("\"noir-test\"", server.LastIfRange);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("hls")]
    [InlineData("dash")]
    [InlineData("asf")]
    [InlineData("matroska")]
    [InlineData("flac")]
    [InlineData("mp4-signature-only")]
    [InlineData("riff-length")]
    [InlineData("riff-unknown")]
    [InlineData("riff-wrong-mime")]
    [InlineData("mp4-wrong-mime")]
    [InlineData("short")]
    public async Task HttpEndpoints_UncertainContentRetainsCompatibility(string kind)
    {
        var bytes = kind switch
        {
            "html" => "<!DOCTYPE html><html><body>Video</body></html>"u8.ToArray(),
            "hls" => "#EXTM3U\n#EXT-X-TARGETDURATION:5\nmedia.ts\n"u8.ToArray(),
            "dash" => "<?xml version='1.0'?><MPD></MPD>"u8.ToArray(),
            "asf" => new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0, 0xAA, 0, 0x62, 0xCE, 0x6C },
            "matroska" => MatroskaFixture.Group(0x1A45DFA3, MatroskaFixture.Text(0x4282, "matroska")),
            "flac" => "fLaCunsupported-endpoint-container"u8.ToArray(),
            "mp4-signature-only" or "mp4-wrong-mime" => Mp4Fixture.Atom("ftyp", "isom0000isom"u8.ToArray()),
            "riff-unknown" => Riff("WEBP", 128),
            "riff-length" => Riff("WAVE", 128)[..^1],
            "riff-wrong-mime" => Riff("WAVE", 128),
            _ => new byte[] { 1, 2, 3 }
        };
        await using var server = new LoopbackMediaServer(bytes, "", kind is "riff-wrong-mime" or "mp4-wrong-mime" ? "video/x-matroska" : "application/octet-stream");
        Assert.Null(await Task.Run(() => WindowsHttpChapters.Inspect(server.Url, new WindowsPlaybackPlan([]))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpEndpoints_QuickTimeVisibleTextKeepsCompatibility(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirEndpoint-" + Guid.NewGuid() + ".mp4");
        try
        {
            Mp4Fixture.WriteQuickTime(path, enabled: enabled);
            await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), "", "video/mp4");
            var plan = await Task.Run(() => WindowsHttpMediaProbe.Inspect(server.Url, new WindowsPlaybackPlan([])));
            if (enabled) Assert.Null(plan);
            else Assert.Equal(new long[] { 0, 4000, 8000 }, plan?.Chapters?.Starts);
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
    public async Task HttpEndpoints_UnavailableRangesCannotAdmitUnknownContent(LoopbackResponse response)
    {
        await using var server = new LoopbackMediaServer(Riff("WAVE", 1024), "", "audio/wav") { Response = response };
        Assert.Null(await Task.Run(() => WindowsHttpMediaProbe.Inspect(server.Url, new WindowsPlaybackPlan([]))).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
