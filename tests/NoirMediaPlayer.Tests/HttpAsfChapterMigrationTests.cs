using System.IO;
using System.Windows;
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
    [InlineData(".wmv", true, ".wmv")]
    [InlineData(".asf", true, ".asf")]
    [InlineData(".wma", true, ".wma")]
    [InlineData(".wma", false, ".wma")]
    [InlineData(".wmv", true, "")]
    [InlineData(".asf", true, ".ashx")]
    [InlineData(".wma", false, ".bin")]
    public async Task HttpAsfChapters_NativeNavigationRetainsTransportCaptionsAndSnapshots(string extension, bool hardware, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "network-chapters" + extension);
        var video = extension != ".wma";
        AsfFixture.Write(path, video, 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url + "?token=AbC", Title = "HTTP ASF chapters", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        var caption = Path.Combine(TestRoot(window), "network.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        if (video) AssertMp4SnapshotColor(await CapturePlayerSnapshot(window), blue: false);
        else AssertNativeAudioCaptionPane(window);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        Assert.Equal(0, selector.SelectedIndex);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        selector.SelectedIndex = 1;
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7850, 9000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(".wmv", false, ".wmv")]
    [InlineData(".wmv", true, ".wmv")]
    [InlineData(".wma", false, ".wma")]
    [InlineData(".wma", true, ".wma")]
    [InlineData(".asf", false, ".asf")]
    [InlineData(".asf", true, ".asf")]
    [InlineData(".wmv", false, "")]
    [InlineData(".wmv", true, "")]
    [InlineData(".wma", false, ".php")]
    [InlineData(".wma", true, ".php")]
    public async Task HttpAsfChapters_ChapterlessNavigationDoesNotRestartEitherBackend(string extension, bool native, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var path = Path.Combine(TestRoot(window), "chapterless" + extension);
        AsfFixture.Write(path, extension != ".wma");
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Chapterless ASF", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.Equal(native, player is not null);
        Assert.Empty(Field<NativeChapterMap>(window, native ? "_nativeChapters" : "_compatibilityChapters").Starts);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(".wmv", ".wmv")]
    [InlineData(".wma", ".wma")]
    [InlineData(".asf", ".asf")]
    [InlineData(".wmv", "")]
    [InlineData(".wma", ".download")]
    public async Task HttpAsfChapters_WindowsMarkersRemainAvailableWithCompatibilityDecoding(string extension, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "compatibility" + extension);
        AsfFixture.Write(path, extension != ".wma", 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Windows ASF markers", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(0, vlc.ChapterCount);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_compatibilityChapters").Starts);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        await Call(window, "NavigateChapterAsync", false);
        Assert.Equal(4000, Field<long>(window, "_compatibilityChapterTime"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.Time >= 4000 && vlc.Time < 5500);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(".wmv", false, ".wmv")]
    [InlineData(".wmv", true, ".wmv")]
    [InlineData(".wma", false, ".wma")]
    [InlineData(".wma", true, ".wma")]
    [InlineData(".wmv", false, "")]
    [InlineData(".wmv", true, ".php")]
    [InlineData(".wma", false, ".ashx")]
    [InlineData(".wma", true, ".bin")]
    public async Task HttpAsfChapters_RestartAndHandoffReuseMarkersWithoutAnotherMetadataRequest(string extension, bool off, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "handoff" + extension);
        AsfFixture.Write(path, extension != ".wma", 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, "application/octet-stream");
        var source = server.Url + "?signature=KeepCase&file=AbC";
        var item = new PlaylistItem { Source = source, Title = "ASF handoff", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var caption = Path.Combine(TestRoot(window), "retained.srt");
        File.WriteAllText(caption, ColorSubtitleFile(".srt"));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        if (off) selector.SelectedIndex = 0;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        var markers = Field<NativeChapterMap>(window, "_nativeChapters");
        // VLC can still read this server, but a second range-only metadata probe
        // would fail. The handoff must reuse the map from the validated session.
        server.Response = LoopbackResponse.IgnoreRanges;
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.Same(markers, Field<NativeChapterMap>(window, "_compatibilityChapters"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(!off, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.Equal(1.5f, vlc.Rate);
        await Call(window, "NavigateChapterAsync", true);
        Assert.Equal(8000, Field<long>(window, "_compatibilityChapterTime"));
        await Call(window, "NavigateChapterAsync", false);
        Assert.Equal(4000, Field<long>(window, "_compatibilityChapterTime"));
        Assert.Equal(ColorSubtitleFile(".srt"), File.ReadAllText(caption));
        Assert.Equal(source, Field<PlaylistItem>(window, "_currentItem").Source);
    });

    [Theory]
    [InlineData(LoopbackResponse.IgnoreRanges)]
    [InlineData(LoopbackResponse.StallHeaders)]
    public async Task HttpAsfChapters_FailedInitialInspectionKeepsPlaybackAndCanRecoverAtHandoff(LoopbackResponse response) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "recover.wma");
        AsfFixture.Write(path, false, 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".wma", "audio/x-ms-wma")
        {
            Response = response, ResetResponseAfterRequest = true
        };
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Recover ASF metadata", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
        var caption = Path.Combine(TestRoot(window), "retained.srt");
        File.WriteAllText(caption, ColorSubtitleFile(".srt"));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_compatibilityChapters").Starts);
        Assert.Equal(4000, Field<long>(window, "_compatibilityChapterTime"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(-1, vlc.Spu);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
    });
}
