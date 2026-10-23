using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

// Physical-disc rendering is opt-in and runs alone in a fresh test process.
public sealed class DvdStartupFitFactAttribute : FactAttribute
{
    public DvdStartupFitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOIR_DVD_FIT_SOURCE")))
            Skip = "Set NOIR_DVD_FIT_SOURCE and run this focused test explicitly with an inserted DVD.";
    }
}

public sealed partial class LogicAuditRegressionTests
{
    [DvdStartupFitFact]
    [Trait("Category", "ManualDVD")]
    public async Task DvdStartupFit_OriginalFitsBeforeAspectToggle()
    {
        var source = Environment.GetEnvironmentVariable("NOIR_DVD_FIT_SOURCE")!;
        var hardware = Environment.GetEnvironmentVariable("NOIR_DVD_FIT_HARDWARE") == "1";
        var softwareWpf = Environment.GetEnvironmentVariable("NOIR_DVD_FIT_SOFTWARE_UI") != "0";
        var autoVideoOutput = Environment.GetEnvironmentVariable("NOIR_DVD_FIT_AUTO_OUTPUT") == "1";
        System.Windows.Media.RenderOptions.ProcessRenderMode = softwareWpf ? RenderMode.SoftwareOnly : RenderMode.Default;
        await WithWindow(async window =>
        {
            ShowPlaybackWindow(window, visible: true);
            window.UpdateLayout();
            window.Activate();
            Assert.Single(System.Windows.Application.Current.Windows.OfType<MainWindow>());
            Field<NoirMediaPlayer.Models.PlayerSettings>(window, "_settings").PlaybackRate = 1;
            var selector = Field<ComboBox>(window, "AspectRatioComboBox");
            Assert.Equal(0, selector.SelectedIndex);
            var item = DiscService.CreateFromSource(source);
            Assert.NotNull(item);
            Assert.True(await Call<bool>(window, "AddItem", item, true));
            var player = Field<LibVLCSharp.Shared.MediaPlayer>(window, "_mediaPlayer");
            var view = Field<PlaybackView>(window, "VideoView");
            await Until(() => player.VoutCount > 0 && ReadBounds(view) is not null, timeoutSeconds: 20);
            await Task.Delay(1000);
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            var initial = ReadBounds(view)!;
            var initialScale = player.Scale;
            var initialAspect = player.AspectRatio;
            var captureDirectory = Environment.GetEnvironmentVariable("NOIR_DVD_FIT_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                CaptureDisplayedDvd(view, Path.Combine(captureDirectory, "initial-playing.png"));
                await Call(window, "TogglePlayPause");
                await Until(() => player.State == VLCState.Paused);
                await Task.Delay(200);
                CaptureDisplayedDvd(view, Path.Combine(captureDirectory, "initial-paused.png"));
            }
            selector.SelectedIndex = 4; // 1:1, matching the reported workaround.
            await Task.Delay(500);
            selector.SelectedIndex = 0; // Original.
            await Task.Delay(1000);
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            var afterToggle = ReadBounds(view)!;
            if (!string.IsNullOrWhiteSpace(captureDirectory))
                CaptureDisplayedDvd(view, Path.Combine(captureDirectory, "after-toggle.png"));
            double? frameDifference = string.IsNullOrWhiteSpace(captureDirectory) ? null : CompareDvdFrames(
                Path.Combine(captureDirectory, "initial-paused.png"), Path.Combine(captureDirectory, "after-toggle.png"));
            var report = new
            {
                Source = source, HardwareDecoding = hardware, Vout = autoVideoOutput ? "automatic" : "direct3d11", SoftwareWpf = softwareWpf,
                Initial = initial, InitialFits = Fits(initial), InitialScale = initialScale, InitialAspect = initialAspect,
                AfterToggle = afterToggle, AfterToggleFits = Fits(afterToggle), Scale = player.Scale, Aspect = player.AspectRatio,
                VideoOutputs = player.VoutCount, State = player.State.ToString(), PlaybackMs = player.Time,
                MeanPausedFrameDifference = frameDifference,
                ApplicationAssembly = typeof(MainWindow).Assembly.Location, CheckedUtc = DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (Environment.GetEnvironmentVariable("NOIR_DVD_FIT_REPORT") is { Length: > 0 } destination)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
                File.WriteAllText(destination, json);
            }
            Assert.True(Fits(initial), "DVD startup does not fit before the aspect toggle. " + json);
            Assert.True(Fits(afterToggle), "Original does not fit after the aspect toggle. " + json);
            Assert.Equal(0, initialScale);
            Assert.True(frameDifference is null or < 6,
                "The paused DVD image changes after the aspect workaround despite identical window bounds. " + json);
        }, hardwareDecoding: hardware, realCompatibilityVideo: true, testTimeoutSeconds: 60, autoCompatibilityVideo: autoVideoOutput);
    }

    private sealed record DvdFitBounds(int HostWidth, int HostHeight, int NativeWidth, int NativeHeight,
        int PictureX, int PictureY, int PictureWidth, int PictureHeight);

    private static DvdFitBounds? ReadBounds(PlaybackView view)
    {
        var native = ResizeGetWindow(view.WindowHandle, 5);
        var picture = native == 0 ? 0 : ResizeGetWindow(native, 5);
        if (native == 0 || picture == 0 || !ResizeClientRect(view.WindowHandle, out var host) ||
            !ResizeClientRect(native, out var client) || !ResizeWindowRect(native, out var bounds) ||
            !ResizeWindowRect(picture, out var pixels) || pixels.Right <= pixels.Left || pixels.Bottom <= pixels.Top) return null;
        return new(host.Right, host.Bottom, client.Right, client.Bottom, pixels.Left - bounds.Left, pixels.Top - bounds.Top,
            pixels.Right - pixels.Left, pixels.Bottom - pixels.Top);
    }

    private static bool Fits(DvdFitBounds bounds) =>
        Math.Abs(bounds.NativeWidth - bounds.HostWidth) <= 2 && Math.Abs(bounds.NativeHeight - bounds.HostHeight) <= 2 &&
        bounds.PictureX >= -2 && bounds.PictureY >= -2 && bounds.PictureX + bounds.PictureWidth <= bounds.HostWidth + 2 &&
        bounds.PictureY + bounds.PictureHeight <= bounds.HostHeight + 2;

    private static double CompareDvdFrames(string before, string after)
    {
        static BitmapSource Load(string path)
        {
            using var input = File.OpenRead(path);
            var image = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            return new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        }
        var first = Load(before);
        var second = Load(after);
        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
        var stride = first.PixelWidth * 4;
        var a = new byte[stride * first.PixelHeight];
        var b = new byte[a.Length];
        first.CopyPixels(a, stride, 0);
        second.CopyPixels(b, stride, 0);
        long total = 0, samples = 0;
        // Ignore the app's transient notice at the top and state badge at the bottom.
        for (var y = first.PixelHeight * 15 / 100; y < first.PixelHeight * 85 / 100; y++)
            for (var x = first.PixelWidth / 10; x < first.PixelWidth * 9 / 10; x++)
                for (var c = 0; c < 3; c++)
                {
                    var offset = y * stride + x * 4 + c;
                    total += Math.Abs(a[offset] - b[offset]);
                    samples++;
                }
        return (double)total / samples;
    }

    private static void CaptureDisplayedDvd(PlaybackView view, string path)
    {
        // Inspect only this displayed viewport through GDI. Do not invoke VLC's
        // snapshot feature or request decoder/GPU frame readback.
        Assert.True(ResizeWindowRect(view.WindowHandle, out var bounds));
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var desktop = DvdFitGetDC(0);
        var memory = DvdFitCreateCompatibleDC(desktop);
        var bitmap = DvdFitCreateCompatibleBitmap(desktop, width, height);
        var previous = DvdFitSelectObject(memory, bitmap);
        try
        {
            Assert.True(DvdFitBitBlt(memory, 0, 0, width, height, desktop, bounds.Left, bounds.Top, 0x40CC0020));
            var pixels = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(pixels));
            using var output = File.Create(path);
            encoder.Save(output);
        }
        finally
        {
            DvdFitSelectObject(memory, previous);
            DvdFitDeleteObject(bitmap);
            DvdFitDeleteDC(memory);
            DvdFitReleaseDC(0, desktop);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetDC")] private static extern nint DvdFitGetDC(nint window);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int DvdFitReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static extern nint DvdFitCreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")] private static extern nint DvdFitCreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern nint DvdFitSelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DvdFitBitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DvdFitDeleteObject(nint value);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DvdFitDeleteDC(nint dc);
}
