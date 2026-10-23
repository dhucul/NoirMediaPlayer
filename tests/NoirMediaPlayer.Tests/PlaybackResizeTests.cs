using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Fact]
    public async Task PlaybackHost_OwnedVideoSurfaceAcceptsFileDrops() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var view = Field<PlaybackView>(window, "VideoView");
        var surface = (Window)typeof(PlaybackView).GetField("_videoWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var path = Path.Combine(TestRoot(window), "drop-on-video.mp4");
        Mp4Fixture.Write(path);
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragEventArgs Drag(RoutedEvent routedEvent)
        {
            // WPF constructs these args internally for OLE drag/drop.
            var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { data, DragDropKeyStates.None, DragDropEffects.Copy, surface, new Point(20, 20) }, null)!;
            args.RoutedEvent = routedEvent;
            return args;
        }
        Assert.True(surface.AllowDrop);
        var over = Drag(DragDrop.DragOverEvent);
        surface.RaiseEvent(over);
        Assert.Equal(DragDropEffects.Copy, over.Effects);
        Assert.True(over.Handled);
        surface.RaiseEvent(Drag(DragDrop.DropEvent));
        await Until(() => Field<Models.PlaylistItem?>(window, "_currentItem")?.Source == path &&
            Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PlaybackHost_VlcPictureFollowsFinalViewport(bool paused, bool hardwareDecoding) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        window.Left = window.Top = 40;
        Field<Models.PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "paused-resize.mp4");
        Mp4Fixture.Write(path);
        Assert.True(await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true));
        var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => player.VoutCount > 0 && player.Time > 100);
        if (paused)
        {
            await Call(window, "TogglePlayPause");
            await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        }
        var before = player.Time;
        var view = Field<PlaybackView>(window, "VideoView");
        bool Fits()
        {
            var native = ResizeGetWindow(view.WindowHandle, 5); // GW_CHILD: VLC's event-thread window.
            var picture = ResizeGetWindow(native, 5);
            if (native == 0 || picture == 0 || !ResizeClientRect(view.WindowHandle, out var host) ||
                !ResizeClientRect(native, out var client) || !ResizeWindowRect(native, out var bounds) ||
                !ResizeWindowRect(picture, out var pixels)) return false;
            if (host.Right != client.Right || host.Bottom != client.Bottom) return false;
            var width = Math.Min(client.Right, client.Bottom * 4d / 3);
            var height = width * 3 / 4;
            return Math.Abs(pixels.Right - pixels.Left - width) <= 2 && Math.Abs(pixels.Bottom - pixels.Top - height) <= 2 &&
                Math.Abs(pixels.Left - bounds.Left - (client.Right - width) / 2) <= 2 &&
                Math.Abs(pixels.Top - bounds.Top - (client.Bottom - height) / 2) <= 2;
        }
        await Until(Fits);
        var convergence = new List<double>();
        var total = new List<double>();
        foreach (var (width, height) in new[] { (1200d, 720d), (1400d, 900d), (1100d, 700d), (1280d, 800d),
            (1360d, 740d), (1140d, 860d), (1320d, 780d), (1240d, 820d) })
        {
            var watch = Stopwatch.StartNew();
            window.Width = width; window.Height = height;
            window.UpdateLayout();
            var arranged = watch.Elapsed.TotalMilliseconds;
            await Until(Fits);
            total.Add(watch.Elapsed.TotalMilliseconds);
            convergence.Add(total[^1] - arranged);
        }
        Invoke(window, "ToggleCompact");
        await Until(Fits);
        Invoke(window, "ToggleFullscreen");
        await Until(Fits);
        if (paused)
        {
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.InRange(player.Time, before - 50, before + 50);
        }
        else
        {
            // A responsive resize can finish before libVLC's cached clock is
            // refreshed. Require continued playback within a bounded wait.
            await Until(() => player.Time > before);
            Assert.Equal(PlaybackState.Playing, Field<PlaybackState>(window, "_playbackState"));
        }
        if (Environment.GetEnvironmentVariable("NOIR_RESIZE_METRICS_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var name = $"native-{(paused ? "paused" : "playing")}{(hardwareDecoding ? "-hardware" : "")}.json";
            File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new
            {
                Paused = paused, HardwareDecoding = hardwareDecoding, NativeConvergenceMs = convergence, AverageNativeConvergenceMs = convergence.Average(),
                MaxNativeConvergenceMs = convergence.Max(), AverageTotalResizeMs = total.Average(),
                PlaybackBeforeMs = before, PlaybackAfterMs = player.Time, CheckedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }, hardwareDecoding: hardwareDecoding, realCompatibilityVideo: true);

    [Fact]
    public async Task PlaybackHost_LiveResizeKeepsVideoAlignedWithoutRepeatedOverlayMoves() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<Models.PlayerSettings>(window, "_settings").PreferWindowsPlayback = false;
        var path = Path.Combine(TestRoot(window), "resize-playback.mp4");
        Mp4Fixture.Write(path);
        Assert.True(await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(path), true));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
        await Until(() => player.VoutCount > 0 && player.Time > 0);
        var view = Field<PlaybackView>(window, "VideoView");
        var overlay = (Window)typeof(PlaybackView).GetField("_overlayWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        bool Aligned()
        {
            var videoPoint = view.PointToScreen(new Point());
            var overlayPoint = overlay.PointToScreen(new Point());
            return overlay.IsVisible && Math.Abs(videoPoint.X - overlayPoint.X) < 2 && Math.Abs(videoPoint.Y - overlayPoint.Y) < 2 &&
                Math.Abs(view.ActualWidth - overlay.ActualWidth) < 2 && Math.Abs(view.ActualHeight - overlay.ActualHeight) < 2;
        }
        window.Width = 1200; window.Height = 720;
        window.Left = 40; window.Top = 40;
        await Until(Aligned);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(overlay).Handle)!;
        var nativeMoves = 0;
        HwndSourceHook hook = (nint handle, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x0046 && lParam != 0) // WM_WINDOWPOSCHANGING
            {
                var position = Marshal.PtrToStructure<ResizeWindowPosition>(lParam);
                if ((position.Flags & 3) != 3) nativeMoves++; // Actual move or resize, excluding z-order-only changes.
            }
            return 0;
        };
        source.AddHook(hook);
        const int steps = 24;
        var times = new List<double>();
        var before = player.Time;
        try
        {
            for (var index = 0; index < steps; index++)
            {
                var size = index < steps / 2 ? index : steps - index;
                var elapsed = Stopwatch.StartNew();
                window.Left = 40 + size * 2;
                window.Top = 40 + size;
                window.Width = 1200 + size * 8;
                window.Height = 720 + size * 4;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                await Until(Aligned);
                times.Add(elapsed.Elapsed.TotalMilliseconds);
                await Task.Delay(16);
            }
        }
        finally { source.RemoveHook(hook); }
        if (Environment.GetEnvironmentVariable("NOIR_RESIZE_METRICS_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "resize.json"), JsonSerializer.Serialize(new
            {
                Steps = steps, NativeOverlayMoves = nativeMoves, AverageStepMs = times.Average(),
                MaxStepMs = times.Max(), PlaybackBeforeMs = before, PlaybackAfterMs = player.Time,
                CheckedUtc = DateTime.UtcNow
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(Aligned());
        Assert.True(player.Time > before, "Playback stopped advancing during window resizing.");
        Assert.True(nativeMoves <= steps * 2, $"Overlay made {nativeMoves} native geometry updates for {steps} resize steps.");
    }, hardwareDecoding: false, realCompatibilityVideo: true);

    [StructLayout(LayoutKind.Sequential)]
    private struct ResizeWindowPosition
    {
        public nint Window, After;
        public int X, Y, Width, Height;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ResizeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern nint ResizeGetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetClientRect")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ResizeClientRect(nint window, out ResizeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ResizeWindowRect(nint window, out ResizeRect rectangle);
}
