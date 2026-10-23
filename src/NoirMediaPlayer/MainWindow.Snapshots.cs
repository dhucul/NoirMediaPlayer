using System.Diagnostics;
using System.IO;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private readonly SnapshotWorker _snapshotWorker = new();
    private CancellationTokenSource? _snapshotCancellation;
    private bool _snapshotRequestPending;

    private async Task<bool> CaptureSnapshotAsync(string path, bool originalPresentation)
    {
        if (_snapshotRequestPending || _snapshotWorker.IsBusy)
        {
            ShowNotice("A snapshot is still finishing · Try again after it completes");
            return false;
        }
        var item = _currentItem;
        if (item is null || _isClosing) return false;
        var generation = Interlocked.Read(ref _playbackGeneration);
        var player = _windowsPlayer;
        var subtitles = _selectedPlainSubtitle;
        _snapshotRequestPending = true;
        try
        {
            SourceSnapshotRequest? request = null;
            try
            {
                // Capture only managed session values on the transport queue.
                // The slow Source Reader never retains the live engine or its lock.
                await _playbackOperations.Enqueue(() =>
                {
                    if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
                    request = player is not null ? player.CreateSnapshotRequest(subtitles)
                        : TryCreateWindowsAsfSnapshot(item.Source, originalPresentation);
                });
                if (!IsCurrentPlayback(generation, item)) return false;
                if (request is not null)
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
                    _snapshotCancellation = cancellation;
                    try
                    {
                        return await CaptureSourceAsync(_snapshotWorker, request, path, cancellation.Token);
                    }
                    finally
                    {
                        if (ReferenceEquals(_snapshotCancellation, cancellation)) _snapshotCancellation = null;
                    }
                }
            }
            catch (OperationCanceledException) when (_isClosing || !IsCurrentPlayback(generation, item)) { return false; }
            catch (TimeoutException)
            {
                // The child is terminated independently of live playback. Timeout
                // remains a snapshot failure even once its slot is available again.
                if (IsCurrentPlayback(generation, item)) ShowNotice("Snapshot timed out · Playback remains available");
                return false;
            }
            catch (Exception exception)
            {
                if (!IsCurrentPlayback(generation, item)) return false;
                Debug.WriteLine(exception);
                if (player is not null) _windowsFailureReason = $"0x{exception.HResult:X8}: {exception.Message}";
            }

            // Completed failures and unsupported capture requests retain the
            // established VLC handoff. A changed source never receives this retry.
            if (!IsCurrentPlayback(generation, item) || !await EnsureVlcForFeatureAsync() ||
                !ReferenceEquals(item, _currentItem)) return false;
            generation = Interlocked.Read(ref _playbackGeneration);
            var success = false;
            await _playbackOperations.Enqueue(() =>
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                success = _mediaPlayer.TakeSnapshot(0, path, 0, 0);
            });
            if (!success && IsCurrentPlayback(generation, item)) ShowNotice("Snapshot is not available for this source");
            return success && IsCurrentPlayback(generation, item);
        }
        finally { _snapshotRequestPending = false; }
    }

    // Send only captured managed state to the decoder process.
    private static Task<bool> CaptureSourceAsync(SnapshotWorker worker, SourceSnapshotRequest request,
        string path, CancellationToken cancellationToken) =>
        worker.TryCaptureAsync(request, path, cancellationToken);

    // Runs on the playback worker, but performs no decoding. Only validated ASF
    // timelines and an unmodified single video stream use independent capture.
    private SourceSnapshotRequest? TryCreateWindowsAsfSnapshot(string source, bool originalPresentation)
    {
        if (!originalPresentation || _compatibilityChapters is null ||
            _playbackState is not (PlaybackState.Playing or PlaybackState.Paused) ||
            _pendingCompatibilitySubtitles is not null || _pendingCompatibilityAudioOrdinal is not null ||
            _hasPendingPosition || _pauseAfterStart) return null;
        try
        {
            if (!_mediaPlayer.IsSeekable || _mediaPlayer.Length <= 0 || _mediaPlayer.VideoTrack < 0 ||
                _mediaPlayer.VideoTrackDescription.Count(track => track.Id >= 0) != 1) return null;
            PlainSubtitleTrack? subtitles = null;
            var selected = _mediaPlayer.Spu;
            // Automatic/embedded/complex captions remain with their renderer.
            // Only uniquely identified plain external tracks have a native map.
            if (selected >= 0 && (!_compatibilityPlainSubtitles.TryGetValue(selected, out subtitles) || !subtitles.HasUnchangedSource)) return null;
            var time = CompatibilityPlaybackTime;
            if (time < 0 || time > _mediaPlayer.Length) return null;
            return new SourceSnapshotRequest(source, time, "video/x-ms-asf", subtitles);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            return null;
        }
    }
}
