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
    [InlineData(".mp4", "video/mp4", "", true)]
    [InlineData(".wav", "audio/wav", "", true)]
    [InlineData(".avi", "video/x-msvideo", "", true)]
    [InlineData(".mp4", "application/octet-stream", "", true)]
    [InlineData(".wav", "application/octet-stream", "", true)]
    [InlineData(".avi", "application/octet-stream", "", true)]
    [InlineData(".mp4", "application/octet-stream", ".php", true)]
    [InlineData(".wav", "audio/wav", ".ashx", false)]
    [InlineData(".avi", "application/octet-stream", ".bin", true)]
    [InlineData(".m4a", "audio/mp4", ".download", false)]
    [InlineData(".mkv", "video/x-matroska", "", true)]
    [InlineData(".mkv", "application/octet-stream", "", true)]
    [InlineData(".mkv", "application/octet-stream", ".php", true)]
    [InlineData(".wmv", "video/x-ms-wmv", "", true)]
    [InlineData(".wmv", "application/octet-stream", ".php", true)]
    [InlineData(".wma", "audio/x-ms-wma", "", false)]
    [InlineData(".wma", "application/octet-stream", ".download", false)]
    public async Task HttpEndpoints_PlaybackKeepsCaptionsSnapshotsAndTransport(string extension, string mime, string endpoint, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        string path;
        if (extension == ".wav") path = CreateWave(window, "endpoint", 20).Source;
        else if (extension == ".avi") path = CreateVideo(window, "endpoint", audioTracks: 1).Source;
        else if (extension == ".mkv")
        {
            path = Path.Combine(TestRoot(window), "endpoint.mkv");
            MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters(), video: true, chaptersAtEnd: true);
        }
        else if (extension is ".wmv" or ".wma")
        {
            path = Path.Combine(TestRoot(window), "endpoint" + extension);
            AsfFixture.Write(path, extension == ".wmv", 0, 4000, 8000);
        }
        else
        {
            path = Path.Combine(TestRoot(window), "endpoint" + extension);
            if (extension == ".m4a") Mp4Fixture.WriteAudio(path, Mp4Fixture.Nero(0, 4000, 8000));
            else Mp4Fixture.Write(path, Mp4Fixture.Nero(0, 4000, 8000));
        }
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), endpoint, mime);
        var source = server.Url + "?id=AbC";
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = source, Title = "Media endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason") ?? "The endpoint did not stay native.");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        var caption = Path.Combine(TestRoot(window), "endpoint.webvtt");
        File.WriteAllText(caption, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        const int ordinal = -1;
        player.SelectAudioTrack(ordinal < 0 ? -1 : player.AudioTracks.Single(track => track.Ordinal == ordinal).Id);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        string? plain = null;
        if (player.HasVideo)
        {
            plain = await CapturePlayerSnapshot(window);
            Assert.True(ReferenceEquals(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer")), Field<string?>(window, "_windowsFailureReason"));
            Assert.InRange(player.Time, 6850, 7150);
        }
        selector.SelectedIndex = 1;
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        if (plain is not null)
        {
            var styled = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.True(ReferenceEquals(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer")), Field<string?>(window, "_windowsFailureReason"));
            Assert.InRange(player.Time, 6850, 7150);
            var expected = SubtitlePresenter.Composite(ReadCaptionBitmap(plain), PlainSubtitleTrack.TryLoad(caption)!.CueAt(7000));
            Assert.Equal(CaptionPixels(expected), CaptionPixels(styled));
            Assert.Equal(320, styled.PixelWidth);
            Assert.Equal(240, styled.PixelHeight);
        }
        else AssertNativeAudioCaptionPane(window);
        if (extension is ".mp4" or ".m4a" or ".mkv" or ".wmv" or ".wma")
        {
            await Call(window, "NavigateChapterAsync", true);
            await Until(() => Math.Abs(player.Time - 8000) < 150);
            await Call(window, "NavigateChapterAsync", false);
            await Until(() => Math.Abs(player.Time - 4000) < 150);
        }
        Assert.Equal(source, Field<PlaylistItem>(window, "_currentItem").Source);
        Assert.Equal(ordinal, player.AudioTracks.FirstOrDefault(track => track.Selected)?.Ordinal ?? -1);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        Assert.InRange(player.Time, extension is ".mp4" or ".m4a" or ".mkv" or ".wmv" or ".wma" ? 3850 : 6850, extension is ".mp4" or ".m4a" or ".mkv" or ".wmv" or ".wma" ? 4150 : 7150);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData(".mp4", true)]
    [InlineData(".wav", false)]
    [InlineData(".avi", true)]
    public async Task HttpEndpoints_RestartAndHandoffKeepTheOriginalUrlAndSession(string extension, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        string path;
        if (extension == ".wav") path = CreateWave(window, "restart-endpoint", 20).Source;
        else if (extension == ".avi") path = CreateVideo(window, "restart-endpoint", audioTracks: 1).Source;
        else { path = Path.Combine(TestRoot(window), "restart-endpoint.mp4"); Mp4Fixture.Write(path); }
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".php", "application/octet-stream");
        var source = server.Url + "?id=AbC&signature=DoNotRewrite";
        var item = new PlaylistItem { Source = source, Title = "Restart endpoint", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var caption = Path.Combine(TestRoot(window), "retained.srt");
        File.WriteAllText(caption, ColorSubtitleFile(".srt"));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", caption));
        Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(-1, vlc.Spu);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.InRange(vlc.Time, 6700, 7500);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(source, Field<PlaylistItem>(window, "_currentItem").Source);
        Assert.Equal(ColorSubtitleFile(".srt"), File.ReadAllText(caption));
    }, hardwareDecoding: hardware);

    [Theory]
    [InlineData("multi-audio", true)]
    [InlineData("software-video", false)]
    [InlineData("no-ranges", true)]
    [InlineData("wrong-mime", true)]
    [InlineData("ordered-matroska", true)]
    [InlineData("visible-text", true)]
    public async Task HttpEndpoints_UnreadySourcesKeepCompatibilityFeatures(string kind, bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "compatibility.mp4");
        if (kind == "multi-audio") path = CreateVideo(window, "multi-audio-endpoint", audioTracks: 2).Source;
        else if (kind == "no-ranges") path = CreateWave(window, "non-seekable-endpoint", 20).Source;
        else if (kind == "visible-text") Mp4Fixture.WriteQuickTime(path, enabled: true);
        else if (kind == "ordered-matroska")
        {
            var ordered = MatroskaFixture.Group(0x1043A770, MatroskaFixture.Group(0x45B9,
                MatroskaFixture.UInt(0x45BC, 1), MatroskaFixture.UInt(0x45DB, 1), MatroskaFixture.UInt(0x45DD, 1),
                MatroskaFixture.Group(0xB6, MatroskaFixture.UInt(0x73C4, 1), MatroskaFixture.UInt(0x91, 4_000_000_000), MatroskaFixture.UInt(0x92, 8_000_000_000))));
            MatroskaFixture.Write(path, false, ordered);
        }
        else Mp4Fixture.Write(path);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".bin", kind == "wrong-mime" ? "video/x-matroska" : "application/octet-stream")
        {
            Response = kind == "no-ranges" ? LoopbackResponse.IgnoreRanges : LoopbackResponse.Normal
        };
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Compatibility endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (kind == "software-video")
        {
            Assert.True(Field<WindowsMediaEngine>(window, "_windowsPlayer").UsesSoftwareVideo);
            Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        }
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        if (kind == "ordered-matroska") { await Until(() => vlc.Length is >= 3900 and <= 4100); return; }
        if (kind == "no-ranges")
        {
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
            return;
        }
        await Until(() => vlc.VoutCount > 0 && vlc.Time > 250);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        if (kind == "multi-audio")
        {
            var audio = vlc.AudioTrackDescription.Where(track => track.Id >= 0).ToArray();
            Assert.Equal(2, audio.Length);
            Assert.True(vlc.SetAudioTrack(audio[1].Id));
            Invoke(window, "SeekTo", 7000L);
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            Assert.Equal(audio[1].Id, vlc.AudioTrack);
        }
        var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(320, snapshot.PixelWidth);
        Assert.Equal(240, snapshot.PixelHeight);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    }, hardwareDecoding: hardware);

    [Fact]
    public async Task HttpEndpoints_RedirectsKeepTheOriginalSignedUrl() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "redirect.mp4");
        Mp4Fixture.Write(path);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".php", "video/mp4") { Response = LoopbackResponse.Redirect };
        var source = server.Url + "?id=AbC&signature=KeepCase";
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = source, Title = "Redirect endpoint", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Assert.Equal(320, ReadCaptionBitmap(await CapturePlayerSnapshot(window)).PixelWidth);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(source, Field<PlaylistItem>(window, "_currentItem").Source);
    });
}
