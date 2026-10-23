using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private bool _hasSizedVideo;
    private Size? _nativeVideoSize;
    private double _videoSizeMultiplier = 1;

    private void ResetVideoSizing(bool preserveWindowSize = false)
    {
        // A backend handoff or presentation restart continues the same video.
        // Retain its sizing decision, including when sizing was skipped for a view mode.
        if (!preserveWindowSize) _hasSizedVideo = false;
        _nativeVideoSize = null;
        UpdateVideoSizeLimit();
    }

    private void UpdateVideoSizeLimit()
    {
        if (VideoView is null) return;
        var size = !_isFullscreen && WindowState == WindowState.Normal ? _nativeVideoSize : null;
        VideoView.MaxWidth = size is { } widthSize ? widthSize.Width * _videoSizeMultiplier : double.PositiveInfinity;
        VideoView.MaxHeight = size is { } heightSize ? heightSize.Height * _videoSizeMultiplier : double.PositiveInfinity;
    }

    private void VideoSize_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || VideoSizeComboBox.SelectedItem is not ComboBoxItem item) return;
        _videoSizeMultiplier = item.Tag?.ToString() switch { "0.5" => 0.5, "2" => 2, _ => 1 };
        if (_nativeVideoSize is not null)
        {
            // An explicit size choice returns to a window where that size can apply.
            // No playback command is needed, so paused videos remain paused.
            if (_isFullscreen) ToggleFullscreen();
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            UpdateVideoSizeLimit();
            ResizeWindowToVideo();
        }
        ShowNotice($"Video size · {item.Content}");
    }

    private void SizeWindowToVideo(uint width, uint height)
    {
        if (!IsLoaded || width == 0 || height == 0) return;
        double pictureWidth = width, pictureHeight = height;
        if (_windowsPlayer is { } player)
        {
            // Display aspect includes non-square pixels and source orientation.
            var aspect = player.DisplayAspectRatio;
            if (aspect > 0) pictureWidth = pictureHeight * aspect;
        }
        else if (_currentMedia is { } media)
        {
            var track = media.Tracks.FirstOrDefault(track => track.TrackType == TrackType.Video && track.Id == _mediaPlayer.VideoTrack);
            var video = track.Data.Video;
            if (video.Width > 0 && video.Height > 0)
            {
                // VLC's output size can already contain a user rotation. Start from
                // source metadata so orientation and pixel aspect are applied once.
                pictureWidth = video.Width;
                pictureHeight = video.Height;
                if (video.SarNum > 0 && video.SarDen > 0)
                    pictureWidth *= (double)video.SarNum / video.SarDen;
                if (video.Orientation is VideoOrientation.LeftTop or VideoOrientation.LeftBottom or
                    VideoOrientation.RightTop or VideoOrientation.RightBottom)
                    (pictureWidth, pictureHeight) = (pictureHeight, pictureWidth);
            }
        }
        if (_rotation is 90 or 270) (pictureWidth, pictureHeight) = (pictureHeight, pictureWidth);
        var ratio = (AspectRatioComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var forcedAspect = ratio switch
        {
            "16:9" => 16d / 9, "16:10" => 1.6, "4:3" => 4d / 3,
            "1:1" => 1, "21:9" => 21d / 9, _ => 0
        };
        if (forcedAspect > 0) pictureWidth = pictureHeight * forcedAspect;
        var dpi = VisualTreeHelper.GetDpi(this);
        _nativeVideoSize = new Size(pictureWidth / dpi.DpiScaleX, pictureHeight / dpi.DpiScaleY);
        UpdateVideoSizeLimit();

        if (_hasSizedVideo) return;
        // Only a new media selection or an explicit size choice resizes the window.
        _hasSizedVideo = true;
        ResizeWindowToVideo();
    }

    private void ResizeWindowToVideo()
    {
        if (_nativeVideoSize is not { } native || _isCompact || _isFullscreen || WindowState != WindowState.Normal) return;
        UpdateLayout();
        var workArea = GetCurrentMonitorBounds(workArea: true);
        // Use the frame's available space, not the possibly capped PlaybackView.
        var chromeWidth = ActualWidth - Math.Max(0, VideoFrame.ActualWidth - 2);
        var chromeHeight = ActualHeight - Math.Max(0, VideoFrame.ActualHeight - 2);
        var requested = new Size(native.Width * _videoSizeMultiplier, native.Height * _videoSizeMultiplier);
        var scale = Math.Min(1, Math.Min(
            Math.Max(1, workArea.Width - chromeWidth) / requested.Width,
            Math.Max(1, workArea.Height - chromeHeight) / requested.Height));
        Width = Math.Max(MinWidth, Math.Min(workArea.Width, requested.Width * scale + chromeWidth));
        Height = Math.Max(MinHeight, Math.Min(workArea.Height, requested.Height * scale + chromeHeight));
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }
}