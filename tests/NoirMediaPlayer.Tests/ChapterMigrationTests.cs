using System.IO;
using System.Windows;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Fact]
    public async Task Compatibility_ChapterNavigationDefinesBoundaryBehavior() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "chapters.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && vlc.ChapterCount == 3);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 5500);
        Invoke(window, "SeekTo", 5500L);
        await Until(() => vlc.Time > 4000 && vlc.Time < 8000);
        Invoke(window, "NextChapter_Click", window, new RoutedEventArgs());
        await Until(() => Math.Abs(vlc.Time - 8000) < 150);
        Invoke(window, "NextChapter_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.InRange(vlc.Time, 7850, 8150);
        Invoke(window, "PreviousChapter_Click", window, new RoutedEventArgs());
        await Until(() => Math.Abs(vlc.Time - 4000) < 150);
        Invoke(window, "SeekTo", 5500L);
        await Until(() => vlc.Time > 4000 && vlc.Time < 8000);
        Invoke(window, "PreviousChapter_Click", window, new RoutedEventArgs());
        await Until(() => vlc.Time < 150);
        Invoke(window, "SeekTo", 1500L);
        await Until(() => vlc.Time > 0 && vlc.Time < 4000);
        Invoke(window, "PreviousChapter_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.InRange(vlc.Time, 1350, 1650); // Previous at the first chapter is a no-op.
    });

    [Fact]
    public async Task WindowsNative_ChapterNavigationKeepsPausedAudioSelectionAndSession() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "native-chapters.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 8000);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7850, 8150);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        Invoke(window, "SeekTo", 5500L);
        await Until(() => player.Time > 4000 && player.Time < 8000);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => player.Time < 150);
        Invoke(window, "SeekTo", 1500L);
        await Until(() => player.Time > 0 && player.Time < 4000);
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(player.Time, 1350, 1650);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
    });

    [Fact]
    public async Task WindowsNative_ChapterlessMatroskaDoesNotRestartForNavigation() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "no-chapters.mkv");
        MatroskaFixture.Write(path, false);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call<bool>(window, "AddItem", CreateWave(window, "other-source", 20), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
    });

    [Fact]
    public async Task WindowsNative_ConsecutiveChapterRequestsAndPlayingStateArePreserved() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "consecutive-chapters.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters());
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 8000);
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        await Until(() => Math.Abs(player.Time - 8000) < 150);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => player.Time >= 4000 && player.Time < 7000);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Playing, Field<PlaybackState>(window, "_playbackState"));
    });

    [Fact]
    public async Task WindowsNative_OrderedChapterTimelineStartsOnCompatibility() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "ordered-chapters.mkv");
        var ordered = MatroskaFixture.Group(0x1043A770, MatroskaFixture.Group(0x45B9,
            MatroskaFixture.UInt(0x45BC, 1), MatroskaFixture.UInt(0x45DB, 1), MatroskaFixture.UInt(0x45DD, 1), MatroskaFixture.Group(0xB6,
                MatroskaFixture.UInt(0x73C4, 1), MatroskaFixture.UInt(0x91, 4_000_000_000),
                MatroskaFixture.UInt(0x92, 8_000_000_000), MatroskaFixture.Group(0x80, MatroskaFixture.Text(0x85, "Virtual edition")))));
        MatroskaFixture.Write(path, false, ordered);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Until(() => vlc.Length is >= 3900 and <= 4100); // Keep the virtual edition, not the physical 20-second file.
    });

    [Fact]
    public async Task WindowsNative_VideoChaptersKeepCaptionsAndSnapshots() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "chapter-video.mkv");
        MatroskaFixture.Write(path, false, MatroskaFixture.SimpleChapters(), video: true);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason") ?? "The native Matroska video source was not selected.");
        Assert.True(player.HasVideo);
        var captions = Path.Combine(TestRoot(window), "captions.srt");
        File.WriteAllText(captions, "1\n00:00:00,000 --> 00:00:03,000\nBeginning\n\n2\n00:00:03,000 --> 00:00:06,000\nNext chapter\n\n3\n00:00:06,000 --> 00:00:20,000\nFinale\n");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Next chapter");
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Finale");
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });
}
