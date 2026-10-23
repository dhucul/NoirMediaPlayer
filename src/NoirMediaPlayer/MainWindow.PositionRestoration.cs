using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private PlaybackRestorationDeadline? _positionRestoreDeadline;

    private void ClearPendingPosition()
    {
        _hasPendingPosition = false;
        _pendingResumePosition = 0;
        _pendingPositionIsExplicit = false;
        _positionRestoreDeadline = null;
    }

    private void TryApplyPendingResume(long generation, long length)
    {
        if (!_hasPendingPosition || generation != Interlocked.Read(ref _playbackGeneration) ||
            !CanReadPlayer || _playbackState is not (PlaybackState.Playing or PlaybackState.Paused)) return;
        // Stream selection and pause must settle first: changing VLC's streams
        // after a seek can reset the clock. Discovery gets one bounded interval.
        if (_windowsPlayer is null && (_pauseAfterStart || _pendingCompatibilitySubtitles is not null ||
            _pendingCompatibilityAudioOrdinal is not null)) return;
        _positionRestoreDeadline ??= new PlaybackRestorationDeadline(TimeSpan.FromSeconds(2));
        if (length <= 0 || !PlaybackIsSeekable)
        {
            // A newly opened stream already starts at zero. Live streams may
            // never expose a duration or seek range; they must still become ready.
            if (_pendingResumePosition == 0 || _positionRestoreDeadline.Remaining == TimeSpan.Zero)
            {
                var skipped = _pendingResumePosition > 0;
                ClearPendingPosition();
                if (skipped) ShowNotice("Playback resumed · This source cannot restore its previous position");
            }
            return;
        }
        var resumeAt = PlaybackResumePolicy.Resolve(_pendingResumePosition, length, _pendingPositionIsExplicit);
        ClearPendingPosition();
        if (resumeAt is { } position)
        {
            _lastPlaybackTime = position;
            _ = PlayerCommandAsync(() => RestoreCompatibilityPosition(position), player => player.Time = position);
            ShowNotice($"Resumed at {FormatTime(position)}");
        }
        else if (_currentItem is not null)
        {
            _settings.ResumePositions.Remove(_currentItem.Source);
            ScheduleSettingsSave();
        }
    }
}
