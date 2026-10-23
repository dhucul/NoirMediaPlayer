using NoirMediaPlayer.Services;

// The linked production partial contains the real subtitle orchestration and
// inventory. Only its native/UI collaborators below are replaced with BCL fakes.
namespace NoirMediaPlayer
{
    public partial class MainWindow
    {
        private sealed class LogicItem { internal string Source => "logic-only.mp4"; }
        private readonly LogicItem _currentItem = new();
        private object? _currentMedia = new();
        private long _playbackGeneration;
        private bool _isClosing = false;
        private LogicNativePlayer? _windowsPlayer = null;
        private readonly LogicMediaPlayer _mediaPlayer = new();
        private readonly LogicOperationQueue _playbackOperations = new();
        private readonly LogicVideoView VideoView = new();
        private bool _refreshMetadata;
        private long PlaybackTime => 0;
        private bool CanReadPlayer => !_isClosing && _playbackOperations.IsIdle;
        private bool IsCurrentPlayback(long generation, object item) =>
            !_isClosing && generation == _playbackGeneration && ReferenceEquals(item, _currentItem);
        private Task<bool> EnsureVlcForFeatureAsync() => Task.FromResult(_currentMedia is not null);
        private Task PlayerCommandAsync(Action command, Action<LogicNativePlayer> nativeCommand)
        {
            if (_windowsPlayer is { } player) nativeCommand(player); else command();
            return Task.CompletedTask;
        }
        private void RefreshSubtitleClock() { }
        private void RefreshTrackSelectors() { }
        private void UpdateNativeSubtitles(long time) { }
        private Task ObserveTaskAsync(Task task)
        {
            ObservedOperations.Add(task);
            var observer = ObserveFaultAsync(task);
            ObserverTasks.Add(observer);
            return observer;
        }
        private static async Task ObserveFaultAsync(Task task) { try { await task; } catch { } }
        private void FailCompatibilityRestoration(long generation, string feature, Exception exception)
        {
            if (_isClosing || generation != _playbackGeneration) return;
            Failure = exception;
            FailureFeature = feature;
            RetireLogicSession();
        }
        private void RetireLogicSession()
        {
            _playbackGeneration++;
            _currentMedia = null;
            _plainSubtitles.Clear();
            _compatibilityPlainSubtitles.Clear();
            _compatibilityExternalSubtitlePaths.Clear();
            _compatibilitySubtitleLoadInProgress = false;
            _pendingCompatibilitySubtitles = null;
            // Model ReleaseCurrentMedia's serialized teardown, never disposal
            // from a timed-out waiter. A blocked queue still owns all actions.
            _ = ObserveTaskAsync(_playbackOperations.Enqueue(() => TeardownCompleted = true));
        }

        internal LogicMediaPlayer Backend => _mediaPlayer;
        internal LogicOperationQueue Queue => _playbackOperations;
        internal Exception? Failure { get; private set; }
        internal string? FailureFeature { get; private set; }
        internal bool TeardownCompleted { get; private set; }
        internal List<Task> ObservedOperations { get; } = [];
        internal List<Task> ObserverTasks { get; } = [];
        internal bool SubtitleLoadPending => _compatibilitySubtitleLoadInProgress;
        internal bool SubtitleRestorePending => _pendingCompatibilitySubtitles is not null;
        internal bool MetadataRefreshRequested => _refreshMetadata;
        internal (PlainSubtitleTrack?, int?) NativeChoices => (_selectedPlainSubtitle, _selectedEmbeddedSubtitleOrdinal);
        internal int VideoGeometryRefreshes => VideoView.RefreshRequests;
        internal void ApplyAspectForLogic(string? ratio) => ApplyCompatibilityAspectRatio(ratio);
        internal void PrepareAspectForLogic(string? ratio, bool dvd) => PrepareCompatibilityVideoPresentation(ratio, dvd);
        internal Task NotifyVideoOutputForLogicAsync(bool available, string? ratio, long? generation = null) =>
            RefreshCompatibilityVideoPresentationAsync(generation ?? _playbackGeneration, ratio, available);
        internal void UseNativeBackendForLogic() => _windowsPlayer = new LogicNativePlayer();
        internal Task<bool> LoadSubtitleForLogicAsync(string path) => LoadExternalSubtitleAsync(path);
        internal (string? Path, int? Ordinal, string[] Files) CaptureSubtitleForLogic()
        {
            var session = CaptureCompatibilitySubtitleSession();
            return (session.SelectedPath, session.SelectedEmbeddedOrdinal,
                session.Tracks.Select(track => track.Path).Concat(session.CompatibilityPaths ?? []).ToArray());
        }
        internal Task RestoreSubtitlesForLogicAsync(params string[] paths)
        {
            var tracks = paths.Select(PlainSubtitleTrack.TryLoad).OfType<PlainSubtitleTrack>().ToArray();
            var extras = paths.Except(tracks.Select(track => track.Path), StringComparer.OrdinalIgnoreCase).ToArray();
            var session = new SubtitleSession(tracks, paths.FirstOrDefault(), -1, extras);
            return RestoreForLogicAsync(session);
        }
        internal Task RestoreCapturedSubtitlesForLogicAsync(MainWindow source) =>
            RestoreForLogicAsync(source.CaptureCompatibilitySubtitleSession());
        private Task RestoreForLogicAsync(SubtitleSession session)
        {
            _pendingCompatibilitySubtitles = session;
            return RestoreCompatibilitySubtitlesAsync(session, _playbackGeneration);
        }
        internal void SupersedeForLogic(bool pendingLoad = false, bool pendingRestore = false)
        {
            RetireLogicSession();
            Failure = null;
            _currentMedia = new();
            _compatibilitySubtitleLoadInProgress = pendingLoad;
            if (pendingRestore) _pendingCompatibilitySubtitles = new SubtitleSession([], null, -1);
        }
        internal Task QueueSubtitleCommandForLogicAsync(PlaybackRestorationDeadline deadline, Action command)
        {
            var generation = _playbackGeneration;
            return QueueCompatibilitySubtitleCommandAsync(deadline,
                () => IsCurrentPlayback(generation, _currentItem), command);
        }
        private sealed class LogicNativePlayer { internal void SelectCaptionTrack(int id) { } }
    }

    internal sealed record LogicSubtitleId(int Id);
    internal sealed class LogicMediaPlayer
    {
        private string? _aspectRatio;
        internal float Scale { get; set; } = 1;
        internal string? AspectRatio
        {
            get => _aspectRatio;
            set { _aspectRatio = value; PresentationApplyCount++; }
        }
        internal int PresentationApplyCount { get; private set; }
        internal LogicSubtitleId[] SpuDescription = [];
        internal int Spu = -1;
        internal Action? OnAdd;
        internal bool AcceptAttachment = true;
        internal bool AcceptSelection = true;
        internal int AddCalls;
        internal int SelectionCalls;
        internal bool? AttachmentRequestedSelection;
        internal bool AddSlave(LibVLCSharp.Shared.MediaSlaveType type, string uri, bool select)
        {
            AddCalls++;
            AttachmentRequestedSelection = select;
            OnAdd?.Invoke();
            return AcceptAttachment;
        }
        internal bool SetSpu(int id)
        {
            SelectionCalls++;
            if (!AcceptSelection) return false;
            Spu = id;
            return true;
        }
    }

    internal sealed class LogicVideoView
    {
        // State counter only: this fake creates no window and sends no messages.
        internal int RefreshRequests { get; private set; }
        internal void RefreshVideoGeometry() => RefreshRequests++;
    }

    internal sealed class LogicOperationQueue
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _waiting = new();
        internal bool BlockNext;
        internal bool IsIdle => _waiting.Count == 0;
        internal int WaitingCount => _waiting.Count;
        internal int EnqueuedCount { get; private set; }
        internal Task? FirstBlocked => _waiting.TryPeek(out var entry) ? entry.Completion.Task : null;
        internal Task Enqueue(Action action)
        {
            EnqueuedCount++;
            if (BlockNext || _waiting.Count > 0)
            {
                BlockNext = false;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiting.Enqueue((action, completion));
                return completion.Task;
            }
            try { action(); return Task.CompletedTask; }
            catch (Exception exception) { return Task.FromException(exception); }
        }
        internal void CompleteQueued(Exception? firstFailure = null)
        {
            while (_waiting.TryDequeue(out var entry))
            {
                try
                {
                    entry.Action();
                    if (firstFailure is { } failure) { firstFailure = null; throw failure; }
                    entry.Completion.SetResult();
                }
                catch (Exception exception) { entry.Completion.SetException(exception); }
            }
        }
    }
}

namespace LibVLCSharp.Shared
{
    // Compile-time shape only. There is no LibVLCSharp assembly or native library.
    internal enum MediaSlaveType { Subtitle }
}

namespace NoirMediaPlayer.Services
{
    internal static class MpegStreamInspection
    {
        internal static bool SupportsExtension(string source) => false;
    }
}
