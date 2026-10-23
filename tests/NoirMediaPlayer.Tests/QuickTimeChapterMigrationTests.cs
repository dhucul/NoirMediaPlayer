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
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickTimeChapters_CompatibilityRecognizesDisabledTextChapterTrack(bool classicText) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "quicktime.mp4");
        Mp4Fixture.WriteQuickTime(path, classicText: classicText);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Mp4Chapters.TryRead(path)?.Starts);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.ChapterCount == 3);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 1 && vlc.Time >= 4000);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 2 && vlc.Time >= 8000);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => vlc.Chapter == 1);
    });

    [Theory]
    [InlineData(".mp4", false)]
    [InlineData(".mov", true)]
    public async Task QuickTimeChapters_NativeNavigationKeepsSessionAndTransport(string extension, bool classicText) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "quicktime" + extension);
        Mp4Fixture.WriteQuickTime(path, classicText: classicText);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null,
            Field<string?>(window, "_windowsFailureReason") ?? "Windows did not accept the chapter-only text track.");
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7850, 8150);
        Invoke(window, "SeekTo", 5500L);
        await Until(() => Math.Abs(player.Time - 5500) < 150);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => player.Time < 150);
        Invoke(window, "SeekTo", 1500L);
        await Until(() => Math.Abs(player.Time - 1500) < 150);
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(player.Time, 1350, 1650);
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        try { await Until(() => Math.Abs(player.Time - 8000) < 150); }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException($"Two queued chapter requests ended at {player.Time} ms instead of 8000 ms.", exception);
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => player.Time >= 4000 && player.Time < 7000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Contains(player.AudioTracks, track => track.Selected);
    });

    [Fact]
    public async Task QuickTimeChapters_RapidRequestsUseTheLatestSeekPosition() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "rapid-quicktime.mp4");
        Mp4Fixture.WriteQuickTime(path);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            player.Time = 1500;
            await Until(() => Math.Abs(player.Time - 1500) < 150);
            await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
            try { await Until(() => Math.Abs(player.Time - 8000) < 150); }
            catch (OperationCanceledException exception)
            {
                throw new InvalidOperationException($"Rapid chapter iteration {attempt} ended at {player.Time} ms.", exception);
            }
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Fact]
    public async Task QuickTimeChapters_NativeCaptionsAndSourceFrameSnapshotsSurviveJumps() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "captioned-quicktime.mp4");
        Mp4Fixture.WriteQuickTime(path);
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), "1\n00:00:00,000 --> 00:00:03,000\nBeginning\n\n2\n00:00:03,000 --> 00:00:06,000\nNext chapter\n\n3\n00:00:06,000 --> 00:00:20,000\nFinale\n");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Next chapter");
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var first = Directory.EnumerateFiles(TestRoot(window), "*.png").Single();
        AssertMp4SnapshotColor(first, blue: true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Finale");
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Count() == 2);
        AssertMp4SnapshotColor(Directory.EnumerateFiles(TestRoot(window), "*.png").Single(snapshot => snapshot != first), blue: false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuickTimeChapters_UnmigratedViewFeaturesKeepChapterTracksCaptionsAndAudioOff(bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "quicktime-handoff.mp4");
        Mp4Fixture.WriteQuickTime(path);
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), "1\n00:00:00,000 --> 00:00:20,000\nRetained caption\n");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Equal(3, Field<NativeChapterMap>(window, "_nativeChapters").Starts.Count);
        if (!selected) Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await Call<bool>(window, "EnsureVlcForFeatureAsync");
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.AspectRatio == "16:9");
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(3, vlc.ChapterCount);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => vlc.Chapter == 1);
    });

    [Fact]
    public async Task QuickTimeChapters_EnabledTextTrackRetainsCompatibilityFromStart() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "visible-quicktime.mp4");
        Mp4Fixture.WriteQuickTime(path, enabled: true);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.ChapterCount == 3);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });
}
