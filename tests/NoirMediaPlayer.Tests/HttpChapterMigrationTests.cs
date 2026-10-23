using System.IO;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData(".mp4", "nero", true)]
    [InlineData(".m4v", "highlights", true)]
    [InlineData(".mov", "text", true)]
    [InlineData(".3gp", "tx3g", true)]
    [InlineData(".m4a", "nero", false)]
    public async Task HttpChapters_NativeSeeksRetainTransportCaptionsAndSnapshots(string extension, string format, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "network-chapters" + extension);
        if (format is "text" or "tx3g") Mp4Fixture.WriteQuickTime(path, classicText: format == "text");
        else if (extension == ".m4a") Mp4Fixture.WriteAudio(path, Mp4Fixture.Nero(0, 4000, 8000));
        else Mp4Fixture.Write(path, format == "nero" ? Mp4Fixture.Nero(0, 4000, 8000) : Mp4Fixture.Highlights(0, 4000, 8000));
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), extension, extension == ".m4a" ? "audio/mp4" : "video/mp4");
        var item = new PlaylistItem { Source = server.Url + "?token=AbC", Title = "Network chapters", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        var caption = Path.Combine(TestRoot(window), "network-caption.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        if (hardware)
        {
            var snapshot = await CapturePlayerSnapshot(window);
            AssertMp4SnapshotColor(snapshot, blue: false);
            Assert.True(ReadCaptionBitmap(snapshot).PixelWidth > 0);
        }
        else AssertNativeAudioCaptionPane(window);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is null);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Equal(0, selector.SelectedIndex);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        selector.SelectedIndex = 1;
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7850, 9000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);

    [Fact]
    public async Task HttpChapters_ChapterlessNavigationDoesNotRestart() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "chapterless.mp4");
        Mp4Fixture.Write(path);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".mp4", "video/mp4");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Chapterless", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Empty(Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HttpChapters_UnsupportedMetadataRetainsCompatibilityHandoff(bool off, bool noRanges) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "unsupported.mp4");
        Mp4Fixture.Write(path, noRanges ? Mp4Fixture.Nero(0, 6000, 12000) :
            Mp4Fixture.Highlights([0, 6000, 12000, .. Enumerable.Range(0, 98).Select(i => (uint)(12_500 + i * 50))]));
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".mp4", "video/mp4")
        {
            Response = noRanges ? LoopbackResponse.IgnoreRanges : LoopbackResponse.Normal,
            ResetResponseAfterRequest = noRanges
        };
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Compatibility chapters", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
        var caption = Path.Combine(TestRoot(window), "retained.srt");
        File.WriteAllText(caption, "1\n00:00:00,000 --> 00:00:20,000\nRetained caption\n");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        if (off) Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null && vlc.ChapterCount == (noRanges ? 3 : 100) && vlc.Chapter == 1);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(!off, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.Equal(1.5f, vlc.Rate);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpChapters_VisibleQuickTimeTracksUseCompatibilityFromTheStart(bool classic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "visible-text.mp4");
        Mp4Fixture.WriteQuickTime(path, enabled: true, classicText: classic);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".mov", "video/quicktime");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Visible chapter track", IsNetwork = true }, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.ChapterCount == 3);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 1);
    });
}
