using System.IO;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mp4Chapters_MatchCompatibilityBoundaries(bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var path = Path.Combine(TestRoot(window), "chapter-video.mp4");
        Mp4Fixture.Write(path, Mp4Fixture.Nero(0, 4000, 8000));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.Equal(native, player is not null);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        if (!native) await Until(() => vlc.ChapterCount == 3);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        long Time() => player?.Time ?? vlc.Time;
        // VLC caches its paused clock across video seeks. Compare its chapter
        // selection, while requiring precise native seek targets on Windows.
        bool AtChapter(int index, long target) => native ? Math.Abs(Time() - target) < 150 : vlc.Chapter == index;
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => AtChapter(1, 4000));
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => AtChapter(2, 8000));
        await Call(window, "NavigateChapterAsync", true);
        Assert.True(AtChapter(2, 8000));
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => AtChapter(1, 4000));
        Invoke(window, "SeekTo", 5500L);
        await Until(() => native ? Math.Abs(Time() - 5500) < 150 : Time() > 5000 && Time() < 7500 && vlc.Chapter == 1);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => AtChapter(0, 0));
        Invoke(window, "SeekTo", 1500L);
        await Until(() => native ? Math.Abs(Time() - 1500) < 150 : Time() > 1000 && Time() < 3500 && vlc.Chapter == 0);
        var beforePrevious = Time();
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(Time(), beforePrevious - 150, beforePrevious + 150);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mp4HiLights_MatchCompatibilityBeforeFirstMarker(bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var path = Path.Combine(TestRoot(window), "highlight-video.mp4");
        Mp4Fixture.Write(path, Mp4Fixture.Highlights(3000, 6000, 9000));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.Equal(native, player is not null);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        if (!native) await Until(() => vlc.ChapterCount == 3);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        long Time() => player?.Time ?? vlc.Time;
        Invoke(window, "SeekTo", 1300L);
        await Until(() => native ? Math.Abs(Time() - 1300) < 150 : Time() > 500 && Time() < 2900 && vlc.Chapter == 0);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => native ? Math.Abs(Time() - 6000) < 150 : vlc.Chapter == 1);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => native ? Math.Abs(Time() - 3000) < 150 : vlc.Chapter == 0);
    });

    [Fact]
    public async Task WindowsNative_Mp4ChapterJumpsKeepCaptionsSnapshotsAndSession() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "captioned-chapters.mp4");
        Mp4Fixture.Write(path, Mp4Fixture.Nero(0, 4000, 8000));
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), "1\n00:00:00,000 --> 00:00:03,000\nBeginning\n\n2\n00:00:03,000 --> 00:00:06,000\nNext chapter\n\n3\n00:00:06,000 --> 00:00:20,000\nFinale\n");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.True(player.HasVideo);
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Next chapter");
        Invoke(window, "Snapshot_Click", window, new System.Windows.RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        var firstSnapshot = Directory.EnumerateFiles(TestRoot(window), "*.png").Single();
        AssertMp4SnapshotColor(firstSnapshot, blue: true);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text == "Finale");
        Invoke(window, "Snapshot_Click", window, new System.Windows.RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Count() == 2);
        AssertMp4SnapshotColor(Directory.EnumerateFiles(TestRoot(window), "*.png").Single(snapshot => snapshot != firstSnapshot), blue: false);
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

    private static void AssertMp4SnapshotColor(string path, bool blue)
    {
        using var file = File.OpenRead(path);
        var image = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr32, null, 0);
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(10, 10, 1, 1), pixel, 4, 0);
        Assert.True(blue ? pixel[0] > pixel[2] + 100 : pixel[2] > pixel[0] + 100,
            $"The snapshot must decode the frame at the chapter target (BGR: {pixel[0]}, {pixel[1]}, {pixel[2]}).");
    }

    [Fact]
    public async Task WindowsNative_ChapterlessMp4NavigationDoesNotRestart() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "chapterless.mp4");
        Mp4Fixture.Write(path);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Empty(Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", false);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsNative_UnsupportedMp4ChapterListFallsBackWithCaptionsAndAudioChoice(bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "unsupported-chapters.mp4");
        // VLC exposes the first 100 tags; native parsing deliberately declines
        // oversized camera lists rather than silently dropping their markers.
        uint[] markers = [0, 6000, 12000, .. Enumerable.Range(0, 98).Select(index => (uint)(12_500 + index * 50))];
        Mp4Fixture.Write(path, Mp4Fixture.Highlights(markers));
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), "1\n00:00:00,000 --> 00:00:20,000\nRetained caption\n");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
        if (!selected) Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 5000);
        Invoke(window, "SeekTo", 1000L);
        await Until(() => Math.Abs(player.Time - 1000) < 150);
        await Call(window, "NavigateChapterAsync", true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
            Field<object?>(window, "_pendingCompatibilitySubtitles") is null &&
            Field<int?>(window, "_pendingCompatibilityAudioOrdinal") is null);
        Assert.Equal(100, vlc.ChapterCount);
        await Until(() => vlc.Chapter == 1);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.Equal(string.Empty, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text);
    });
}
