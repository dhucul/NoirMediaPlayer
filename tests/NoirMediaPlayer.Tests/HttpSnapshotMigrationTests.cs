using System.Diagnostics;
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
    [InlineData(".mp4", false)]
    [InlineData(".mp4", true)]
    [InlineData(".avi", false)]
    [InlineData(".avi", true)]
    [InlineData(".wmv", false)]
    [InlineData(".wmv", true)]
    public async Task HttpSnapshots_CaptureSourcePixelsWithoutRestartingOrChangingTransport(string extension, bool playing) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        PlaylistItem file;
        if (extension == ".avi") file = CreateVideo(window, "http-snapshot", audioTracks: 2);
        else
        {
            var path = Path.Combine(TestRoot(window), "http-snapshot" + extension);
            if (extension == ".mp4") Mp4Fixture.Write(path);
            else AsfFixture.Write(path, video: true);
            file = MediaSourceService.CreateFileItem(path);
        }
        await Call<bool>(window, "AddItem", file, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 7000);
        var local = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        var expected = new Dictionary<long, string>();
        foreach (var time in new[] { 1000L, 7000L })
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(local.Time - time) < 150);
            var snapshot = Path.Combine(TestRoot(window), $"expected-{time}.png");
            await Task.Run(() => local.SaveSnapshot(snapshot));
            expected.Add(time, snapshot);
        }
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(file.Source), extension,
            extension == ".mp4" ? "video/mp4" : "application/octet-stream");
        var item = new PlaylistItem { Source = server.Url + "?token=AbC", Title = "HTTP snapshot", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 7000);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var audioOrdinal = extension == ".avi" ? 1 : -1;
        player.SelectAudioTrack(audioOrdinal < 0 ? -1 : player.AudioTracks.Single(track => track.Ordinal == audioOrdinal).Id);
        var plain = Path.Combine(TestRoot(window), "captions.srt");
        var styled = Path.Combine(TestRoot(window), "background.vtt");
        File.WriteAllText(plain, "1\n00:00:00,000 --> 00:00:25,000\nPlain café 日本語\n");
        File.WriteAllText(styled, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        if (playing)
        {
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        }
        var first = await CapturePlayerSnapshot(window);
        Assert.Equal(CaptionPixels(ReadCaptionBitmap(expected[1000])), CaptionPixels(ReadCaptionBitmap(first)));
        Assert.Equal(playing ? PlaybackState.Playing : PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        if (playing)
        {
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        }
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        foreach (var choice in new[] { 1, 2, 0 })
        {
            selector.SelectedIndex = choice;
            var track = choice == 0 ? null : PlainSubtitleTrack.TryLoad(choice == 1 ? plain : styled);
            var next = await CapturePlayerSnapshot(window);
            var source = ReadCaptionBitmap(expected[7000]);
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(source, track?.CueAt(7000))), CaptionPixels(ReadCaptionBitmap(next)));
            Assert.InRange(player.Time, 6850, 7150);
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(audioOrdinal, player.AudioTracks.FirstOrDefault(track => track.Selected)?.Ordinal ?? -1);
        Assert.Equal(3, selector.Items.Count);
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(styled));
        Assert.True(server.RangeRequests >= 4);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(LoopbackResponse.IgnoreRanges, false)]
    [InlineData(LoopbackResponse.WrongRange, true)]
    [InlineData(LoopbackResponse.Truncated, true)]
    [InlineData(LoopbackResponse.StallBody, false)]
    public async Task HttpSnapshots_FailedNativeCaptureRetainsCompatibilityTracksPauseAndPosition(LoopbackResponse response, bool captionsOff) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var file = Path.Combine(TestRoot(window), "http-fallback.mp4");
        Mp4Fixture.Write(file);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(file), ".mp4", "video/mp4");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "HTTP fallback", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var plain = Path.Combine(TestRoot(window), "captions.srt");
        var styled = Path.Combine(TestRoot(window), "background.vtt");
        File.WriteAllText(plain, "1\n00:00:00,000 --> 00:00:25,000\nRetained caption\n");
        File.WriteAllText(styled, BackgroundSubtitleFile());
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
        if (captionsOff) Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 7000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        server.ResetResponseAfterRequest = true;
        server.Response = response;
        var clock = Stopwatch.StartNew();
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null, timeoutSeconds: 12);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        Assert.InRange(clock.Elapsed.TotalSeconds, 0, 13);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(captionsOff ? -1 : tracks[1].Id, vlc.Spu);
        Assert.InRange(vlc.Time, 6700, 7500);
        Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(styled));
    });

    private static async Task<string> CapturePlayerSnapshot(MainWindow window, int timeoutSeconds = 5)
    {
        var previous = Directory.EnumerateFiles(TestRoot(window), "*.png").ToHashSet();
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle &&
            Directory.EnumerateFiles(TestRoot(window), "*.png").Except(previous).Any(), timeoutSeconds);
        return Directory.EnumerateFiles(TestRoot(window), "*.png").Except(previous).Single();
    }
}
