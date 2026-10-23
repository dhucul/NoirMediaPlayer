using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public async Task VideoSizing_PlaybackUsesNativePixelsAndDoesNotResizeOnResume(bool native) => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = native;
        var video = CreateVideo(window, "native-sizing");
        Assert.True(await Call<bool>(window, "AddItem", video, true));
        var view = Field<PlaybackView>(window, "VideoView");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(320, view.ActualWidth * dpi.DpiScaleX, 0);
        Assert.Equal(240, view.ActualHeight * dpi.DpiScaleY, 0);
        Assert.True(window.Width < 1440);
        Assert.Equal(native, Field<WindowsMediaEngine?>(window, "_windowsPlayer") is not null);
        window.Width += 50;
        var resizedWidth = window.Width;
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Paused);
        await Call(window, "TogglePlayPause");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing);
        Invoke(window, "RefreshVideoInfo");
        Assert.Equal(resizedWidth, window.Width);
        Invoke(window, "ToggleFullscreen");
        Assert.True(double.IsPositiveInfinity(view.MaxWidth));
        Invoke(window, "ToggleFullscreen");
        Assert.Equal(320 / dpi.DpiScaleX, view.MaxWidth, 3);
        window.WindowState = WindowState.Maximized;
        Assert.True(double.IsPositiveInfinity(view.MaxWidth));
        window.WindowState = WindowState.Normal;
        Assert.Equal(320 / dpi.DpiScaleX, view.MaxWidth, 3);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Math.Abs(view.MaxWidth * dpi.DpiScaleX - 240) < 1 && Math.Abs(view.MaxHeight * dpi.DpiScaleY - 320) < 1);
        Invoke(window, "ResetNowPlaying");
        Assert.True(double.IsPositiveInfinity(view.MaxWidth));
    });

    [Fact]
    public async Task VideoSizing_LargeVideoFitsWorkAreaAndRotationSwapsNativeBounds() => await WithWindow(window =>
    {
        ShowPlaybackWindow(window);
        Invoke(window, "SizeWindowToVideo", (uint)3840, (uint)2160);
        window.UpdateLayout();
        var workArea = (Rect)Invoke(window, "GetCurrentMonitorBounds", true)!;
        Assert.True(window.Width <= Math.Max(window.MinWidth, workArea.Width));
        Assert.True(window.Height <= Math.Max(window.MinHeight, workArea.Height));
        Assert.InRange(window.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - window.Width));
        typeof(MainWindow).GetField("_rotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, 90);
        Invoke(window, "SizeWindowToVideo", (uint)640, (uint)480);
        var view = Field<PlaybackView>(window, "VideoView");
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert.Equal(480 / dpi.DpiScaleX, view.MaxWidth, 3);
        Assert.Equal(640 / dpi.DpiScaleY, view.MaxHeight, 3);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task ScrollbarTheme_AppliesToInspectorAndSupportsBothDirections() => await WithWindow(window =>
    {
        ShowPlaybackWindow(window);
        window.UpdateLayout();
        static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) yield return match;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        var style = (Style)window.FindResource(typeof(ScrollBar));
        var bars = Descendants<ScrollBar>(window).ToArray();
        Assert.True(bars.Length >= 2);
        Assert.All(bars, bar => Assert.Same(style, bar.Style));
        foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            var bar = new ScrollBar { Style = style, Orientation = orientation, Maximum = 100, Value = 25, ViewportSize = 20 };
            bar.ApplyTemplate();
            bar.Measure(new Size(orientation == Orientation.Vertical ? 10 : 200, orientation == Orientation.Vertical ? 200 : 10));
            bar.Arrange(new Rect(bar.DesiredSize));
            var track = (Track)bar.Template.FindName("PART_Track", bar);
            Assert.Equal(25, track.Value);
            Assert.Equal(100, track.Maximum);
            Assert.Equal(20, track.ViewportSize);
            Assert.Equal(orientation == Orientation.Vertical, track.IsDirectionReversed);
            Assert.Equal(orientation == Orientation.Vertical ? ScrollBar.PageDownCommand : ScrollBar.PageRightCommand, track.IncreaseRepeatButton.Command);
            Assert.Equal("#FF0C0E12", bar.Background.ToString());
        }
        return Task.CompletedTask;
    });
}
