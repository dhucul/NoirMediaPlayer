using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static BitmapSource RenderAudioCaptionPane(MainWindow window)
    {
        var pane = Field<Grid>(window, "VideoOverlay");
        pane.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(pane.ActualWidth), (int)Math.Ceiling(pane.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(pane);
        bitmap.Freeze();
        return bitmap;
    }

    private static void AssertNativeAudioCaptionPane(MainWindow window)
    {
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.True(player.HasAudio);
        Assert.False(player.HasVideo);
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(0, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio);
        Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(Field<Grid>(window, "VideoOverlay").Background).Color);
        var picture = RenderAudioCaptionPane(window);
        var corner = (picture.PixelWidth * 10 + 10) * 4;
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, CaptionPixels(picture).AsSpan(corner, 4).ToArray());
    }

    [Theory]
    [InlineData(".srt", true)]
    [InlineData(".srt", false)]
    [InlineData(".vtt", true)]
    [InlineData(".vtt", false)]
    public async Task AudioCaptions_ManualAndAutomaticTracksKeepNativeTimingStylesAndAudioControls(string extension, bool automatic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-captions", 30);
        var path = automatic ? Path.ChangeExtension(item.Source, extension) : Path.Combine(TestRoot(window), "external" + extension);
        File.WriteAllText(path, ColorSubtitleFile(extension));
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (!automatic) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        async Task Seek(long time, uint? color)
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == time - 1000);
            if (color is null) Assert.Empty(presenter.Cue!.Styles);
            else Assert.Contains(presenter.Cue!.Styles, style => style.Foreground == color);
        }
        player.SelectAudioTrack(-1);
        await Seek(1000, 0xFF0000);
        await Seek(3000, 0x0000FF); // Identical text with a different foreground.
        await Seek(5000, null);
        await Seek(7000, 0x00FF00);
        AssertNativeAudioCaptionPane(window);
        var picture = RenderAudioCaptionPane(window);
        Assert.True(ColorCaptionPixels(picture, 0xFF0000) > 20);
        Assert.True(ColorCaptionPixels(picture, 0x00FF00) > 20);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        Assert.Equal(2, selector.Items.Count);
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        Assert.Equal(0, ColorCaptionPixels(RenderAudioCaptionPane(window), 0xFF0000));
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Styles.Any(style => style.Foreground == 0x00FF00) == true);
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Contains(player.AudioTracks, audio => audio.Selected);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task AudioCaptions_AutomaticDefaultMatchesCompatibilityAndNativeChaptersKeepCaptions(string extension) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "audio-chapters.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
        File.WriteAllText(Path.ChangeExtension(path, extension), ColorSubtitleFile(extension));
        var item = MediaSourceService.CreateFileItem(path);
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.PreferWindowsPlayback = false;
        await Call<bool>(window, "AddItem", item, true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        settings.PreferWindowsPlayback = true;
        await Call<bool>(window, "PlayItem", item, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing &&
            !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => Math.Abs(player.Time - 4000) < 150 && presenter.Cue?.Start == 4000);
        Assert.Empty(presenter.Cue!.Styles);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150 && presenter.Cue?.Start == 6000);
        Assert.Contains(presenter.Cue!.Styles, style => style.Foreground == 0x00FF00);
        AssertNativeAudioCaptionPane(window);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(".srt", 0)]
    [InlineData(".srt", 1)]
    [InlineData(".srt", 2)]
    [InlineData(".vtt", 0)]
    [InlineData(".vtt", 1)]
    [InlineData(".vtt", 2)]
    public async Task AudioCaptions_RestartAndHandoffRetainAllTracksSelectionPauseAndAudioOff(string extension, int selection) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-handoff", 30);
        var first = Path.ChangeExtension(item.Source, extension);
        var second = Path.Combine(TestRoot(window), "subs", "audio-handoff.fr" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, ColorSubtitleFile(extension));
        File.WriteAllText(second, ColorSubtitleFile(extension).Replace("Same", "Other"));
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 3);
        selector.SelectedIndex = selection;
        await Call<bool>(window, "PlayItem", item, false, 3000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Equal(selection, selector.SelectedIndex);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        var position = player.Time;
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        if (selection == 0) Assert.Null(presenter.Cue);
        else await Until(() => presenter.Text == (selection == 1 ? "Same" : "Other"));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AspectRatio == "16:9" && vlc.AudioTrack == -1);
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(selection == 0 ? -1 : tracks[selection - 1].Id, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, position - 300, position + 500);
        Assert.Null(presenter.Cue);
        Assert.Equal(ColorSubtitleFile(extension), File.ReadAllText(first));
    });

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task AudioCaptions_ManualHttpCaptionsStayNative(string extension) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var wave = CreateWave(window, "audio-http", 30);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(wave.Source), ".wav", "audio/wav");
        var captions = Path.Combine(TestRoot(window), "network" + extension);
        File.WriteAllText(captions, ColorSubtitleFile(extension));
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Audio HTTP", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 8000);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue?.Start == 6000);
        AssertNativeAudioCaptionPane(window);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task AudioCaptions_UnsupportedFormattingRestoresExistingTracksBeforeLoadingInVlc(string extension) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-unsupported", 30);
        var nativePath = Path.Combine(TestRoot(window), "native" + extension);
        var complexPath = Path.Combine(TestRoot(window), "complex" + extension);
        File.WriteAllText(nativePath, ColorSubtitleFile(extension));
        var unsupported = extension == ".srt"
            ? "1\n00:00:00,000 --> 00:00:25,000\n<font face='Arial' color='red'>Compatibility caption</font>\n"
            : "WEBVTT\n\n00:00.000 --> 00:25.000\n<c.red.bg_blue.custom>Compatibility caption</c>\n";
        File.WriteAllText(complexPath, unsupported);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", nativePath));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        player.SelectAudioTrack(-1);
        var position = player.Time;
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", complexPath));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        try
        {
            await Until(() => vlc.SpuDescription.Count(track => track.Id >= 0) == 2 &&
                vlc.Spu == vlc.SpuDescription.Last(track => track.Id >= 0).Id);
        }
        catch (TaskCanceledException)
        {
            Assert.Fail($"Caption IDs: {string.Join(", ", vlc.SpuDescription.Select(track => track.Id))}; selected {vlc.Spu}; audio {vlc.AudioTrack}");
        }
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, position - 300, position + 500);
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(ColorSubtitleFile(extension), File.ReadAllText(nativePath));
        Assert.Equal(unsupported, File.ReadAllText(complexPath));
    });

    [Theory]
    [InlineData(".srt", true, false)]
    [InlineData(".srt", false, true)]
    [InlineData(".vtt", true, true)]
    [InlineData(".vtt", false, false)]
    public async Task AudioCaptions_SnapshotRequestRetainsCompatibilityBehaviorAndSession(string extension, bool selected, bool concurrentFallback) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-snapshot", 30);
        File.WriteAllText(Path.ChangeExtension(item.Source, extension), ColorSubtitleFile(extension));
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 2);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        if (!selected) selector.SelectedIndex = 0;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        player.SelectAudioTrack(-1);
        var position = player.Time;
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        // Exercise both the snapshot's own fallback and a second fallback request
        // arriving while its failing native command still occupies the queue.
        if (!concurrentFallback) await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.InRange(vlc.Time, position - 300, position + 500);
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
    });

    [Fact]
    public async Task AudioCaptions_VideoReplacementIsCoveredAndStopClearsTheCaption() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        await Call<bool>(window, "AddItem", CreateVideo(window, "prior-video"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(Field<Grid>(window, "VideoOverlay").Background).Color);
        var item = CreateWave(window, "next-audio", 30);
        File.WriteAllText(Path.ChangeExtension(item.Source, ".srt"), ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", item, true);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && presenter.Cue is not null);
        AssertNativeAudioCaptionPane(window);
        Invoke(window, "Stop_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Stopped);
        Assert.Null(presenter.Cue);
        await Call<bool>(window, "AddItem", CreateVideo(window, "next-video"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(Field<WindowsMediaEngine>(window, "_windowsPlayer").HasVideo);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(Field<Grid>(window, "VideoOverlay").Background).Color);
        Assert.Null(presenter.Cue);
    });
}
