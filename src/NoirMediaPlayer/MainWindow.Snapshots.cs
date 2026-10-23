using System.Diagnostics;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    // Runs on the playback worker. Only Windows-validated ASF physical timelines
    // and an unmodified, single video stream can use independent source capture.
    private bool TrySaveWindowsAsfSnapshot(string source, string path, bool originalPresentation)
    {
        if (!originalPresentation || _compatibilityChapters is null ||
            _playbackState is not (PlaybackState.Playing or PlaybackState.Paused) ||
            _pendingCompatibilitySubtitles is not null || _pendingCompatibilityAudioOrdinal is not null ||
            _hasPendingPosition || _pauseAfterStart) return false;
        try
        {
            if (!_mediaPlayer.IsSeekable || _mediaPlayer.Length <= 0 || _mediaPlayer.VideoTrack < 0 ||
                _mediaPlayer.VideoTrackDescription.Count(track => track.Id >= 0) != 1) return false;
            PlainSubtitleTrack? subtitles = null;
            var selected = _mediaPlayer.Spu;
            // Automatic/embedded/complex captions remain with their renderer.
            // Only uniquely identified plain external tracks have a native map.
            if (selected >= 0 && (!_compatibilityPlainSubtitles.TryGetValue(selected, out subtitles) || !subtitles.HasUnchangedSource)) return false;
            var time = CompatibilityPlaybackTime;
            if (time < 0 || time > _mediaPlayer.Length) return false;
            WindowsMediaEngine.SaveSourceSnapshot(source, path, time, "video/x-ms-asf", subtitles);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            return false; // Keep the existing capture path if Windows cannot decode.
        }
    }
}
