using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NoirMediaPlayer.Controls;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.Tests;

public sealed partial class LogicAuditRegressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VideoSizing_SizeChoicesScaleBothDimensionsWhilePaused(bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        Assert.True(await Call<bool>(window, "AddItem", CreateVideo(window, "size-choices"), true));
        var view = Field<PlaybackView>(window, "VideoView");
        var selector = Field<ComboBox>(window, "VideoSizeComboBox");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        Assert.Equal(native, Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        var dpi = VisualTreeHelper.GetDpi(window);
        foreach (var (index, scale) in new[] { (0, 0.5), (2, 2.0), (1, 1.0) })
        {
            selector.SelectedIndex = index;
            window.UpdateLayout();
            Assert.Equal(320 * scale / dpi.DpiScaleX, view.MaxWidth, 3);
            Assert.Equal(240 * scale / dpi.DpiScaleY, view.MaxHeight, 3);
            Assert.Equal(4d / 3, view.MaxWidth / view.MaxHeight, 3);
            Assert.True(view.ActualWidth <= view.MaxWidth + 1);
            Assert.True(view.ActualHeight <= view.MaxHeight + 1);
            Assert.Equal(PlaybackState.Paused, Field<PlaybackState>(window, "_playbackState"));
        }
        // The restored original size is based on the source, not the previous scale.
        Assert.Equal(320, view.ActualWidth * dpi.DpiScaleX, 0);
        Assert.Equal(240, view.ActualHeight * dpi.DpiScaleY, 0);
        selector.SelectedIndex = 0;
        Invoke(window, "ToggleFullscreen");
        Assert.True(double.IsPositiveInfinity(view.MaxWidth));
        Invoke(window, "ToggleFullscreen");
        Assert.Equal(160 / dpi.DpiScaleX, view.MaxWidth, 3);
        window.WindowState = WindowState.Maximized;
        selector.SelectedIndex = 2;
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal(640 / dpi.DpiScaleX, view.MaxWidth, 3);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Math.Abs(view.MaxWidth * dpi.DpiScaleX - 480) < 1 && Math.Abs(view.MaxHeight * dpi.DpiScaleY - 640) < 1);
        Assert.Equal(3d / 4, view.MaxWidth / view.MaxHeight, 3);
    });

    [Fact]
    public async Task VideoSizing_SizeChoiceBeforePlaybackIsAppliedAndOversizeStaysOnScreen() => await WithWindow(window =>
    {
        ShowPlaybackWindow(window);
        var selector = Field<ComboBox>(window, "VideoSizeComboBox");
        var view = Field<PlaybackView>(window, "VideoView");
        var initialWidth = window.Width;
        selector.SelectedIndex = 2;
        Assert.Equal(initialWidth, window.Width);
        Assert.True(double.IsPositiveInfinity(view.MaxWidth));
        Invoke(window, "SizeWindowToVideo", (uint)3840, (uint)2160);
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(7680 / dpi.DpiScaleX, view.MaxWidth, 3);
        Assert.Equal(4320 / dpi.DpiScaleY, view.MaxHeight, 3);
        var workArea = (Rect)Invoke(window, "GetCurrentMonitorBounds", true)!;
        Assert.True(window.Width <= Math.Max(window.MinWidth, workArea.Width));
        Assert.True(window.Height <= Math.Max(window.MinHeight, workArea.Height));
        Assert.InRange(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width));
        Assert.InRange(window.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - window.Height));
        return Task.CompletedTask;
    });
}
