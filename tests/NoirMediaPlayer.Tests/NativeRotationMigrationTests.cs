using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(90u, false)]
    [InlineData(180u, false)]
    [InlineData(270u, false)]
    [InlineData(0u, true)]
    [InlineData(90u, true)]
    [InlineData(180u, true)]
    [InlineData(270u, true)]
    public async Task NativeRotation_RenderedCornersAndLetterboxingFollowEveryTurn(uint sourceRotation, bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "rotation.mp4");
        Mp4Fixture.WriteOriented(source, sourceRotation, aspectNumerator: 2);
        var original = File.ReadAllBytes(source);
        await using var server = network ? new LoopbackMediaServer(original, ".mp4", "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Rotation HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine?>(window, "_windowsPlayer");
        Assert.True(player is not null, Field<string?>(window, "_windowsFailureReason"));
        Assert.NotNull(player);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Until(() => Field<long>(window, "_lastPlaybackLength") > 10000);
        Invoke(window, "SeekTo", 3000L);
        await Until(() => Math.Abs(player.Time - 3000) < 100);
        player.SelectAudioTrack(-1);
        foreach (var rotation in new[] { 0, 90, 180, 270, 0 })
        {
            if (rotation != 0 || Field<int>(window, "_rotation") != 0)
                Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
            await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
            Assert.Equal(rotation, Field<int>(window, "_rotation"));
            var total = (sourceRotation + (uint)rotation) % 360;
            var aspect = total is 0 or 180 ? 640d / 240 : 240d / 640;
            await AssertRotationPicture(window, total, aspect);
            var captured = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(total is 0 or 180 ? 640 : 240, captured.PixelWidth);
            Assert.Equal(total is 0 or 180 ? 240 : 640, captured.PixelHeight);
            AssertOrientedCorners(captured, total);
            Assert.InRange(player.PresentationAspectRatio, aspect - 0.001, aspect + 0.001);
            Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
            Assert.Null(Field<Media?>(window, "_currentMedia"));
            Assert.InRange(player.Time, 2900, 3100);
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
            Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRotation_ResizeMiniPlayerAndFullscreenKeepOriginalProportions(bool portrait) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "resize.mp4");
        Mp4Fixture.WriteOriented(source, 0);
        await Call<bool>(window, "AddItem", MediaSourceService.CreateFileItem(source), true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        if (portrait) Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        var rotation = portrait ? 90u : 0u;
        var aspect = portrait ? 0.75 : 4d / 3;
        window.Width = 1050; window.Height = 680;
        await AssertRotationPicture(window, rotation, aspect);
        window.Width = 1400; window.Height = 960;
        await AssertRotationPicture(window, rotation, aspect);
        Invoke(window, "ToggleCompact");
        await AssertRotationPicture(window, rotation, aspect);
        Invoke(window, "ToggleCompact");
        Invoke(window, "ToggleFullscreen");
        await AssertRotationPicture(window, rotation, aspect);
        Invoke(window, "ToggleFullscreen");
        window.WindowState = WindowState.Minimized;
        await Task.Delay(50);
        window.WindowState = WindowState.Normal;
        await AssertRotationPicture(window, rotation, aspect);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<string?>(window, "_windowsFailureReason"));
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRotation_CaptionsChaptersRestartAndFallbackKeepTheirState(bool network) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var source = Path.Combine(TestRoot(window), "retained.mp4");
        Mp4Fixture.Write(source, Mp4Fixture.Nero(0, 4000, 8000));
        await using var server = network ? new LoopbackMediaServer(File.ReadAllBytes(source), ".mp4", "video/mp4") : null;
        var item = network ? new PlaylistItem { Source = server!.Url, Title = "Retained HTTP", IsNetwork = true } : MediaSourceService.CreateFileItem(source);
        var captions = Path.ChangeExtension(source, ".webvtt");
        File.WriteAllText(captions, BackgroundSubtitleFile());
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        if (network) Assert.True(await Call<bool>(window, "LoadExternalSubtitleAsync", captions));
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        player.SelectAudioTrack(-1);
        await Call(window, "NavigateChapterAsync", true);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        var plainPath = Path.Combine(TestRoot(window), "no-captions.png");
        await Task.Run(() => player.SaveSnapshot(plainPath));
        var picture = ReadCaptionBitmap(plainPath);
        var selector = Field<ComboBox>(window, "SubtitleTrackComboBox");
        foreach (var selection in new[] { 0, 1 })
        {
            selector.SelectedIndex = selection;
            var cue = selection == 0 ? null : PlainSubtitleTrack.TryLoad(captions)!.CueAt(4000);
            var image = ReadCaptionBitmap(await CapturePlayerSnapshot(window));
            Assert.Equal(CaptionPixels(SubtitlePresenter.Composite(picture, cue)), CaptionPixels(image));
            if (selection != 0)
                Assert.InRange(Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio, 0.749, 0.751);
        }
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.InRange(player.Time, 3900, 4100);
        Assert.DoesNotContain(player.AudioTracks, track => track.Selected);
        await Call(window, "NavigateChapterAsync", true);
        Assert.InRange(player.Time, 7900, 8100);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        await Until(() => player.Time > 8200);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        // Reopening a rotated source uses the native presentation from its first frame.
        Assert.True(await Call<bool>(window, "PlayItem", item, false, 4000L));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !Field<bool>(window, "_hasPendingPosition"));
        var restarted = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        Assert.NotSame(player, restarted);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        Assert.Equal(90, Field<int>(window, "_rotation"));
        Assert.Equal(1, selector.SelectedIndex);
        restarted.SelectAudioTrack(-1);
        // A ratio change now stays native; a later compatibility feature still
        // carries both the rotation and ratio into the existing handoff.
        Field<ComboBox>(window, "AspectRatioComboBox").SelectedIndex = 1;
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(restarted, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.True(await Call<bool>(window, "EnsureVlcForFeatureAsync"));
        var vlc = Field<MediaPlayer>(window, "_mediaPlayer");
        Assert.Equal(-1, vlc.AudioTrack);
        Assert.True(vlc.Spu >= 0);
        Assert.Single(vlc.SpuDescription, track => track.Id >= 0);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        Assert.InRange(vlc.Time, 3800, 5100);
        Assert.Equal(1.5f, vlc.Rate);
        Assert.Equal(BackgroundSubtitleFile(), File.ReadAllText(captions));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRotation_AudioKeepsItsNativeTransportAndCaptions(bool hardware) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        var item = CreateWave(window, "audio-rotation", 20);
        var captions = Path.ChangeExtension(item.Source, ".srt");
        File.WriteAllText(captions, ColorSubtitleFile(".srt"));
        await Call<bool>(window, "AddItem", item, true);
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        var player = Field<WindowsMediaEngine>(window, "_windowsPlayer");
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var position = player.Time;
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Field<PlaybackOperationQueue>(window, "_playbackOperations").IsIdle);
        Assert.Same(player, Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Assert.Null(Field<Media?>(window, "_currentMedia"));
        Assert.False(player.HasVideo);
        Assert.InRange(player.Time, position - 50, position + 50);
        Assert.Equal(0, Field<SubtitlePresenter>(window, "NativeSubtitlePresenter").PictureAspectRatio);
        Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
    }, hardwareDecoding: hardware);

    private static async Task AssertRotationPicture(MainWindow window, uint rotation, double aspect, bool later = false)
    {
        // Read the window that owns the actual video surface, including an owned
        // video window. Inspect rendered pixels independently of snapshot decoding.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var top = RotationGetAncestor(Field<PlaybackView>(window, "VideoView").WindowHandle, 2); // GA_ROOT
                var frame = CaptureRotationWindow(top);
                Assert.True(RotationGetWindowRect(top, out var owner));
                Assert.True(RotationGetWindowRect(Field<PlaybackView>(window, "VideoView").WindowHandle, out var child));
                var picture = new CroppedBitmap(frame, new Int32Rect(child.Left - owner.Left, child.Top - owner.Top, child.Right - child.Left, child.Bottom - child.Top));
                var pixels = CaptionPixels(picture);
                int left = picture.PixelWidth, topRow = picture.PixelHeight, right = -1, bottom = -1;
                for (var y = 0; y < picture.PixelHeight; y++)
                    for (var x = 0; x < picture.PixelWidth; x++)
                    {
                        var offset = (y * picture.PixelWidth + x) * 4;
                        if (Math.Max(pixels[offset], Math.Max(pixels[offset + 1], pixels[offset + 2])) < 120) continue;
                        left = Math.Min(left, x); topRow = Math.Min(topRow, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                    }
                Assert.True(right > left && bottom > topRow, "The composed video must contain decoded pixels.");
                var expectedWidth = Math.Min(picture.PixelWidth, picture.PixelHeight * aspect);
                var expectedHeight = expectedWidth / aspect;
                Assert.InRange(right - left + 1, expectedWidth - 4, expectedWidth + 4);
                Assert.InRange(bottom - topRow + 1, expectedHeight - 4, expectedHeight + 4);
                Assert.InRange(left, (picture.PixelWidth - expectedWidth) / 2 - 3, (picture.PixelWidth - expectedWidth) / 2 + 3);
                Assert.InRange(topRow, (picture.PixelHeight - expectedHeight) / 2 - 3, (picture.PixelHeight - expectedHeight) / 2 + 3);
                AssertOrientedCorners(new CroppedBitmap(picture, new Int32Rect(left, topRow, right - left + 1, bottom - topRow + 1)), rotation, later);
                return;
            }
            catch (Xunit.Sdk.XunitException) when (clock.Elapsed < TimeSpan.FromSeconds(3)) { await Task.Delay(50); }
        }
    }

    private static unsafe BitmapSource CaptureRotationWindow(nint window)
    {
        Assert.True(RotationGetWindowRect(window, out var rect));
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var dc = RotationCreateCompatibleDC(0);
        var info = new RotationBitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = RotationCreateDIBSection(dc, &info, 0, out var bits, 0, 0);
        var old = RotationSelectObject(dc, bitmap);
        try
        {
            Assert.True(RotationPrintWindow(window, dc, 2));
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }
        finally
        {
            RotationSelectObject(dc, old);
            RotationDeleteObject(bitmap);
            RotationDeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RotationRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct RotationBitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool RotationGetWindowRect(nint window, out RotationRect rect);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern nint RotationGetAncestor(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")] private static extern bool RotationPrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static extern nint RotationCreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection")] private static extern unsafe nint RotationCreateDIBSection(nint dc, RotationBitmapInfo* info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern nint RotationSelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool RotationDeleteObject(nint value);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] private static extern bool RotationDeleteDC(nint dc);
}
