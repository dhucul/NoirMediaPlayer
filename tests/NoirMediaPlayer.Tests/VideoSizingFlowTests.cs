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
    [Fact]
    public async Task VideoSizing_NewVideoPreservesMiniPlayerBounds() => await WithWindow(window =>
    {
        ShowPlaybackWindow(window);
        Invoke(window, "ToggleCompact");
        window.UpdateLayout();
        var before = new Size(window.Width, window.Height);
        Invoke(window, "SizeWindowToVideo", (uint)1920, (uint)1080);
        window.UpdateLayout();
        Assert.True(Field<bool>(window, "_isCompact"));
        Assert.Equal(before, new Size(window.Width, window.Height));
        return Task.CompletedTask;
    });

    [Fact]
    public async Task VideoSizing_NativeRotationKeepsRequestedPictureSize() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = true;
        Assert.True(await Call<bool>(window, "AddItem", CreateVideo(window, "flow-rotation"), true));
        var view = Field<PlaybackView>(window, "VideoView");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        Field<ComboBox>(window, "VideoSizeComboBox").SelectedIndex = 2;
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        Invoke(window, "Rotate_Click", window, new RoutedEventArgs());
        await Until(() => Math.Abs(view.MaxHeight * dpi.DpiScaleY - 640) < 1);
        window.UpdateLayout();
        var workArea = (Rect)Invoke(window, "GetCurrentMonitorBounds", true)!;
        var frame = Field<Border>(window, "VideoFrame");
        var chromeHeight = window.ActualHeight - frame.ActualHeight + 2;
        var expectedHeight = Math.Min(view.MaxHeight, workArea.Height - chromeHeight);
        Assert.True(view.ActualHeight >= expectedHeight - 1,
            $"Double-size rotated picture needs {expectedHeight:F1} DIPs; available video height stayed {view.ActualHeight:F1}.");
    });

    [Fact]
    public async Task VideoSizing_BackendHandoffPreservesUserWindowSize() => await WithWindow(async window =>
    {
        ShowPlaybackWindow(window);
        Field<PlayerSettings>(window, "_settings").PreferWindowsPlayback = true;
        Assert.True(await Call<bool>(window, "AddItem", CreateVideo(window, "flow-handoff"), true));
        var view = Field<PlaybackView>(window, "VideoView");
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        Assert.NotNull(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        var originalSize = new Size(window.Width, window.Height);
        window.Width += 100;
        window.Height += 80;
        window.UpdateLayout();
        var before = new Size(window.Width, window.Height);
        Assert.True(await Call<bool>(window, "SwitchToVlcAsync"));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        Assert.Null(Field<WindowsMediaEngine?>(window, "_windowsPlayer"));
        window.UpdateLayout();
        Assert.Equal(before, new Size(window.Width, window.Height));

        // A genuinely new selection must still size itself after the preserved handoff.
        Assert.True(await Call<bool>(window, "AddItem", CreateVideo(window, "flow-next-video"), true));
        await Until(() => Field<PlaybackState>(window, "_playbackState") == PlaybackState.Playing && !double.IsInfinity(view.MaxWidth));
        window.UpdateLayout();
        Assert.Equal(originalSize, new Size(window.Width, window.Height));
    });
}
