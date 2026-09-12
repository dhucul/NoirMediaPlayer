using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

[CollectionDefinition("Player UI", DisableParallelization = true)]
public sealed class PlayerUiCollection;

[Collection("Player UI")]
public sealed class LogicAuditRegressionTests
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

    private static async Task WithWindow(Func<MainWindow, Task> test)
    {
        var dispatcher = await Ui.Value.WaitAsync(TimeSpan.FromSeconds(10));
        await dispatcher.InvokeAsync(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "NoirAuditTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var settings = new PlayerSettings { PlaybackRate = 1.5f, AutoPlayNext = false, RememberPosition = false, SnapshotFolder = root };
            var prewarm = typeof(StartupPrewarm);
            prewarm.GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, settings);
            prewarm.GetField("_settingsService", BindingFlags.Static | BindingFlags.NonPublic)!
                .SetValue(null, new SettingsService(Path.Combine(root, "settings.json")));
            var lib = new LibVLC("--aout=dummy", "--vout=dummy", "--quiet");
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
                await Call(window, "DisposePlaybackResourcesAsync").WaitAsync(TimeSpan.FromSeconds(5));
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
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
