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
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task SubtitleNames_ManualAndAutomaticWebVttKeepLocalAndHttpAudioAndVideoNative(bool audioOnly, bool network, bool automatic) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var file = audioOnly ? CreateWave(window, "alias-audio", 30) : CreateVideo(window, "alias-video", audioTracks: 2);
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(file.Source),
            Path.GetExtension(file.Source), "application/octet-stream") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "WebVTT alias HTTP", IsNetwork = true } : file;
        var path = automatic ? Path.ChangeExtension(file.Source, ".en_US.forced.WEBVTT") : Path.Combine(TestRoot(window), "external.WEBVTT");
        var content = BackgroundSubtitleFile();
        File.WriteAllText(path, content);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Equal(!audioOnly, player.HasVideo);
        if (automatic) await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue is not null);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path)); // Replacing the same file cannot duplicate tracks.
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        Assert.Equal(2, selector.Items.Count);
        Assert.Equal(path, Assert.IsType<PlainSubtitleTrack>(((ComboBoxItem)selector.SelectedItem).Tag).Path);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        foreach (var time in new[] { 7000L, 3000L })
        {
            Invoke(window, "SeekTo", time);
            await Until(() => Math.Abs(player.Time - time) < 150 && presenter.Cue?.Start == time - 1000);
            Assert.Contains(presenter.Cue!.Styles, range => range.Background == (time == 7000 ? 0x00FF00u : 0xFF0000u));
        }
        if (audioOnly)
        {
            AssertNativeAudioCaptionPane(window);
            Assert.True(ColorCaptionPixels(RenderAudioCaptionPane(window), 0xFF0000) > 300);
        }
        else
        {
            var captured = await CapturePlayerSnapshot(window);
            var source = Path.Combine(TestRoot(window), "uncaptioned.png");
            await Task.Run(() => player.SaveSnapshot(source));
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(ReadCaptionBitmap(source), presenter.Cue)),
                CaptionPixels(ReadCaptionBitmap(captured)));
            var redPixels = ColorCaptionPixels(ReadCaptionBitmap(captured), 0xFF0000);
            Assert.True(redPixels > 40, $"The short source-resolution caption contains {redPixels} red background pixels.");
        }
        selector.SelectedIndex = 0;
        Assert.Null(presenter.Cue);
        selector.SelectedIndex = 1;
        await Until(() => presenter.Cue?.Start == 2000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(content, File.ReadAllText(path));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SubtitleNames_AutomaticWebVttAndQualifiedTracksKeepChapterNavigationAndHandoff(int selection) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "caption-names.mkv");
        MatroskaFixture.Write(path, subtitles: false, MatroskaFixture.SimpleChapters(), video: true);
        var first = Path.ChangeExtension(path, ".webvtt");
        var second = Path.Combine(TestRoot(window), "subs", "caption-names.en_US.forced.webvtt");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        var content = BackgroundSubtitleFile();
        File.WriteAllText(first, content);
        File.WriteAllText(second, content.Replace("bg_blue", "bg_magenta"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && selector.Items.Count == 3);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        selector.SelectedIndex = selection;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        if (selection == 0) Assert.Null(presenter.Cue);
        else await Until(() => presenter.Cue?.Start == 6000 &&
            presenter.Cue.Styles.Any(range => range.Background == (selection == 1 ? 0x0000FFu : 0xFF00FFu)));
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var captured = await CapturePlayerSnapshot(window);
        var source = Path.Combine(TestRoot(window), "uncaptioned.png");
        await Task.Run(() => player.SaveSnapshot(source));
        var expectedCue = selection == 0 ? null : PlainSubtitleTrack.TryLoad(selection == 1 ? first : second)!.CueAt(8000);
        Assert.Equal(expectedCue?.Text, presenter.Cue?.Text);
        if (expectedCue is not null) Assert.Equal(expectedCue.Styles, presenter.Cue!.Styles);
        Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(ReadCaptionBitmap(source), expectedCue)),
            CaptionPixels(ReadCaptionBitmap(captured)));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(selection == 0 ? -1 : tracks[selection - 1].Id, vlc.Spu);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 7700, 8500);
        Assert.Null(presenter.Cue);
        Assert.Equal(content, File.ReadAllText(first));
        Assert.Equal(content.Replace("bg_blue", "bg_magenta"), File.ReadAllText(second));
    });

    [Fact]
    public async Task SubtitleNames_UnsupportedWebVttRetainsAllTracksAndTransportOnFallback() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var supported = Path.Combine(TestRoot(window), "supported.webvtt");
        var unsupported = Path.Combine(TestRoot(window), "css.webvtt");
        var content = BackgroundSubtitleFile();
        const string css = "WEBVTT\n\nSTYLE\n::cue { color: red; }\n\n00:00.000 --> 00:25.000\nCSS caption\n";
        File.WriteAllText(supported, content);
        File.WriteAllText(unsupported, css);
        await Call<bool>(window, "AddItem", CreateVideo(window, "unsupported-alias", audioTracks: 2), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", supported));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 15_000);
        player.SelectAudioTrack(-1);
        Invoke(window, "SeekTo", 7000L);
        await Until(() => Math.Abs(player.Time - 7000) < 150);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", unsupported));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        var tracks = vlc.SpuDescription.Where(track => track.Id >= 0).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Equal(tracks[1].Id, vlc.Spu);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.InRange(vlc.Time, 6700, 7500);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Null(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue);
        Assert.Equal(content, File.ReadAllText(supported));
        Assert.Equal(css, File.ReadAllText(unsupported));
    });
}
