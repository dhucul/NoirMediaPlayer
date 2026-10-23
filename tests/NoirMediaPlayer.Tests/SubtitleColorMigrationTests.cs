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
    private static string ColorSubtitleFile(string extension)
    {
        var captions = extension == ".vtt" ? new[]
        {
            "<c.red>Same</c>", "<c.blue>Same</c>", "Same",
            "<c.red><b>Red <i>café 😀</i></b></c>\n<c.lime><u>日本語 green</u></c>"
        } : new[]
        {
            "<font color='#FF0000'>Same</font>", "<font color=blue>Same</font>", "Same",
            "<font color='red'><b>Red <i>café 😀</i></b></font>\n<font color='#00FF00'><u>日本語 green</u></font>"
        };
        var result = extension == ".vtt" ? "WEBVTT\n\n" : "";
        for (var index = 0; index < captions.Length; index++)
        {
            var end = index == 3 ? 25 : (index + 1) * 2;
            result += extension == ".vtt"
                ? $"00:{index * 2:00}.000 --> 00:{end:00}.000\n{captions[index]}\n\n"
                : $"{index + 1}\n00:00:{index * 2:00},000 --> 00:00:{end:00},000\n{captions[index]}\n\n";
        }
        return result;
    }

    [Theory]
    [InlineData(".srt", false)]
    [InlineData(".srt", true)]
    [InlineData(".vtt", false)]
    [InlineData(".vtt", true)]
    public async Task SubtitleColors_NativeLoadingSeekingAndSnapshotsKeepColors(string extension, bool automatic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "color-playback", audioTracks: 2);
        var path = automatic ? Path.ChangeExtension(item.Source, extension) : Path.Combine(TestRoot(window), "external" + extension);
        File.WriteAllText(path, ColorSubtitleFile(extension));
        var track = PlainSubtitleTrack.TryLoad(path)!;
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
        async Task SeekColor(long time, uint? expectedColor)
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == track.CueAt(time)!.Start);
            Assert.Equal(track.TextAt(time), presenter.Text);
            if (expectedColor is null) Assert.Empty(presenter.Cue!.Styles);
            else Assert.Contains(presenter.Cue!.Styles, range => range.Foreground == expectedColor);
        }
        await SeekColor(1000, 0xFF0000);
        await SeekColor(3000, 0x0000FF);
        await SeekColor(5000, null);
        await SeekColor(7000, 0x00FF00);
        var snapshot = await CapturePlayerSnapshot(window);
        var sourcePath = Path.Combine(TestRoot(window), "source.png");
        player.SaveSnapshot(sourcePath);
        var source = ReadCaptionBitmap(sourcePath);
        var captured = ReadCaptionBitmap(snapshot);
        Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(source, track.CueAt(7000))), CaptionPixels(captured));
        Assert.True(ColorCaptionPixels(captured, 0xFF0000) > 20);
        Assert.True(ColorCaptionPixels(captured, 0x00FF00) > 20);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Styles.Any(range => range.Foreground == 0x00FF00) == true);
        await SeekColor(3000, 0x0000FF);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, audio => audio.Selected);
        player.SelectAudioTrack(player.AudioTracks.First(audio => audio.Ordinal == 1).Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(player.AudioTracks.Single(audio => audio.Selected).Ordinal == 1);
    });

    [Theory]
    [InlineData(".srt", true)]
    [InlineData(".srt", false)]
    [InlineData(".vtt", true)]
    [InlineData(".vtt", false)]
    public async Task SubtitleColors_MultipleAutomaticTracksSurviveRestartAndCompatibilityHandoff(string extension, bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "color-handoff", audioTracks: 2);
        var path = Path.ChangeExtension(item.Source, extension);
        var other = Path.Combine(TestRoot(window), "subs", "color-handoff.fr" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(path, ColorSubtitleFile(extension));
        File.WriteAllText(other, ColorSubtitleFile(extension));
        await Call<bool>(window, "AddItem", item, true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 3);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        if (!selected) selector.SelectedIndex = 0;
        await Call<bool>(window, "PlayItem", item, false, 3000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Equal(selected, selector.SelectedIndex == 1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 4000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 150);
        player.SelectAudioTrack(player.AudioTracks.First(audio => audio.Ordinal == 1).Id);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AspectRatio == "16:9");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(selected ? tracks[0].Id : -1, vlc.Spu);
        Assert.Equal(vlc.AudioTrackDescription.Where(audio => audio.Id >= 0).ElementAt(1).Id, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 2700, 3500);
        Assert.Equal(ColorSubtitleFile(extension), File.ReadAllText(path));
        Assert.Equal(ColorSubtitleFile(extension), File.ReadAllText(other));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
    });
}
