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
    [InlineData(".wmv", false)]
    [InlineData(".wmv", true)]
    [InlineData(".wma", false)]
    [InlineData(".wma", true)]
    [InlineData(".asf", false)]
    [InlineData(".asf", true)]
    public async Task AsfChapters_WindowsMarkersDriveEitherBackend(string extension, bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var path = Path.Combine(TestRoot(window), "chapter-media" + extension);
        AsfFixture.Write(path, extension != ".wma", 0, 4000, 8000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.Equal(native, player is not null);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        if (!native) Assert.Equal(0, vlc.ChapterCount); // Windows supplies these markers.
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10_000);
        long Time() => player?.Time ?? vlc.Time;
        async Task AtChapter(long target)
        {
            if (native) { await Until(() => Math.Abs(Time() - target) < 150); return; }
            // VLC's paused video clock is cached. Resume to verify the actual seek,
            // then pause again before checking the next chapter boundary.
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
            await Until(() => Time() >= target && Time() < target + 1000);
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        }
        await Call(window, "NavigateChapterAsync", true);
        await AtChapter(4000);
        await Call(window, "NavigateChapterAsync", true);
        await AtChapter(8000);
        var beforeNext = Time();
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(Time(), beforeNext - 150, beforeNext + 150);
        await Call(window, "NavigateChapterAsync", false);
        await AtChapter(4000);
        Invoke(window, "SeekTo", 5500L);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        await AtChapter(5500);
        await Call(window, "NavigateChapterAsync", false);
        await AtChapter(0);
        var beforePrevious = Time();
        await Call(window, "NavigateChapterAsync", false);
        Assert.InRange(Time(), beforePrevious - 150, beforePrevious + 150);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsfChapters_LeadInCanReachTheFirstMarkerOnEitherBackend(bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var path = Path.Combine(TestRoot(window), "lead-in.wma");
        AsfFixture.Write(path, false, 3000, 6000, 9000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.Equal(native, player is not null);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "NavigateChapterAsync", true);
        if (native) await Until(() => Math.Abs(player!.Time - 6000) < 150);
        else
        {
            await Call(window, "TogglePlayPause");
            await Until(() => vlc.Time >= 6000 && vlc.Time < 7000);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsfChapters_NativeJumpsKeepCaptionsAudioRateAndSession(bool video) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "captioned" + (video ? ".wmv" : ".wma"));
        AsfFixture.Write(path, video, 0, 4000, 8000);
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        // Consecutive requests must use the first seek's confirmed position.
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", true));
        await Until(() => Math.Abs(player.Time - 8000) < 150 &&
            Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Cue?.Start == 6000);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        if (video)
        {
            Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
            await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
            AssertMp4SnapshotColor(Directory.EnumerateFiles(TestRoot(window), "*.png").Single(), blue: false);
        }
        else AssertNativeAudioCaptionPane(window);
        player.SelectAudioTrack(player.AudioTracks[0].Id);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "NavigateChapterAsync", false);
        await Until(() => player.Time >= 4000 && player.Time < 7000);
        Assert.Contains(player.AudioTracks, track => track.Selected);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsfChapters_ChapterlessNavigationStaysNative(bool video) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "chapterless" + (video ? ".wmv" : ".wma"));
        AsfFixture.Write(path, video);
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
    [InlineData(".wmv", true)]
    [InlineData(".wmv", false)]
    [InlineData(".wma", true)]
    [InlineData(".wma", false)]
    public async Task AsfChapters_HandoffRetainsWindowsMarkersCaptionsPauseAndAudioOff(string extension, bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "handoff" + extension);
        AsfFixture.Write(path, extension == ".wmv", 0, 4000, 8000);
        var captions = Path.ChangeExtension(path, ".srt");
        File.WriteAllText(captions, ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        player.SelectAudioTrack(-1);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        if (!selected) selector.SelectedIndex = 0;
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_compatibilityChapters").Starts);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        await Task.WhenAll(Call(window, "NavigateChapterAsync", true), Call(window, "NavigateChapterAsync", false), Call(window, "NavigateChapterAsync", true));
        await Until(() => Field<long>(window, "_lastPlaybackTime") == 8000);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => vlc.Time >= 8000 && vlc.Time < 9500);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Invoke(window, "SeekTo", 1500L);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        await Call(window, "NavigateChapterAsync", false); // First chapter: keep the explicit seek.
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => vlc.Time >= 1500 && vlc.Time < 3000);
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.Equal(ColorSubtitleFile(".srt"), File.ReadAllText(captions));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call<bool>(window, "AddItem", CreateWave(window, "replacement", 20), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Null(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
    });

    [Theory]
    [InlineData("duplicate")]
    [InlineData("long-name")]
    public async Task AsfChapters_UnsupportedMarkersRetainCompatibilityAndCaptionState(string kind) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "unsupported.wma");
        AsfFixture.Write(path, false, 0, 4000, 8000);
        AsfFixture.Edit(path, header => AsfFixture.Marker(header, 40_000_000, kind == "long-name" ? new string('A', 4097) : "Duplicate"));
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.Null(Field<NativeChapterMap?>(window, "_nativeChapters"));
        player.SelectAudioTrack(-1);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.True(vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
    });

    [Fact]
    public async Task AsfChapters_NetworkMarkersStayNativeAfterWindowsMetadataInspection() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "network.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(path), ".wmv", "video/x-ms-wmv");
        await Call<bool>(window, "AddItem", new PlaylistItem { Source = server.Url, Title = "ASF HTTP", IsNetwork = true }, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.Equal(new long[] { 0, 4000, 8000 }, Field<NativeChapterMap>(window, "_nativeChapters").Starts);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Math.Abs(player.Time - 4000) < 150);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<NativeChapterMap?>(window, "_compatibilityChapters"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Fact]
    public async Task AsfChapters_PlayingSeekCannotLeaveAStalePausedChapterPosition() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "playing-seek.wma");
        AsfFixture.Write(path, false, 0, 4000, 8000, 12000, 16000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 17_000);
        Invoke(window, "SeekTo", 8500L);
        await Until(() => vlc.Time >= 12500 && vlc.Time < 14500);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", false);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => vlc.Time >= 8000 && vlc.Time < 9500);
    });

    [Fact]
    public async Task AsfChapters_PausedTimelineAndBookmarksUseTheRequestedChapter() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.PreferWindowsPlayback = false;
        settings.RememberPosition = true;
        var path = Path.Combine(TestRoot(window), "bookmark.wma");
        AsfFixture.WriteLongAudio(path, 0, 12000, 24000);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        await Until(() => Field<long>(window, "_lastPlaybackTime") == 12000);
        Invoke(window, "PersistCurrentPosition", true);
        Assert.Equal(12000, settings.ResumePositions[path]);
        Invoke(window, "SeekTo", 15000L);
        await Until(() => Field<long>(window, "_lastPlaybackTime") == 15000);
        Invoke(window, "PersistCurrentPosition", true);
        Assert.Equal(15000, settings.ResumePositions[path]);
    });

    [Fact]
    public async Task AsfChapters_NativeRotationAndLaterHandoffKeepPauseAudioOffCaptionsAndMarkers() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "rotated.wmv");
        AsfFixture.Write(path, true, 0, 4000, 8000);
        File.WriteAllText(Path.ChangeExtension(path, ".srt"), ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "NavigateChapterAsync", true);
        player.SelectAudioTrack(-1);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        var rotated = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
        Assert.Equal(240, rotated.PixelWidth);
        Assert.Equal(320, rotated.PixelHeight);
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.True(vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        await Call(window, "NavigateChapterAsync", true);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => vlc.Time >= 8000 && vlc.Time < 9500);
        Assert.Equal(-1, vlc.AudioTrack);
    });
}
