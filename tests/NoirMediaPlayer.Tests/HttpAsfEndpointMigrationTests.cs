using System.IO;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("matroska")]
    public async Task HttpAsfEndpoints_SourceReplacementKeepsTheNewContainersChapterNavigation(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "first.wma");
        AsfFixture.Write(path, false, 0, 4000, 8000);
        await using var first = new LoopbackMediaServer(File.ReadAllBytes(path), "", "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = first.Url, Title = "ASF endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        path = Path.Combine(TestRoot(window), "replacement");
        if (kind == "mp4") Mp4Fixture.Write(path, Mp4Fixture.Nero(0, 4000, 8000));
        else MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
        await using var next = new LoopbackMediaServer(File.ReadAllBytes(path), ".php", "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = next.Url, Title = "Replacement endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.Null(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.ChapterCount == 3);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 2);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Theory]
    [InlineData("software", false)]
    [InlineData("known-software", false)]
    [InlineData("markers", true)]
    [InlineData("mime", true)]
    public async Task HttpAsfEndpoints_SoftwareAndUnreadySourcesRetainVideoCaptionsAndTransport(string kind, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "compatibility.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        if (kind == "markers") AsfFixture.Edit(path, header => AsfFixture.Marker(header, 40_000_000));
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), kind == "known-software" ? ".wmv" : ".bin", kind == "mime" ? "video/mp4" : "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Compatibility ASF endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (!hardware)
        {
            Assert.True(Field<WindowsMediaEngine>(window, "_windowsPlayer").UsesSoftwareVideo);
            // Keep the remaining assertions on the compatibility capture/marker
            // path after proving this formerly unsupported source now opens natively.
            Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        }
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        await Until(() => vlc.VoutCount > 0 && vlc.Time > 250);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var caption = Path.Combine(TestRoot(window), "compatibility.srt");
        File.WriteAllText(caption, ColorSubtitleFile(".srt"));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.True(vlc.Spu >= 0);
        Assert.True(vlc.SetAudioTrack(-1));
        var firstSnapshot = await CapturePlayerSnapshot(window);
        Assert.Equal(320, ReadCaptionBitmap(firstSnapshot).PixelWidth);
        AssertMp4SnapshotColor(firstSnapshot, blue: true);
        if (kind.EndsWith("software", StringComparison.Ordinal))
        {
            Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_compatibilityChapters").Starts);
            await Call(window, "NavigateChapterAsync", true);
            await Call(window, "NavigateChapterAsync", true);
            Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
            var media = Field<Media>(window, "_currentMedia");
            int Frames() => media.Statistics is { } stats ? stats.DecodedVideo : 0;
            var frames = Frames();
            // Verify the actual seek with newly decoded video rather than
            // relying on VLC's cached paused clock.
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
                vlc.Time >= 8000 && vlc.Time < 10_500 && Frames() > frames);
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        }
        else Assert.Null(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);
}
