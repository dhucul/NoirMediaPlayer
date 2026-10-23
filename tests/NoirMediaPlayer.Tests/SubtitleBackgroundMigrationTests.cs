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
    private static string BackgroundSubtitleFile() => "WEBVTT\n\n" +
        "00:00.000 --> 00:02.000\n<c.yellow.bg_blue>Same</c>\n\n" +
        "00:02.000 --> 00:04.000\n<c.yellow.bg_red>Same</c>\n\n" +
        "00:04.000 --> 00:06.000\nSame\n\n" +
        "00:06.000 --> 00:25.000\n<c.yellow.bg_blue><b>Unicode <i>café 😀</i></b></c>\n<c.black.bg_lime><u>日本語 العربية background</u></c>\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubtitleBackgrounds_ManualAndAutomaticVideoPlaybackKeepsColorsControlsAndSnapshots(bool automatic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "background-video", audioTracks: 2);
        var path = automatic ? Path.ChangeExtension(item.Source, ".vtt") : Path.Combine(TestRoot(window), "external.vtt");
        File.WriteAllText(path, BackgroundSubtitleFile());
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        if (!automatic) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        async Task Seek(long time, uint? background)
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == time - 1000);
            if (background is null) Assert.Empty(presenter.Cue!.Styles);
            else Assert.Contains(presenter.Cue!.Styles, range => range.Background == background);
        }
        await Seek(1000, 0x0000FF);
        await Seek(3000, 0xFF0000); // Same text, different background.
        await Seek(5000, null);
        await Seek(7000, 0x00FF00);
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var captured = ReadCaptionBitmap(Directory.EnumerateFiles(TestRoot(window), "*.png").Single());
        var sourcePath = Path.Combine(TestRoot(window), "source.png");
        player.SaveSnapshot(sourcePath);
        var source = ReadCaptionBitmap(sourcePath);
        Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(source, PlainSubtitleTrack.TryLoad(path)!.CueAt(7000))), CaptionPixels(captured));
        Assert.True(ColorCaptionPixels(captured, 0x0000FF) > 300);
        Assert.True(ColorCaptionPixels(captured, 0x00FF00) > 300);
        Assert.True(ColorCaptionPixels(captured, 0xFFFF00) > 20);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        Assert.Equal(0, ColorCaptionPixels(RenderCaption(presenter), 0x0000FF));
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Start == 6000);
        await Seek(3000, 0xFF0000);
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        player.SelectAudioTrack(player.AudioTracks.First(audio => audio.Ordinal == 1).Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Equal(1, Assert.Single(player.AudioTracks, audio => audio.Selected).Ordinal);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(path));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubtitleBackgrounds_LocalAndHttpAudioStayNative(bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var wave = CreateWave(window, "background-audio", 30);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(wave.Source), ".wav", "audio/wav") : null;
        var path = network ? Path.Combine(TestRoot(window), "external.vtt") : Path.ChangeExtension(wave.Source, ".vtt");
        File.WriteAllText(path, BackgroundSubtitleFile());
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Background audio HTTP", IsNetwork = true } : wave;
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 3000L);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => presenter.Cue?.Start == 2000);
        AssertNativeAudioCaptionPane(window);
        Assert.True(ColorCaptionPixels(RenderAudioCaptionPane(window), 0xFF0000) > 300);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => presenter.Cue?.Start == 6000);
        Assert.True(ColorCaptionPixels(RenderAudioCaptionPane(window), 0x00FF00) > 300);
        Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        Assert.Equal(0, ColorCaptionPixels(RenderAudioCaptionPane(window), 0x00FF00));
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SubtitleBackgrounds_TrackChoiceChaptersAndRestartSurviveCompatibilityHandoff(int selection) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "background-chapters.mkv");
        MatroskaFixture.Write(path, subtitles: false, MatroskaFixture.SimpleChapters(), video: true);
        var first = Path.ChangeExtension(path, ".vtt");
        var second = Path.Combine(TestRoot(window), "subs", "background-chapters.fr.vtt");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, BackgroundSubtitleFile());
        var alternate = BackgroundSubtitleFile().Replace("bg_blue", "bg_magenta");
        File.WriteAllText(second, alternate);
        var item = MediaSourceService.CreateFileItem(path);
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 3);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        selector.SelectedIndex = selection;
        await Call<bool>(window, "PlayItem", item, false, 3000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Equal(selection, selector.SelectedIndex);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        if (selection == 0) Assert.Null(presenter.Cue);
        else
        {
            var expected = selection == 1 ? 0x0000FFu : 0xFF00FFu;
            await Until(() => presenter.Cue?.Styles.Any(range => range.Background == expected) == true);
        }
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(selection == 0 ? -1 : tracks[selection - 1].Id, vlc.Spu);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        await Until(() => vlc.Time is >= 7700 and <= 8500);
        Assert.Equal(8000L, Field<long>(window, "_compatibilityChapterTime"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => vlc.Time is >= 8000 and < 9000);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(selection == 0 ? -1 : tracks[selection - 1].Id, vlc.Spu);
        Assert.Null(presenter.Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(first));
        Assert.Equal(alternate, File.ReadAllText(second));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubtitleBackgrounds_UnsupportedCustomClassesOrCssRetainTheNativeTrackOnHandoff(bool css) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var original = Path.Combine(TestRoot(window), "background.vtt");
        var unsupported = Path.Combine(TestRoot(window), "complex.vtt");
        File.WriteAllText(original, BackgroundSubtitleFile());
        var complex = css ? "WEBVTT\n\nSTYLE\n::cue { background-color: red; }\n\n00:00.000 --> 00:25.000\nCSS caption\n"
            : "WEBVTT\n\n00:00.000 --> 00:25.000\n<c.red.bg_blue.custom>Custom caption</c>\n";
        File.WriteAllText(unsupported, complex);
        await Call<bool>(window, "AddItem", CreateVideo(window, "unsupported-background"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", original));
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", unsupported));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(2, vlc.SpuDescription.Count(track => track.Id >= 0));
        Assert.Equal(vlc.SpuDescription.Last(track => track.Id >= 0).Id, vlc.Spu);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(original));
        Assert.Equal(complex, File.ReadAllText(unsupported));
    });

    [Fact]
    public async Task SubtitleBackgrounds_HttpVideoSnapshotsKeepNativeCaptionsAndSession() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "network.mp4");
        Mp4Fixture.Write(path);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".mp4", "video/mp4");
        var captions = Path.Combine(TestRoot(window), "background.vtt");
        File.WriteAllText(captions, BackgroundSubtitleFile());
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "Background video HTTP", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.True(player.HasVideo);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 7000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150 && Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue?.Start == 6000);
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var image = ReadCaptionBitmap(Directory.EnumerateFiles(TestRoot(window), "*.png").Single());
        Assert.True(ColorCaptionPixels(image, 0x00FF00) > 300);
        Assert.True(ColorCaptionPixels(image, 0xFFFF00) > 20);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.InRange(player.Time, 6850, 7150);
        Assert.Equal(1, Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(captions));
    });
}
