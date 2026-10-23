using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    private static string StyledSubtitleFile(string extension) => extension == ".vtt"
        ? "WEBVTT\n\n00:00.000 --> 00:02.000\nSame\n\n00:02.000 --> 00:04.000\n<i>Same</i>\n\n00:04.000 --> 00:06.000\n<b>Same</b>\n\n00:06.000 --> 00:08.000\n<u>Same</u>\n\n00:08.000 --> 00:25.000\n<b>Bold <i>and italic</i></b>\n<u>日本語 café</u>\n"
        : "1\n00:00:00,000 --> 00:00:02,000\nSame\n\n2\n00:00:02,000 --> 00:00:04,000\n<i>Same</i>\n\n3\n00:00:04,000 --> 00:00:06,000\n<b>Same</b>\n\n4\n00:00:06,000 --> 00:00:08,000\n<u>Same</u>\n\n5\n00:00:08,000 --> 00:00:25,000\n<b>Bold <i>and italic</i></b>\n<u>日本語 café</u>\n";

    private static BitmapSource ReadCaptionBitmap(string path)
    {
        using var file = File.OpenRead(path);
        var image = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }

    [Theory]
    [InlineData(".srt", true)]
    [InlineData(".srt", false)]
    [InlineData(".vtt", true)]
    [InlineData(".vtt", false)]
    public async Task SubtitleStyles_NativePlaybackKeepsTimingAudioOffTrackSelectionAndSnapshots(string extension, bool automatic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "styled-captions", audioTracks: 2);
        var path = automatic ? Path.ChangeExtension(item.Source, extension) : Path.Combine(TestRoot(window), "external" + extension);
        File.WriteAllText(path, StyledSubtitleFile(extension));
        var track = PlainSubtitleTrack.TryLoad(path);
        Assert.NotNull(track);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        if (!automatic) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        async Task SeekStyled(long time, SubtitleStyle style)
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue is { } cue && cue.Start == track.CueAt(time)!.Start);
            var styles = presenter.Cue!.Styles;
            if (style == SubtitleStyle.None) Assert.Empty(styles);
            else Assert.Contains(styles, range => (range.Style & style) == style);
            Assert.Equal(track.TextAt(time), presenter.Text);
        }
        await SeekStyled(1000, SubtitleStyle.None);
        await SeekStyled(3000, SubtitleStyle.Italic);
        await SeekStyled(5000, SubtitleStyle.Bold);
        await SeekStyled(7000, SubtitleStyle.Underline);
        await SeekStyled(9000, SubtitleStyle.Bold | SubtitleStyle.Italic);
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var styledSnapshot = Directory.EnumerateFiles(TestRoot(window), "*.png").Single();
        var plainPath = Path.Combine(TestRoot(window), "uncaptioned.png");
        player.SaveSnapshot(plainPath);
        var source = ReadCaptionBitmap(plainPath);
        var expected = SubtitlePresenter.Composite(source, track.CueAt(9000));
        Assert.Equal(CaptionPixels(expected), CaptionPixels(ReadCaptionBitmap(styledSnapshot)));
        Assert.True(WhiteCaptionPixels(expected).Count(value => value) > 20);
        Assert.NotEqual(CaptionPixels(SubtitlePresenter.Composite(source, new SubtitleCue(8000, 25000, track.TextAt(9000)))), CaptionPixels(expected));
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Assert.Empty(presenter.Text);
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Styles.Count > 0);
        await SeekStyled(3000, SubtitleStyle.Italic);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        player.SelectAudioTrack(player.AudioTracks.First(audio => audio.Ordinal == 1).Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.True(player.AudioTracks.Single(audio => audio.Selected).Ordinal == 1);
    });

    [Theory]
    [InlineData(".srt", true)]
    [InlineData(".srt", false)]
    [InlineData(".vtt", true)]
    [InlineData(".vtt", false)]
    public async Task SubtitleStyles_CompatibilityHandoffKeepsTheOriginalStyledFileAndOffSelection(string extension, bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "styled-handoff", audioTracks: 2);
        var path = Path.ChangeExtension(item.Source, extension);
        File.WriteAllText(path, StyledSubtitleFile(extension));
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => selector.Items.Count == 2);
        if (!selected) selector.SelectedIndex = 0;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 4000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 150);
        player.SelectAudioTrack(-1);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AspectRatio == "16:9" && vlc.AudioTrack == -1);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, subtitle => subtitle.Id >= 0);
        Assert.InRange(vlc.Time, 2700, 3500);
        Assert.Equal(StyledSubtitleFile(extension), File.ReadAllText(path));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
    });
}
