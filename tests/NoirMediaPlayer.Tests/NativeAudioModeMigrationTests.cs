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
    [InlineData(".wav", false, false)]
    [InlineData(".wma", false, false)]
    [InlineData(".mkv", false, false)]
    [InlineData(".mkv", false, true)]
    [InlineData(".mkv", true, false)]
    [InlineData(".avi", true, false)]
    [InlineData(".mp4", true, false)]
    [InlineData(".wmv", true, false)]
    public async Task NativeAudioMode_AudioOnlyEngineRetainsTheCompleteStreamInventory(string extension, bool video, bool captions) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        string path;
        if (extension == ".wav") path = CreateWave(window, "audio-mode", 20).Source;
        else if (extension == ".avi") path = CreateVideo(window, "audio-mode", audioTracks: 2).Source;
        else
        {
            path = Path.Combine(TestRoot(window), "audio-mode" + extension);
            if (extension == ".mkv") MatroskaFixture.Write(path, captions, video: video);
            else if (extension == ".mp4") Mp4Fixture.Write(path);
            else AsfFixture.Write(path, video: video);
        }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = Field<PlaybackView>(window, "VideoView").WindowHandle;
        using var player = await Task.Run(() => new WindowsMediaEngine(handle, mediaEvent =>
        {
            if (mediaEvent == 13) ready.TrySetResult();
            if (mediaEvent is 5 or 1014) ready.TrySetException(new InvalidOperationException($"Audio-only mode failed: event {mediaEvent}."));
        }, requireAudioOnly: true));
        await Task.Run(() => player.Open(path, 0, true, 1));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(player.HasAudio);
        Assert.False(player.HasVideo); // The flag prevents video decoding, including A/V sources.
        Assert.Equal(!video, player.HasSupportedStreams); // Supported embedded SRT text is now part of the native inventory.
        if (captions) Assert.Single(player.CaptionTracks);
        Assert.NotEmpty(player.AudioTracks);
    });

    [Theory]
    [InlineData(".wav", false)]
    [InlineData(".wav", true)]
    [InlineData(".wma", false)]
    [InlineData(".wma", true)]
    [InlineData(".m4a", false)]
    [InlineData(".m4a", true)]
    [InlineData(".mkv", false)]
    public async Task NativeAudioMode_HardwareOffKeepsLocalAndHttpAudioCaptionsAndControlsNative(string extension, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var file = AudioModeFile(window, extension);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(file.Source), extension, "application/octet-stream") : null;
        var first = Path.ChangeExtension(file.Source, ".webvtt");
        var second = Path.Combine(TestRoot(window), "subs", "software-audio.en_US.forced.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, BackgroundSubtitleFile());
        File.WriteAllText(second, "1\n00:00:00,000 --> 00:00:25,000\n<b>Alternate caption</b>\n");
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Software audio HTTP", IsNetwork = true } : file;
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network)
        {
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", first));
            Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", second));
        }
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.True(player.HasSupportedStreams);
        AssertNativeAudioCaptionPane(window);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 3);
        selector.SelectedIndex = 1;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var time in new[] { 7000L, 3000L })
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == time - 1000);
        }
        Assert.True(ColorCaptionPixels(RenderAudioCaptionPane(window), 0xFF0000) > 300);
        var audioSelector = Field<ComboBox>(window, "AudioTrackComboBox");
        audioSelector.SelectedIndex = 0;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && !player.AudioTracks.Any(track => track.Selected));
        var ordinal = extension == ".mkv" ? 1 : 0;
        audioSelector.SelectedItem = audioSelector.Items.OfType<ComboBoxItem>().Single(option => option.Tag is WindowsAudioTrack track && track.Ordinal == ordinal);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle && player.AudioTracks.Any(track => track.Selected && track.Ordinal == ordinal));
        Invoke(window, "SelectSpeed", 0.75f);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        selector.SelectedIndex = 2;
        await Until(() => presenter.Text == "Alternate caption");
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        window.Width = 1050;
        window.Height = 680;
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        AssertNativeAudioCaptionPane(window);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Start == 2000);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.False(Field<PlayerSettings>(window, "_settings").HardwareDecoding);
        Assert.Equal(0.75f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(first));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(".mkv", 0, -1)]
    [InlineData(".mkv", 1, 1)]
    [InlineData(".mkv", 2, -1)]
    [InlineData(".m4a", 1, -1)]
    [InlineData(".wma", 2, 0)]
    public async Task NativeAudioMode_ChaptersRestartAndHandoffKeepCaptionsAudioChoiceAndPause(string extension, int selection, int audioOrdinal) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = AudioModeFile(window, extension);
        var first = Path.ChangeExtension(item.Source, ".webvtt");
        var second = Path.Combine(TestRoot(window), "subs", "software-audio.fr.srt");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, BackgroundSubtitleFile());
        File.WriteAllText(second, "1\n00:00:00,000 --> 00:00:25,000\nRetained caption\n");
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 3);
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
        Assert.Equal(audioOrdinal, player.AudioTracks.FirstOrDefault(track => track.Selected)?.Ordinal ?? -1);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(selection == 0 ? -1 : tracks[selection - 1].Id, vlc.Spu);
        Assert.Equal(audioOrdinal, Array.FindIndex(vlc.AudioTrackDescription.Where(track => track.Id >= 0).ToArray(), track => track.Id == vlc.AudioTrack));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(extension == ".wma" ? Field<long>(window, "_compatibilityChapterTime") : vlc.Time, 7700, 8500);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(first));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(".avi", false, false)]
    [InlineData(".mp4", false, false)]
    [InlineData(".wmv", false, false)]
    [InlineData(".mkv", false, false)]
    [InlineData(".mp4", true, false)]
    [InlineData(".m4a", true, false)]
    [InlineData(".mp4", false, true)]
    public async Task NativeAudioMode_VideoKeepsSoftwarePlaybackOrRequiredFallbackAndCaptions(string extension, bool network, bool videoOnly) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        string path;
        if (extension == ".avi") path = CreateVideo(window, "software-video", audioTracks: 2).Source;
        else
        {
            path = Path.Combine(TestRoot(window), "software-video" + extension);
            if (extension == ".mkv") MatroskaFixture.Write(path, false, video: true);
            else if (extension == ".wmv") AsfFixture.Write(path, video: true);
            else if (videoOnly) Mp4Fixture.WriteVideo(path);
            else Mp4Fixture.Write(path);
        }
        var captions = Path.ChangeExtension(path, ".webvtt");
        File.WriteAllText(captions, BackgroundSubtitleFile());
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(path), extension, "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Software video HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(path);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        if (player is null)
        {
            // Windows' software MJPEG negotiation currently returns
            // MF_E_TRANSFORM_TYPE_NOT_SET. Keep its actual compatibility renderer.
            Assert.Contains(extension, new[] { ".avi", ".mkv" });
            Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
            var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
            await Until(() => vlc.VoutCount > 0 && vlc.Time > 250);
            if (!network) await Until(() => vlc.SpuDescription.Count(track => track.Id >= 0) == 1 && vlc.Spu >= 0);
        }
        else
        {
            Assert.True(player.UsesSoftwareVideo);
            Assert.True(player.HasVideo);
            Assert.True(player.HasSupportedStreams);
            Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        }
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        await Until(() => Field<ComboBox>(window, "SubtitleTrackComboBox").Items.Count == 2);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var snapshot = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(320, snapshot.PixelWidth);
        Assert.Equal(240, snapshot.PixelHeight);
        var center = (snapshot.PixelHeight / 2 * snapshot.PixelWidth + snapshot.PixelWidth / 2) * 4;
        Assert.Contains(CaptionPixels(snapshot).AsSpan(center, 3).ToArray(), component => component > 30);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        if (player is not null) Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(captions));
    }, hardwareDecoding: false);

    [Fact]
    public async Task NativeAudioMode_EmbeddedTextStaysNativeAndExplicitCompatibilityChoiceStillUsesVlc() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "embedded.mkv");
        MatroskaFixture.Write(path, subtitles: true);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Embedded captions");
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        await Call<bool>(window, "AddItem", CreateWave(window, "compatibility-opt-out", 20), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    }, hardwareDecoding: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeAudioMode_AudioSnapshotAndUnsupportedCaptionFallbackKeepTheWholeSession(bool snapshot) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var first = Path.Combine(TestRoot(window), "first.webvtt");
        var second = Path.Combine(TestRoot(window), "second.srt");
        var unsupported = Path.Combine(TestRoot(window), "complex.webvtt");
        File.WriteAllText(first, BackgroundSubtitleFile());
        const string plain = "1\n00:00:00,000 --> 00:00:25,000\nRetained plain caption\n";
        const string css = "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nCSS caption\n";
        File.WriteAllText(second, plain);
        File.WriteAllText(unsupported, css);
        await Call<bool>(window, "AddItem", CreateWave(window, "fallback-audio", 20), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", first));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", second));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        if (snapshot)
        {
            Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
            await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
            Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            Assert.Empty(Directory.EnumerateFiles(TestRoot(window), "*.png"));
        }
        else Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", unsupported));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(snapshot ? 2 : 3, tracks.Length);
        Assert.Equal(tracks[^1].Id, vlc.Spu);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 6700, 7500);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(first));
        Assert.Equal(plain, File.ReadAllText(second));
        Assert.Equal(css, File.ReadAllText(unsupported));
    }, hardwareDecoding: false);

    private static PlaylistItem AudioModeFile(MainWindow window, string extension)
    {
        if (extension == ".wav") return CreateWave(window, "software-audio", 20);
        var path = Path.Combine(TestRoot(window), "software-audio" + extension);
        if (extension == ".mkv") MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters(), audioTracks: 2);
        else if (extension == ".m4a") Mp4Fixture.WriteAudio(path, Mp4Fixture.Nero(0, 4000, 8000));
        else AsfFixture.Write(path, false, 0, 4000, 8000);
        return MediaSourceService.CreateFileItem(path);
    }
}
