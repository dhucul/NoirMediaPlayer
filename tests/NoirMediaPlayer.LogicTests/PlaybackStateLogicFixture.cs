using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer
{
    // The actual production startup, resume and snapshot partials run against
    // these managed collaborators. No player, window or image API is loaded.
    public partial class MainWindow
    {
        private readonly object _windowsSessionSync = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly LogicSettings _settings = new();
        private readonly LogicLabel EngineStatusText = new();
        private Task<bool>? _windowsFallbackTask;
        private bool _windowsStartupPending, _pauseAfterStart;
        private string? _windowsFailureReason;
        private int _rotation;
        private long _pendingResumePosition, _lastPlaybackTime;
        private bool _hasPendingPosition, _pendingPositionIsExplicit;
        private int? _pendingCompatibilityAudioOrdinal;
        private PlaybackState _playbackState = PlaybackState.Playing;
        private bool PlaybackIsSeekable { get; set; }
        private long CompatibilityPlaybackTime => _lastPlaybackTime;
        private object? _compatibilityChapters = new();
        internal List<long> RestoredPositions { get; } = [];
        internal string? Notice { get; private set; }
        internal int Fallbacks { get; private set; }
        internal WindowsMediaEngine? NativeForLogic => _windowsPlayer;
        internal bool PositionPendingForLogic => _hasPendingPosition;
        internal bool SnapshotBusyForLogic => _snapshotWorker.IsBusy;
        internal bool StartupFallbackClearedForLogic => _windowsFallbackTask is null;

        internal MainWindow(SnapshotWorker? worker = null)
        {
            if (worker is not null) _snapshotWorker = worker;
        }
        private void DispatchWindowsPlaybackEvent(long generation, uint mediaEvent) { }
        private void StartPlaybackStartupWatchdog(PlaylistItem item, long generation) { }
        private void ScheduleSettingsSave() { }
        private void ShowNotice(string text) => Notice = text;
        private string FormatTime(long time) => time.ToString();
        private void RestoreCompatibilityPosition(long time) => RestoredPositions.Add(time);

        internal void PendingPositionForLogic(long position, TimeProvider clock, bool explicitPosition = true)
        {
            _pendingResumePosition = position;
            _hasPendingPosition = true;
            _pendingPositionIsExplicit = explicitPosition;
            _positionRestoreDeadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(2), clock);
        }
        internal void TickPositionForLogic(long length, bool seekable, long generation = 0)
        {
            PlaybackIsSeekable = seekable;
            TryApplyPendingResume(generation, length);
        }
        internal void SetPendingAudioForLogic(bool pending) => _pendingCompatibilityAudioOrdinal = pending ? 0 : null;
        internal void SetPendingPauseForLogic(bool pending) => _pauseAfterStart = pending;
        internal void ClearPositionForLogic() => ClearPendingPosition();
        internal bool PositionDeadlineClearedForLogic => _positionRestoreDeadline is null;
        internal void SetRotationForLogic(int rotation) => _rotation = rotation;
        internal Task StartNativeForLogic()
        {
            _currentMedia = null;
            return StartWindowsPlaybackAsync(_currentItem, 1, _playbackGeneration);
        }
        internal Task<bool> SnapshotForLogic(string path) => CaptureSnapshotAsync(path, originalPresentation: true);
        internal Task StopSnapshotSessionForLogic(bool closing = false)
        {
            _playbackGeneration++;
            _snapshotCancellation?.Cancel();
            if (closing) { _isClosing = true; _lifetimeCancellation.Cancel(); }
            var player = _windowsPlayer;
            _windowsPlayer = null;
            _currentMedia = null;
            return _playbackOperations.Enqueue(() => player?.Dispose());
        }
    }

    internal sealed class LogicSettings
    {
        internal int Volume = 80;
        internal bool IsMuted = false;
        internal float PlaybackRate = 1;
        internal bool HardwareDecoding = false;
        internal Dictionary<string, long> ResumePositions = [];
    }
    internal sealed class LogicLabel { internal object? ToolTip; }
}

namespace NoirMediaPlayer.Services
{
    internal sealed class WindowsMediaEngine : IDisposable
    {
        internal int Rotation { get; private set; }
        internal int RotationAtOpen { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool UnsupportedSnapshot;
        internal string Source = "logic-only.mp4";
        internal long Time { get; set; }
        internal WindowsMediaEngine(nint window = 0, Action<uint>? notify = null, bool softwareVideo = true) { }
        internal void SetRotation(int rotation) => Rotation = rotation;
        internal void SetAspectRatio(string? value) { }
        internal void Open(string source, int volume, bool muted, float rate, string? contentType, bool autoPlay)
        {
            Source = source;
            RotationAtOpen = Rotation;
        }
        internal void SelectCaptionTrack(int id) { }
        internal SourceSnapshotRequest CreateSnapshotRequest(PlainSubtitleTrack? subtitles)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (UnsupportedSnapshot) throw new NotSupportedException("fake unsupported source");
            return new SourceSnapshotRequest(Source, Time, Subtitles: subtitles, Rotation: Rotation);
        }
        public void Dispose() => Disposed = true;
    }
}
