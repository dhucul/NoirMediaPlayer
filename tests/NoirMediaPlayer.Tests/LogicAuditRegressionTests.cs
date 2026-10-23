using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PixelFormats = System.Windows.Media.PixelFormats;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using NoirMediaPlayer.Controls;
using Xunit;

namespace NoirMediaPlayer.Tests;

[CollectionDefinition("Player UI", DisableParallelization = true)]
public sealed class PlayerUiCollection;

[Collection("Player UI")]
public sealed partial class LogicAuditRegressionTests
{
    [Fact]
    public async Task PlaybackQueue_DoesNotOvertakeAStopEvenWhenItsWaitTimesOut()
    {
        var queue = new PlaybackOperationQueue();
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var order = new List<string>();
        var stop = queue.Enqueue(() => { entered.Set(); unblock.Wait(); order.Add("stop"); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(TimeSpan.FromMilliseconds(30)));
            var start = queue.Enqueue(() => order.Add("start"));
            Assert.False(start.IsCompleted);
            Assert.False(queue.IsIdle);
            unblock.Set();
            await start.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { "stop", "start" }, order);
        }
        finally { unblock.Set(); await stop; }
    }

    [Fact]
    public async Task PlaybackQueue_CleansUpAfterAnOperationFails()
    {
        var queue = new PlaybackOperationQueue();
        var fault = queue.Enqueue(() => throw new InvalidOperationException());
        var disposed = false;
        await queue.Enqueue(() => disposed = true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fault);
        Assert.True(disposed);
    }

    [Fact]
    public void Shuffle_VisitsEachItemOnceAndOnlyRestartsWhenRequested()
    {
        var cycle = new ShuffleCycle();
        string[] sources = ["a.mp3", "b.mp3", "c.mp3"];
        var random = new Random(1);
        var index = 0;
        cycle.MarkPlayed(sources[index]);
        var visited = new HashSet<int> { index };
        for (var i = 0; i < 2; i++)
        {
            index = cycle.Next(sources, index, false, random);
            Assert.True(visited.Add(index));
            cycle.MarkPlayed(sources[index]);
        }
        Assert.Equal(-1, cycle.Next(sources, index, false, random));
        Assert.NotEqual(index, cycle.Next(sources, index, true, random));
    }

    [Theory]
    [InlineData(95_000, 100_000, true, 95_000)]
    [InlineData(100_000, 100_000, true, 99_999)]
    [InlineData(0, 100_000, true, 0)]
    [InlineData(20_000, 100_000, false, 20_000)]
    public void ExplicitReposition_PreservesTheEndOfMedia(long position, long length, bool explicitPosition, long expected) =>
        Assert.Equal(expected, PlaybackResumePolicy.Resolve(position, length, explicitPosition));

    [Fact]
    public void ResumeBookmark_StillRejectsACompletedOrUnknownLengthPosition()
    {
        Assert.Null(PlaybackResumePolicy.Resolve(95_000, 100_000, false));
        Assert.Null(PlaybackResumePolicy.Resolve(20_000, 0, true));
    }

    [Fact]
    public void SourceIdentity_PreservesNetworkPathQueryAndCredentialCase()
    {
        var comparer = MediaSourceComparer.Instance;
        Assert.True(comparer.Equals(@"C:\Media\Film.mkv", @"c:\media\film.MKV"));
        Assert.True(comparer.Equals("https://EXAMPLE.com/Video", "https://example.com/Video"));
        var sources = new HashSet<string>(comparer)
        {
            "https://example.com/Video?id=A", "https://example.com/video?id=A",
            "https://example.com/Video?id=a", "rtsp://USER:Secret@example.com/live",
            "rtsp://USER:secret@example.com/live"
        };
        Assert.Equal(5, sources.Count);
    }

    [Fact]
    public async Task SpeedSetting_SurvivesActualWindowInitialization() => await WithWindow(async window =>
    {
        Assert.Equal(1.5f, Field<PlayerSettings>(window, "_settings").PlaybackRate);
        Assert.Equal("1.5", ((ComboBoxItem)Field<ComboBox>(window, "SpeedComboBox").SelectedItem).Tag);
        await Task.CompletedTask;
    });

    [Fact]
    public async Task DiscFolders_AndExportedDiscEntriesRoundTrip() => await WithWindow(async window =>
    {
        var root = TestRoot(window);
        var dvd = Path.Combine(root, "dvd");
        var bluray = Path.Combine(root, "bluray");
        Directory.CreateDirectory(Path.Combine(dvd, "VIDEO_TS"));
        Directory.CreateDirectory(Path.Combine(bluray, "BDMV"));
        var items = new[] { DiscService.CreateFromFolder(Path.Combine(dvd, "VIDEO_TS"))!,
            DiscService.CreateFromFolder(Path.Combine(bluray, "BDMV"))! };
        Assert.All(items, item => Assert.True(item.IsDisc));
        var importedFolders = MediaSourceService.ExpandFiles([dvd, bluray]).ToArray();
        Assert.Equal(items.Select(item => item.Source), importedFolders);
        var playlist = Path.Combine(root, "discs.m3u8");
        await (Task)typeof(MainWindow).GetMethod("WritePlaylistAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [playlist, items, CancellationToken.None])!;
        Assert.Equal(items.Select(item => item.Source), MediaSourceService.ExpandFiles([playlist]));
        Assert.Null(DiscService.CreateFromSource("dvd://server/share/"));
        Assert.Null(DiscService.CreateFromSource("dvd:relative/"));
    });

    [Fact]
    public async Task AlreadyQueuedFile_CanBeOpenedAgain() => await WithWindow(async window =>
    {
        var item = CreateWave(window, "duplicate", 4);
        await Call<bool>(window, "AddItem", item, false);
        await Call<int>(window, "ImportSourcesAsync", new[] { item.Source }, true, false);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Single(Queue(window));
        Assert.Same(item, Field<PlaylistItem>(window, "_currentItem"));
    });

    [Fact]
    public async Task FinishedMedia_ReplaysAndRestoresTheQueueMarker() => await WithWindow(async window =>
    {
        var item = CreateWave(window, "replay", 0.4);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Ended);
        Assert.False(item.IsPlaying);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(item.IsPlaying);
    });

    [Fact]
    public async Task LaterPlaybackRequest_WinsDuringTeardown() => await WithWindow(async window =>
    {
        var first = CreateWave(window, "first", 5);
        var last = CreateWave(window, "last", 5);
        await Call<bool>(window, "AddItem", first, false);
        await Call<bool>(window, "AddItem", last, false);
        var openingFirst = Call<bool>(window, "PlayItem", first, true, -1L);
        var openingLast = Call<bool>(window, "PlayItem", last, true, -1L);
        Assert.True(await openingLast);
        await openingFirst;
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(last, Field<PlaylistItem>(window, "_currentItem"));
        Assert.False(first.IsPlaying);
        Assert.True(last.IsPlaying);
        Invoke(window, "Stop_Click", window, new RoutedEventArgs());
        Assert.False(last.IsPlaying);
        await Call(window, "TogglePlayPause");
        await Until(() => last.IsPlaying);
    });

    [Fact]
    public async Task ClearingAnImport_DoesNotAcceptBufferedItems() => await WithWindow(async window =>
    {
        var paths = CreateImport(window);
        var cleared = false;
        Queue(window).CollectionChanged += (_, _) =>
        {
            if (Queue(window).Count != 100 || cleared) return;
            cleared = true;
            window.Dispatcher.BeginInvoke(() => Invoke(window, "ClearPlaylist_Click", window, new RoutedEventArgs()));
        };
        await Call<int>(window, "ImportSourcesAsync", paths, true, false);
        Assert.True(cleared);
        Assert.Empty(Queue(window));
        Assert.Null(Field<object?>(window, "_currentItem"));
        Assert.Equal("Queue cleared", Field<TextBlock>(window, "PlaybackNoticeText").Text);
    });

    [Fact]
    public async Task RemovingTheImportTarget_PreventsDelayedAutoplay() => await WithWindow(async window =>
    {
        var paths = CreateImport(window);
        var removed = false;
        Queue(window).CollectionChanged += (_, _) =>
        {
            if (Queue(window).Count != 100 || removed) return;
            removed = true;
            window.Dispatcher.BeginInvoke(() =>
            {
                Field<ListBox>(window, "PlaylistView").SelectedItem = Queue(window)[0];
                Invoke(window, "RemoveSelected_Click", window, new RoutedEventArgs());
            });
        };
        await Call<int>(window, "ImportSourcesAsync", paths, true, false);
        Assert.True(removed);
        Assert.Null(Field<object?>(window, "_currentItem"));
        Assert.Equal(299, Queue(window).Count);
    });

    [Fact]
    public async Task NewPlaybackChoice_IsNotOverwrittenWhenImportCompletes() => await WithWindow(async window =>
    {
        var paths = CreateImport(window);
        var choice = CreateWave(window, "new-choice", 5);
        Task<bool>? play = null;
        var scheduled = false;
        Queue(window).CollectionChanged += (_, _) =>
        {
            if (Queue(window).Count != 100 || scheduled) return;
            scheduled = true;
            window.Dispatcher.BeginInvoke(() => play = Call<bool>(window, "AddItem", choice, true));
        };
        await Call<int>(window, "ImportSourcesAsync", paths, true, false);
        Assert.NotNull(play);
        Assert.True(await play);
        await Until(() => choice.IsPlaying);
        Assert.Same(choice, Field<PlaylistItem>(window, "_currentItem"));
    });

    [Fact]
    public async Task EjectReservation_RejectsPlaybackBeforeTouchingTheDrive() => await WithWindow(async window =>
    {
        var item = new PlaylistItem { Source = "dvd:///Z:/", Title = "Reserved drive", IsDisc = true };
        await Call<bool>(window, "AddItem", item, false);
        typeof(MainWindow).GetField("_ejectingDriveRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, @"Z:\");
        Assert.False(await Call<bool>(window, "PlayItem", item, true, -1L));
        Assert.Null(Field<object?>(window, "_currentItem"));
        Assert.False(item.IsPlaying);
    });

    [Fact]
    public async Task Shutdown_SavesSettingsAndFinishesWhileNativeCleanupIsBlocked() => await WithWindow(async window =>
    {
        var queue = Field<PlaybackOperationQueue>(window, "_playbackOperations");
        using var entered = new ManualResetEventSlim();
        using var unblock = new ManualResetEventSlim();
        var native = queue.Enqueue(() => { entered.Set(); unblock.Wait(); });
        await Until(() => entered.IsSet);
        try
        {
            var timer = Stopwatch.StartNew();
            var args = new CancelEventArgs();
            Invoke(window, "Window_Closing", window, args);
            Assert.True(args.Cancel);
            await Until(() => Field<bool>(window, "_shutdownComplete"));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
            Assert.False(queue.IsIdle);
            Assert.True(File.Exists(Path.Combine(TestRoot(window), "settings.json")));
        }
        finally
        {
            unblock.Set();
            await native;
            await Field<Task>(window, "_playbackTeardownTask");
        }
    });

    [Fact]
    public async Task ShufflePlayback_StopsAfterOneCycleWithRepeatOff() => await WithWindow(async window =>
    {
        var settings = Field<PlayerSettings>(window, "_settings");
        settings.Shuffle = true;
        settings.AutoPlayNext = true;
        var first = CreateWave(window, "shuffle-first", 0.3);
        var second = CreateWave(window, "shuffle-second", 0.3);
        await Call<bool>(window, "AddItem", first, false);
        await Call<bool>(window, "AddItem", second, false);
        await Call<bool>(window, "PlayItem", first, true, -1L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Ended);
        Assert.Same(second, Field<PlaylistItem>(window, "_currentItem"));
        Assert.False(first.IsPlaying);
        Assert.False(second.IsPlaying);
    });

    [Fact]
    public async Task WindowsNative_VideoAndPausedAspectOverridePreservePosition() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var video = CreateVideo(window, "native-video");
        await Call<bool>(window, "AddItem", video, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Task.Delay(200);
        Assert.True(Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null,
            Field<string?>(window, "_windowsFailureReason") ?? "Windows playback was not selected.");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(Field<WindowsMediaEngine>(window, "_windowsPlayer").Time - 3000) < 150);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.InRange(player.PresentationAspectRatio, 1.777, 1.779);
        Assert.InRange(player.Time, 2900, 3100);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 0;
        await Call<bool>(window, "PlayItem", video, false, 1000L);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
    });

    [Fact]
    public async Task WindowsNative_ReplacementDisposesPreviousSession() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var first = CreateWave(window, "native-first", 20);
        var last = CreateWave(window, "native-last", 20);
        await Call<bool>(window, "AddItem", first, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var previous = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotNull(previous);
        await Call<bool>(window, "AddItem", last, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.Same(last, Field<PlaylistItem>(window, "_currentItem"));
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.False(first.IsPlaying);
        Assert.Throws<ObjectDisposedException>(() => _ = previous.Time);
        Invoke(window, "Stop_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.False(last.IsPlaying);
    });

    [Fact]
    public async Task WindowsNative_DisablingAudioKeepsNativePlayback() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        await Call<bool>(window, "AddItem", CreateWave(window, "native-audio-disable", 20), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Invoke(window, "RefreshTrackSelectors");
        var audio = Field<ComboBox>(window, "AudioTrackComboBox");
        audio.SelectedItem = audio.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, -1));
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.NotNull(player);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Invoke(window, "RefreshTrackSelectors");
        audio.SelectedItem = audio.Items.OfType<ComboBoxItem>().Single(item => item.Tag is WindowsAudioTrack);
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Contains(player.AudioTracks, track => track.Selected);
    });

    [Fact]
    public async Task WindowsNative_AudioTracksSwitchAndSurviveCompatibilityHandoff() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        await Call<bool>(window, "AddItem", CreateVideo(window, "native-multi-audio", audioTracks: 2), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(2, player.AudioTracks.Count);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 100);
        Invoke(window, "RefreshTrackSelectors");
        var audio = Field<ComboBox>(window, "AudioTrackComboBox");
        audio.SelectedItem = audio.Items.OfType<ComboBoxItem>().Single(item => item.Tag is WindowsAudioTrack { Ordinal: 1 });
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(1, Assert.Single(player.AudioTracks, track => track.Selected).Ordinal);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(player.Time, 2900, 3100);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
            Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle &&
            Field<int?>(window, "_pendingCompatibilityAudioOrdinal") is null && !Field<bool>(window, "_hasPendingPosition") &&
            Field<MediaPlayer>(window, "_mediaPlayer").AspectRatio == "16:9");
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(vlc.AudioTrackDescription.Where(track => track.Id >= 0).ElementAt(1).Id, vlc.AudioTrack);
        Assert.InRange(vlc.Time, 2700, 3400);
    });

    [Fact]
    public async Task WindowsNative_SnapshotUsesSourcePixelsAndKeepsPausedSession() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        await Call<bool>(window, "AddItem", CreateVideo(window, "native-snapshot"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 100);
        // Exercise the actual button, including command dispatch, file naming, and encoding.
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(player.Time, 2900, 3100);
        var path = Directory.EnumerateFiles(TestRoot(window), "*.png").Single();
        using var png = File.OpenRead(path);
        var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(320, frame.PixelWidth);
        Assert.Equal(240, frame.PixelHeight);
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[320 * 240 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        var top = (20 * 320 + 20) * 4;
        var bottom = (220 * 320 + 20) * 4;
        Assert.True(pixels[top + 1] > 150 && pixels[top + 2] < 80, "Snapshot must capture the green top of the frame at 3 seconds.");
        Assert.True(pixels[bottom] > 150 && pixels[bottom + 2] < 80, "Snapshot must preserve the blue bottom of the frame.");
    });

    [Theory]
    [InlineData(".srt")]
    [InlineData(".vtt")]
    public async Task WindowsNative_PlainCaptionsRenderSeekAndAppearInSnapshots(string extension) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "native-subtitles");
        var path = Path.Combine(TestRoot(window), "captions" + extension);
        File.WriteAllText(path, extension == ".srt"
            ? "1\n00:00:00,000 --> 00:00:02,000\nFirst caption\n\n2\n00:00:02,000 --> 00:00:08,000\nNative caption\nSecond line\n"
            : "WEBVTT\n\nfirst\n00:00.000 --> 00:02.000\nFirst caption\n\nsecond\n00:02.000 --> 00:08.000\nNative caption\nSecond line\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        var presenter = Field<SubtitlePresenter>(window, "NativeSubtitlePresenter");
        await Until(() => presenter.Text == "Native caption\nSecond line");
        Assert.InRange(presenter.PictureAspectRatio, 1.33, 1.34);
        Invoke(window, "Snapshot_Click", window, new RoutedEventArgs());
        await Until(() => Directory.EnumerateFiles(TestRoot(window), "*.png").Any());
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        using var png = File.OpenRead(Directory.EnumerateFiles(TestRoot(window), "*.png").Single());
        var frame = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[320 * 240 * 4];
        bitmap.CopyPixels(pixels, 320 * 4, 0);
        var white = 0;
        for (var y = 180; y < 235; y++)
        for (var x = 10; x < 310; x++)
        {
            var offset = (y * 320 + x) * 4;
            if (pixels[offset] > 180 && pixels[offset + 1] > 180 && pixels[offset + 2] > 180) white++;
        }
        Assert.True(white > 20, $"The snapshot should include the active subtitle glyphs over the blue source frame (white pixels: {white}).");
        Invoke(window, "SeekTo", 1000L);
        await Until(() => presenter.Text == "First caption");
        var subtitles = Field<ComboBox>(window, "SubtitleTrackComboBox");
        subtitles.SelectedIndex = 0;
        Assert.Equal(string.Empty, presenter.Text);
        subtitles.SelectedIndex = 1;
        await Until(() => presenter.Text == "First caption");
        Invoke(window, "Stop_Click", window, new RoutedEventArgs());
        Assert.Equal(string.Empty, presenter.Text);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowsNative_SubtitleSelectionSurvivesCompatibilityHandoff(bool selected) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateVideo(window, "native-caption-handoff");
        var path = Path.Combine(TestRoot(window), "captions.srt");
        File.WriteAllText(path, "1\n00:00:00,000 --> 00:00:20,000\nRetained caption\n");
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", path));
        if (!selected) Field<ComboBox>(window, "SubtitleTrackComboBox").SelectedIndex = 0;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(Field<WindowsMediaEngine>(window, "_windowsPlayer").Time - 3000) < 100);
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        await Until(() => Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused &&
            Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle &&
            !Field<bool>(window, "_hasPendingPosition") && Field<MediaPlayer>(window, "_mediaPlayer").AspectRatio == "16:9");
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.SpuDescription.Any(track => track.Id >= 0) && Field<object?>(window, "_pendingCompatibilitySubtitles") is null);
        Assert.Equal(selected, vlc.Spu >= 0);
        Assert.InRange(vlc.Time, 2700, 3400);
        Assert.Equal(string.Empty, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").Text);
    });

    [Fact]
    public async Task WindowsVideoHost_OverlayFollowsResizeMiniFullscreenAndMinimize() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var view = Field<PlaybackView>(window, "VideoView");
        Assert.NotEqual(0, view.WindowHandle);
        var overlay = (Window)typeof(PlaybackView).GetField("_overlayWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var surface = (Window)typeof(PlaybackView).GetField("_videoWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Assert.Same(window, overlay.Owner);
        Assert.Same(window, surface.Owner);
        Assert.Equal(new System.Windows.Interop.WindowInteropHelper(surface).Handle, RotationGetAncestor(view.WindowHandle, 2));
        Assert.NotEqual(new System.Windows.Interop.WindowInteropHelper(window).Handle, RotationGetAncestor(view.WindowHandle, 2));
        Assert.False(overlay.ShowInTaskbar);
        Assert.False(overlay.ShowActivated);
        Assert.False(surface.ShowInTaskbar);
        Assert.False(surface.ShowActivated);
        bool Aligned()
        {
            if (!overlay.IsVisible || !surface.IsVisible || view.ActualWidth <= 0) return false;
            var videoPoint = view.PointToScreen(new Point());
            var overlayPoint = overlay.PointToScreen(new Point());
            if (!RotationGetWindowRect(view.WindowHandle, out var native)) return false;
            return Math.Abs(videoPoint.X - native.Left) < 2 && Math.Abs(videoPoint.Y - native.Top) < 2 &&
                Math.Abs(videoPoint.X - overlayPoint.X) < 2 && Math.Abs(videoPoint.Y - overlayPoint.Y) < 2 &&
                Math.Abs(view.ActualWidth - overlay.ActualWidth) < 2 && Math.Abs(view.ActualHeight - overlay.ActualHeight) < 2;
        }
        await Until(Aligned);
        window.Width = 1100;
        window.Height = 680;
        await Until(Aligned);
        Invoke(window, "ToggleCompact");
        await Until(() => view.ActualWidth < 600 && Aligned());
        Invoke(window, "ToggleFullscreen");
        await Until(Aligned);
        window.WindowState = WindowState.Minimized;
        await Until(() => !overlay.IsVisible && !surface.IsVisible);
        window.WindowState = WindowState.Normal;
        await Until(Aligned);
    });

    [Fact]
    public async Task WindowsNative_FontAttributesFallBackWithoutDroppingLoadedCaptions() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var plain = Path.Combine(TestRoot(window), "plain.srt");
        var styled = Path.Combine(TestRoot(window), "styled.srt");
        File.WriteAllText(plain, "1\n00:00:00,000 --> 00:00:20,000\nPlain\n");
        File.WriteAllText(styled, "1\n00:00:00,000 --> 00:00:20,000\n<font color=\"#FF0000\" face=\"Arial\">Font attributes</font>\n");
        await Call<bool>(window, "AddItem", CreateVideo(window, "native-styled"), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", plain));
        Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", styled));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        await Until(() => vlc.SpuDescription.Count(track => track.Id >= 0) == 2);
        await Until(() => vlc.Spu == vlc.SpuDescription.Last(track => track.Id >= 0).Id);
    });

    [Fact]
    public async Task WindowsNative_MatroskaAudioPlaysNativelyAndCaptionsKeepVlcSupport() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var plain = Path.Combine(TestRoot(window), "native-audio.mkv");
        var captioned = Path.Combine(TestRoot(window), "native-captioned.mkv");
        MatroskaFixture.Write(plain, subtitles: false);
        MatroskaFixture.Write(captioned, subtitles: true, subtitleText: "{\\an7}Positioned captions");
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(plain), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Assert.True(Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null, Field<string?>(window, "_windowsFailureReason"));
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(captioned), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && Field<WindowsMediaEngine?>(window, "_windowsPlayer") is null);
        await Until(() => Field<MediaPlayer>(window, "_mediaPlayer").SpuDescription.Any(track => track.Id >= 0));
        Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
    });

    [Fact]
    public async Task WindowsNative_HttpAudioPlaysAndSeeksWithoutVlc() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var wave = CreateWave(window, "native-http", 20);
        await using var server = new LoopbackMediaServer(File.ReadAllBytes(wave.Source), ".wav", "audio/wav");
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        Assert.Equal(new FileInfo(wave.Source).Length, (await http.GetByteArrayAsync(server.Url)).LongLength);
        var item = new PlaylistItem { Source = server.Url, Title = "Native HTTP", IsNetwork = true };
        await Call<bool>(window, "AddItem", item, true);
        try { await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing, timeoutSeconds: 12); }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException($"HTTP playback: {Field<PlaybackState>(window, "_playbackState")}; requests: {server.Requests}; reason: {Field<string?>(window, "_windowsFailureReason")}");
        }
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.True(player.IsSeekable);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 3000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 100);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsNative_UnsupportedSourceAutomaticallyRetriesVlc(bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var path = Path.Combine(TestRoot(window), "native-unsupported.avi");
        using (var stream = File.Create(path))
        {
            stream.Write(Encoding.ASCII.GetBytes("YUV4MPEG2 W32 H24 F2:1 Ip A1:1 C420jpeg\n"));
            for (var i = 0; i < 20; i++)
            {
                stream.Write(Encoding.ASCII.GetBytes("FRAME\n"));
                stream.Write(Enumerable.Repeat((byte)210, 32 * 24).ToArray());
                stream.Write(Enumerable.Repeat((byte)128, 32 * 24 / 2).ToArray());
            }
        }
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(path), ".avi", "video/x-msvideo") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Unsupported HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(path);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing, timeoutSeconds: 12);
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.NotNull(Field<string?>(window, "_windowsFailureReason"));
        Assert.NotNull(Field<Media?>(window, "_currentMedia"));
    });

    private static void ShowPlaybackWindow(MainWindow window)
    {
        // Exercise the real HWND renderer without startup imports or changes to user AACS files.
        var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("Window_Loaded", BindingFlags.Instance | BindingFlags.NonPublic)!);
        window.Loaded -= loaded;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -10000;
        window.ShowActivated = false;
        window.Show();
        Field<DispatcherTimer>(window, "_uiTimer").Start();
    }

    private static PlaylistItem CreateVideo(MainWindow window, string name, int audioTracks = 0)
    {
        const int width = 320, height = 240, count = 60;
        byte[] Frame(bool later)
        {
            var pixels = new byte[width * height * 3];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = later ? (byte)(y < height / 2 ? 20 : 200) : (byte)30;
                pixels[offset + 1] = later ? (byte)(y < height / 2 ? 190 : 30) : (byte)30;
                pixels[offset + 2] = later ? (byte)25 : (byte)220;
            }
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3)));
            using var jpeg = new MemoryStream();
            encoder.Save(jpeg);
            return jpeg.ToArray();
        }
        var earlyFrame = Frame(false);
        var frame = Frame(true);
        byte[] Data(Action<BinaryWriter> write)
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            write(writer);
            return buffer.ToArray();
        }
        byte[] Chunk(string type, byte[] data) => Data(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes(type)); writer.Write(data.Length); writer.Write(data);
            if (data.Length % 2 != 0) writer.Write((byte)0);
        });
        var avih = Data(writer =>
        {
            foreach (var value in new[] { 500000, frame.Length * 2 + audioTracks * 16000, 0, 0x10, count, 0, 1 + audioTracks, frame.Length, width, height, 0, 0, 0, 0 }) writer.Write(value);
        });
        var strh = Data(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("vidsMJPG")); writer.Write(0); writer.Write((short)0); writer.Write((short)0);
            foreach (var value in new[] { 0, 1, 2, 0, count, frame.Length, -1, 0 }) writer.Write(value);
            foreach (var value in new short[] { 0, 0, width, height }) writer.Write(value);
        });
        var strf = Data(writer =>
        {
            writer.Write(40); writer.Write(width); writer.Write(height); writer.Write((short)1); writer.Write((short)24);
            writer.Write(Encoding.ASCII.GetBytes("MJPG"));
            foreach (var value in new[] { frame.Length, 0, 0, 0, 0 }) writer.Write(value);
        });
        var streams = new List<byte[]> { Chunk("LIST", Encoding.ASCII.GetBytes("strl").Concat(Chunk("strh", strh)).Concat(Chunk("strf", strf)).ToArray()) };
        for (var track = 0; track < audioTracks; track++)
        {
            var audioHeader = Data(writer =>
            {
                writer.Write(Encoding.ASCII.GetBytes("auds")); writer.Write(0); writer.Write(0); writer.Write((short)0); writer.Write((short)0);
                foreach (var value in new[] { 0, 1, 8000, 0, count * 4000, 8000, -1, 2 }) writer.Write(value);
                foreach (var value in new short[] { 0, 0, 0, 0 }) writer.Write(value);
            });
            var audioFormat = Data(writer =>
            {
                writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
                writer.Write((short)2); writer.Write((short)16);
            });
            streams.Add(Chunk("LIST", Encoding.ASCII.GetBytes("strl").Concat(Chunk("strh", audioHeader)).Concat(Chunk("strf", audioFormat)).ToArray()));
        }
        var header = Chunk("LIST", Encoding.ASCII.GetBytes("hdrl").Concat(Chunk("avih", avih)).Concat(streams.SelectMany(stream => stream)).ToArray());
        var chunks = new List<(string Type, byte[] Data)>();
        for (var frameIndex = 0; frameIndex < count; frameIndex++)
        {
            chunks.Add(("00dc", frameIndex < 4 ? earlyFrame : frame));
            for (var track = 0; track < audioTracks; track++) chunks.Add(($"{track + 1:00}wb", new byte[8000]));
        }
        var movi = Chunk("LIST", Encoding.ASCII.GetBytes("movi").Concat(chunks.SelectMany(chunk => Chunk(chunk.Type, chunk.Data))).ToArray());
        var index = Chunk("idx1", Data(writer =>
        {
            var offset = 4;
            foreach (var chunk in chunks)
            {
                writer.Write(Encoding.ASCII.GetBytes(chunk.Type)); writer.Write(0x10); writer.Write(offset); writer.Write(chunk.Data.Length);
                offset += 8 + chunk.Data.Length + chunk.Data.Length % 2;
            }
        }));
        var path = Path.Combine(TestRoot(window), name + ".avi");
        File.WriteAllBytes(path, Chunk("RIFF", Encoding.ASCII.GetBytes("AVI ").Concat(header).Concat(movi).Concat(index).ToArray()));
        return MediaSourceService.CreateFileItem(path);
    }

    private static readonly Lazy<Task<Dispatcher>> Ui = new(() =>
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    });

    private static async Task WithWindow(Func<MainWindow, Task> test, bool hardwareDecoding = true, bool realCompatibilityVideo = false)
    {
        var dispatcher = await Ui.Value.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcher.InvokeAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "NoirAuditTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new PlayerSettings { HardwareDecoding = hardwareDecoding, PlaybackRate = 1.5f, AutoPlayNext = false, RememberPosition = false, SnapshotFolder = root };
            var prewarm = typeof(StartupPrewarm);
            prewarm.GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
            prewarm.GetField("_settingsService", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, new SettingsService(Path.Combine(root, "settings.json")));
            var options = new List<string> { "--aout=dummy", realCompatibilityVideo ? "--vout=direct3d11" : "--vout=dummy", "--quiet" };
            if (!hardwareDecoding) options.Add("--avcodec-hw=none");
            var lib = new LibVLC(options.ToArray());
            prewarm.GetField("_engineTask", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, Task.FromResult(new PlaybackEngine(lib, new MediaPlayer(lib))));
            var window = new MainWindow();
            try { await test(window); }
            finally
            {
                typeof(MainWindow).GetField("_isClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                var lifetime = Field<CancellationTokenSource>(window, "_lifetimeCancellation");
                if (!lifetime.IsCancellationRequested) lifetime.Cancel();
                foreach (var name in new[] { "_uiTimer", "_noticeTimer", "_searchDebounceTimer" }) Field<DispatcherTimer>(window, name).Stop();
                // VLC's subtitle decoder can wait five seconds for a video output
                // after an immediate paused seek. Drain it before deleting fixtures;
                // application shutdown deadlines have their own regression test.
                await Call(window, "DisposePlaybackResourcesAsync").WaitAsync(TimeSpan.FromSeconds(10));
                if (window.IsLoaded)
                {
                    typeof(MainWindow).GetField("_shutdownComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    window.Close();
                }
                if (Field<Task?>(window, "_settingsSaveTask") is { } save) await save;
                var resolvedRoot = Path.GetFullPath(root);
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolvedRoot);
                Assert.StartsWith("NoirAuditTests-", Path.GetFileName(resolvedRoot));
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static string TestRoot(MainWindow window) => Field<PlayerSettings>(window, "_settings").SnapshotFolder;
    private static ObservableCollection<PlaylistItem> Queue(MainWindow window) => Field<ObservableCollection<PlaylistItem>>(window, "_playlist");
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
    private static Task Call(MainWindow window, string name, params object[] args) => (Task)Invoke(window, name, args)!;
    private static Task<T> Call<T>(MainWindow window, string name, params object[] args) => (Task<T>)Invoke(window, name, args)!;
    private static async Task Until(Func<bool> condition, int timeoutSeconds = 5)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string[] CreateImport(MainWindow window) => Enumerable.Range(0, 300).Select(index =>
    {
        var path = Path.Combine(TestRoot(window), $"import-{index}.mp3");
        File.WriteAllBytes(path, []);
        return path;
    }).ToArray();

    private static PlaylistItem CreateWave(MainWindow window, string name, double seconds)
    {
        var path = Path.Combine(TestRoot(window), name + ".wav");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            var size = (int)(seconds * 16_000);
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + size);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size); writer.Write(new byte[size]);
        }
        return MediaSourceService.CreateFileItem(path);
    }
}
