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
    [InlineData(true, true, false, ".mkv", "video/x-matroska")]
    [InlineData(true, true, true, ".mkv", "video/x-matroska")]
    [InlineData(false, true, false, ".mkv", "video/x-matroska")]
    [InlineData(false, false, true, ".mkv", "video/x-matroska")]
    [InlineData(false, true, true, "", "audio/x-matroska")]
    [InlineData(false, false, true, ".download", "application/octet-stream")]
    public async Task HttpMatroskaChapters_NativePlaybackKeepsTracksCaptionsSnapshotsAndTransport(bool video, bool hardware, bool chaptersAtEnd, string endpoint, string mime) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "native-network.mkv");
        var audioTracks = video ? 1 : 2;
        var audioOrdinal = audioTracks - 1;
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters(), video: video, chaptersAtEnd: chaptersAtEnd, audioTracks: audioTracks);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, mime);
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url + "?token=AbC", Title = "HTTP Matroska", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason") ?? "Matroska did not stay native.");
        Assert.Equal(video, player.HasVideo);
        Assert.Equal(audioTracks, player.AudioTracks.Count);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        var caption = Path.Combine(TestRoot(window), "network.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        player.SelectAudioTrack(player.AudioTracks.Single(track => track.Ordinal == audioOrdinal).Id);
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(ReferenceEquals(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer")), Field<string?>(window, "_windowsFailureReason"));
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        await Task.WhenAll(Call(window, "NavigateChapterAsync", false), Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        if (video)
        {
            var snapshot = await CapturePlayerSnapshot(window);
            var bitmap = ReadCaptionBitmap(snapshot);
            Assert.Equal(320, bitmap.PixelWidth);
            Assert.Equal(240, bitmap.PixelHeight);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(160, 100, 1, 1), pixel, 4, 0);
            Assert.True(pixel[0] > pixel[2] + 100);
        }
        else AssertNativeAudioCaptionPane(window);
        Assert.Equal(audioOrdinal, player.AudioTracks.Single(track => track.Selected).Ordinal);
        player.SelectAudioTrack(-1);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Invoke(window, "SeekTo", 1500L);
        await Until(() => Math.Abs(player.Time - 1500) < 150);
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(player.Time, 1350, 1650);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(0, selector.SelectedIndex);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        selector.SelectedIndex = 1;
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7850, 9000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(false, ".mkv")]
    [InlineData(true, ".mkv")]
    [InlineData(false, "")]
    [InlineData(true, ".bin")]
    public async Task HttpMatroskaChapters_ChapterlessNavigationKeepsNativeSession(bool video, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "chapterless.mkv");
        MatroskaFixture.Write(path, false, video: video);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, endpoint == ".mkv" ? "video/x-matroska" : "application/octet-stream");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Chapterless Matroska", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Empty(Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData("ordered", true, ".mkv")]
    [InlineData("captions", true, ".mkv")]
    [InlineData("ranges", true, ".mkv")]
    [InlineData("slow", true, ".mkv")]
    [InlineData("software-video", false, ".mkv")]
    [InlineData("multi-audio-video", true, ".mkv")]
    [InlineData("captions", true, ".php")]
    [InlineData("ranges", true, "")]
    [InlineData("slow", true, ".download")]
    [InlineData("software-video", false, ".bin")]
    [InlineData("multi-audio-video", true, "")]
    public async Task HttpMatroskaChapters_UnreadySourcesRetainCompatibilityFeatures(string kind, bool hardware, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "compatibility-network.mkv");
        var ordered = MatroskaFixture.Group(0x1043A770, MatroskaFixture.Group(0x45B9,
            MatroskaFixture.UInt(0x45BC, 1), MatroskaFixture.UInt(0x45DB, 1), MatroskaFixture.UInt(0x45DD, 1),
            MatroskaFixture.Group(0xB6, MatroskaFixture.UInt(0x73C4, 1), MatroskaFixture.UInt(0x91, 4_000_000_000),
                MatroskaFixture.UInt(0x92, 8_000_000_000), MatroskaFixture.Group(0x80, MatroskaFixture.Text(0x85, "Virtual edition")))));
        var video = kind is "software-video" or "multi-audio-video";
        MatroskaFixture.Write(path, kind == "captions", kind == "ordered" ? ordered : MatroskaFixture.SimpleChapters(),
            video: video, audioTracks: kind == "multi-audio-video" ? 2 : 1);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, endpoint == ".mkv" ? "video/x-matroska" : "application/octet-stream")
        {
            Response = kind == "ranges" ? LoopbackResponse.IgnoreRanges : kind == "slow" ? LoopbackResponse.StallHeaders : LoopbackResponse.Normal,
            ResetResponseAfterRequest = kind is "ranges" or "slow"
        };
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Compatibility Matroska", IsNetwork = true }, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        if (kind == "ordered")
        {
            await Until(() => vlc.Length is >= 3900 and <= 4100);
            Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
            return;
        }
        await Until(() => vlc.ChapterCount == 3);
        if (kind == "captions") Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        if (video) await Until(() => vlc.VoutCount > 0 && vlc.Time > 250);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 1);
        if (kind == "multi-audio-video")
        {
            var audio = vlc.AudioTrackDescription.Where(track => track.Id >= 0).ToArray();
            Assert.Equal(2, audio.Length);
            vlc.SetAudioTrack(audio[1].Id);
            await Call(window, "NavigateChapterAsync", true);
            await Until(() => vlc.Chapter == 2);
            Assert.Equal(audio[1].Id, vlc.AudioTrack);
        }
        if (video)
        {
            var snapshot = await CapturePlayerSnapshot(window);
            Assert.Equal(320, ReadCaptionBitmap(snapshot).PixelWidth);
        }
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(0, -1, true, ".mkv")]
    [InlineData(1, 1, false, ".mkv")]
    [InlineData(0, -1, true, "")]
    [InlineData(1, 1, false, ".php")]
    public async Task HttpMatroskaChapters_RestartAndHandoffRetainCaptionAudioAndTransport(int selection, int audioOrdinal, bool hardware, string endpoint) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "restart-network.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters(), audioTracks: 2);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, endpoint == ".mkv" ? "video/x-matroska" : "application/octet-stream");
        var source = server.Url + "?signature=KeepCase&file=AbC";
        var item = new PlaylistItem { Source = source, Title = "Restart Matroska", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var caption = Path.Combine(TestRoot(window), "retained.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = selection;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(audioOrdinal < 0 ? -1 : player.AudioTracks.Single(track => track.Ordinal == audioOrdinal).Id);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        Assert.Equal(selection, selector.SelectedIndex);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var subtitles = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Single(subtitles);
        Assert.Equal(selection == 0 ? -1 : subtitles[0].Id, vlc.Spu);
        Assert.Equal(audioOrdinal, Array.FindIndex(vlc.AudioTrackDescription.Where(track => track.Id >= 0).ToArray(), track => track.Id == vlc.AudioTrack));
        Assert.InRange(vlc.Time, 7700, 8500);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(caption));
        Assert.Equal(source, Field<PlaylistItem>(window, "_currentItem").Source);
        await Until(() => vlc.ChapterCount == 3);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => vlc.Chapter == 1);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    }, hardwareDecoding: hardware);
}
