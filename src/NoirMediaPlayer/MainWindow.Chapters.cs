using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private NativeChapterMap? _nativeChapters;
    private bool _nativeUsesAsfMarkers;
    private NativeChapterMap? _compatibilityChapters;
    private long _compatibilityChapterTime = -1;

    private long CompatibilityPlaybackTime
    {
        get
        {
            var hint = Interlocked.Read(ref _compatibilityChapterTime);
            return _compatibilityChapters is not null && _playbackState == PlaybackState.Paused && hint >= 0
                ? hint : _mediaPlayer.Time;
        }
    }

    private Task NavigateChapterAsync(bool next)
    {
        var chapters = _nativeChapters;
        long? requestedTarget = null;
        return PlayerCommandAsync(
            () =>
            {
                // A timed-out native seek may already have moved the clock. Retry
                // its absolute target rather than advancing the chapter twice.
                if (requestedTarget is { } target) SeekCompatibilityChapter(target);
                else if (_compatibilityChapters is { } map)
                {
                    // VLC's ASF demuxer exposes no chapter inventory. Keep using
                    // Windows' marker metadata if another feature needs VLC decoding.
                    if (map.Navigate(CompatibilityPlaybackTime, next) is { } destination && destination < _mediaPlayer.Length)
                        SeekCompatibilityChapter(destination);
                }
                else if (next) _mediaPlayer.NextChapter();
                else _mediaPlayer.PreviousChapter();
            },
            chapters is null ? null : player =>
            {
                if (chapters.Starts.Count == 0) return;
                if (!player.IsSeekable) throw new NotSupportedException("Native chapter seeking is unavailable.");
                if (chapters.Navigate(player.ChapterTime, next) is { } target)
                {
                    // The source length can change while it opens. Do not turn an
                    // invalid marker into an accidental jump to the end of the file.
                    if (target >= player.Length) throw new NotSupportedException("The chapter is outside the native timeline.");
                    requestedTarget = target;
                    player.SeekChapter(target);
                }
            });
    }

    private void SeekCompatibilityChapter(long target)
    {
        SeekCompatibilityTime(target);
        // VLC can retain its old clock while a video is paused. Retain the last
        // seek's position until another seek, resume, or source change.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_isClosing && _playbackState == PlaybackState.Playing && clock.Elapsed < TimeSpan.FromSeconds(2) &&
            Math.Abs(_mediaPlayer.Time - target) > 500)
            Thread.Sleep(5);
    }

    private void SeekCompatibilityTime(long target)
    {
        _mediaPlayer.Time = target;
        Interlocked.Exchange(ref _compatibilityChapterTime,
            _playbackState == PlaybackState.Paused || _pauseAfterStart ? target : -1);
    }
}
