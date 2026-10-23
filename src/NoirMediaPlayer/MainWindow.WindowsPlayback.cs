using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Models;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private WindowsMediaEngine? _windowsPlayer;
    private readonly object _windowsSessionSync = new();
    private Task<bool>? _windowsFallbackTask;
    private bool _pauseAfterStart;
    private string? _windowsFailureReason;
    private bool _windowsResizeQueued;
    private bool _windowsStartupPending;
    private int? _pendingCompatibilityAudioOrdinal;
    private Task? _compatibilityAudioRestoreTask;

    private long PlaybackTime => _windowsPlayer?.Time ?? CompatibilityPlaybackTime;
    private long PlaybackLength => _windowsPlayer?.Length ?? _mediaPlayer.Length;
    private bool PlaybackIsSeekable => _windowsPlayer?.IsSeekable ?? _mediaPlayer.IsSeekable;
    private bool HasCurrentMedia => _windowsPlayer is not null || _currentMedia is not null;

    private void VideoView_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (_windowsPlayer is null || _windowsResizeQueued || _isClosing) return;
        _windowsResizeQueued = true;
        var generation = Interlocked.Read(ref _playbackGeneration);
        _ = Dispatcher.BeginInvoke(() =>
        {
            _windowsResizeQueued = false;
            if (generation != Interlocked.Read(ref _playbackGeneration) || !CanReadPlayer ||
                _windowsPlayer is not { } player || _playbackState is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            try { player.UpdateVideo(force: true); }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                _windowsFailureReason = $"0x{exception.HResult:X8}: {exception.Message}";
                _ = ObserveTaskAsync(SwitchToVlcAsync());
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private bool TryGetWindowsPlaybackWindow(WindowsPlaybackPlan? plan, out nint handle)
    {
        handle = 0;
        if (!IsLoaded || plan is null) return false;
        handle = VideoView.WindowHandle;
        return handle != 0;
    }

    private async Task StartWindowsPlaybackAsync(PlaylistItem item, nint handle, long generation, string? contentType = null,
        int rotation = 0, string? aspectRatio = null)
    {
        var volume = _settings.Volume;
        var muted = _settings.IsMuted;
        var rate = _settings.PlaybackRate;
        var softwareVideo = !_settings.HardwareDecoding;
        _windowsFallbackTask = null;
        // Only local Matroska captions need metadata-first selection. Keep the
        // established startup path for other files and network streams.
        var prepareCaptions = !item.IsNetwork && System.IO.Path.GetExtension(item.Source).Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        _windowsStartupPending = prepareCaptions;
        _windowsFailureReason = null;
        EngineStatusText.ToolTip = "Windows Media Foundation";
        await _playbackOperations.Enqueue(() =>
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
            var player = new WindowsMediaEngine(handle, mediaEvent => DispatchWindowsPlaybackEvent(generation, mediaEvent), softwareVideo: softwareVideo);
            lock (_windowsSessionSync)
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration))
                {
                    player.Dispose();
                    return;
                }
                _windowsPlayer = player;
            }
            player.SetRotation(rotation);
            player.SetAspectRatio(aspectRatio);
            player.Open(item.Source, volume, muted, rate, contentType, autoPlay: !prepareCaptions);
        }).WaitAsync(_lifetimeCancellation.Token);
        if (IsCurrentPlayback(generation, item)) StartPlaybackStartupWatchdog(item, generation);
    }

    private void DispatchWindowsPlaybackEvent(long generation, uint mediaEvent)
    {
        if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration) || _windowsPlayer is not { } player) return;
            try
            {
                switch (mediaEvent)
                {
                    case 10: // LOADEDMETADATA
                    case 1010: // TRACKSCHANGE
                        // Subscribe to the first streamed cue before the decoder
                        // starts. Late selection can miss its formatting metadata.
                        if (mediaEvent == 10 && _windowsStartupPending)
                        {
                            InitializeEmbeddedSubtitleSelection(player);
                            _windowsStartupPending = false;
                            await _playbackOperations.Enqueue(() =>
                            {
                                if (!_isClosing && generation == Interlocked.Read(ref _playbackGeneration)) player.Play();
                            });
                            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration) || !ReferenceEquals(player, _windowsPlayer)) return;
                        }
                        // Stream selection and video positioning can return E_FAIL between
                        // LOADEDMETADATA and PLAYING. Validate after decoding actually starts.
                        if (_playbackState is PlaybackState.Playing or PlaybackState.Paused &&
                            !player.HasSupportedStreams)
                        {
                            _windowsFailureReason = "The source has streams that require compatibility playback with the current decoding settings.";
                            await SwitchToVlcAsync();
                            return;
                        }
                        if (_playbackState is PlaybackState.Playing or PlaybackState.Paused) SetVideoSurfaceActive(true);
                        if (_playbackState is PlaybackState.Playing or PlaybackState.Paused) InitializeEmbeddedSubtitleSelection(player);
                        _refreshMetadata = true;
                        _metadataRetries = 12;
                        break;
                    case 1000: // FORMATCHANGE: the source dimensions or display aspect can change.
                        if (_playbackState is PlaybackState.Playing or PlaybackState.Paused)
                        {
                            // The presentation cache avoids reconfiguring an unchanged
                            // surface, including notifications from our own resize.
                            player.UpdateVideo();
                            UpdateNativeSubtitles(PlaybackTime);
                        }
                        _refreshMetadata = true;
                        break;
                    case 1009: // FIRSTFRAMEREADY
                        // With metadata-first startup, the windowless renderer waits
                        // for its initial surface before it can report PLAYING.
                        if (_playbackState == PlaybackState.Opening) player.UpdateVideo();
                        break;
                    case 13: // PLAYING
                        if (!player.HasSupportedStreams)
                        {
                            _windowsFailureReason = "The playing source has streams that require compatibility playback with the current decoding settings.";
                            await SwitchToVlcAsync();
                            return;
                        }
                        // A ratio may change while source inspection or COM startup
                        // is still pending. Apply the current UI choice before the
                        // first composed picture, and again when playback resumes.
                        player.SetAspectRatio((AspectRatioComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString());
                        InitializeEmbeddedSubtitleSelection(player);
                        OnPlaying(VLCState.Playing, generation);
                        break;
                    case 9: // PAUSE
                        OnPaused(VLCState.Paused);
                        break;
                    case 19: // ENDED
                        await HandleMediaEnded(generation);
                        break;
                    case 21: // DURATIONCHANGE
                        TryApplyPendingResume(generation, player.Length);
                        break;
                    case 5: // ERROR
                    case 1012: // RESOURCELOST
                    case 1014: // STREAMRENDERINGERROR
                        _windowsFailureReason = $"Media Foundation event {mediaEvent}, error 0x{player.ErrorCode:X8}";
                        await SwitchToVlcAsync();
                        break;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                _windowsFailureReason = $"0x{exception.HResult:X8}: {exception.Message}";
                if (generation == Interlocked.Read(ref _playbackGeneration)) await SwitchToVlcAsync();
            }
        });
    }

    private Task<bool> SwitchToVlcAsync()
    {
        if (_windowsPlayer is null) return Task.FromResult(!_isClosing && _currentMedia is not null);
        return _windowsFallbackTask ??= SwitchToVlcCoreAsync();
    }

    private async Task<bool> SwitchToVlcCoreAsync()
    {
        if (_windowsPlayer is not { } player || _currentItem is not { } item || _isClosing) return false;
        var generation = Interlocked.Read(ref _playbackGeneration);
        var hasPendingPosition = _hasPendingPosition;
        var position = _hasPendingPosition ? _pendingResumePosition : _lastPlaybackTime;
        var canCaptureAudio = _playbackState is PlaybackState.Playing or PlaybackState.Paused;
        int? audioOrdinal = null;
        // A failing native command may still occupy the queue when another fallback
        // request arrives. Capture after it completes instead of losing the selection
        // merely because CanReadPlayer was temporarily false.
        await _playbackOperations.Enqueue(() =>
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
            try { if (!hasPendingPosition) position = player.Time; }
            catch (Exception exception) { Debug.WriteLine(exception); }
            try
            {
                if (canCaptureAudio)
                {
                    var tracks = player.AudioTracks;
                    if (tracks.Count > 0) audioOrdinal = tracks.FirstOrDefault(track => track.Selected)?.Ordinal ?? -1;
                }
            }
            catch (Exception exception) { Debug.WriteLine(exception); }
        });
        if (!IsCurrentPlayback(generation, item) || !ReferenceEquals(player, _windowsPlayer)) return false;
        var paused = _playbackState == PlaybackState.Paused || _pauseAfterStart;
        Debug.WriteLine($"Retrying {item.KindText} playback with VLC compatibility backend.");
        return await PlayItemCore(item, allowResume: false, requestedPosition: position, forceVlc: true, pauseAfterStart: paused,
            requestedAudioOrdinal: audioOrdinal, requestedSubtitles: CaptureSubtitleSession());
    }

    private void ApplyVlcAudioOrdinal(int ordinal)
    {
        var tracks = _mediaPlayer.AudioTrackDescription.Where(track => track.Id >= 0).ToArray();
        if (ordinal >= tracks.Length || ordinal < -1) throw new InvalidOperationException("The audio track is unavailable in compatibility playback.");
        if (!_mediaPlayer.SetAudioTrack(ordinal < 0 ? -1 : tracks[ordinal].Id))
            throw new InvalidOperationException("The compatibility player could not select the audio track.");
    }

    private async Task<bool> RestartCompatibilityPresentationAsync(PlaylistItem item)
    {
        if (!await EnsureVlcForFeatureAsync() || !ReferenceEquals(item, _currentItem)) return false;
        var generation = Interlocked.Read(ref _playbackGeneration);
        PlaybackSessionChoices<SubtitleSession>? choices = null;
        try
        {
            await _playbackOperations.Enqueue(() =>
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
                var audioIds = _mediaPlayer.AudioTrackDescription.Where(track => track.Id >= 0).Select(track => track.Id).ToArray();
                choices = PlaybackSessionChoices<SubtitleSession>.Capture(_mediaPlayer.Time,
                    _mediaPlayer.State == VLCState.Paused, audioIds, _mediaPlayer.AudioTrack, CaptureCompatibilitySubtitleSession());
            });
            if (!IsCurrentPlayback(generation, item) || choices is null) return false;
            return await PlayItemCore(item, allowResume: false, requestedPosition: choices.Position, forceVlc: true,
                pauseAfterStart: choices.Paused, requestedAudioOrdinal: choices.AudioOrdinal, requestedSubtitles: choices.Subtitles);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            if (IsCurrentPlayback(generation, item)) ShowNotice("The current track choices could not be retained for rotation");
            return false;
        }
    }

    private void TryApplyPendingCompatibilityAudio()
    {
        if (_pendingCompatibilityAudioOrdinal is not { } ordinal || _windowsPlayer is not null || !CanReadPlayer ||
            _playbackState is not (PlaybackState.Playing or PlaybackState.Paused) ||
            _compatibilityAudioRestoreTask is not null || _pauseAfterStart ||
            _pendingCompatibilitySubtitles is not null) return;
        // Even "audio off" must wait for the input's audio inventory. Restore it
        // after captions, then apply the saved position. Changing VLC's streams
        // after a paused seek can move the input clock away from its target.
        // The restore owns the discovery deadline as well as the selection wait.
        // Never leave an undiscovered ordinal pending without a terminal outcome.
        _compatibilityAudioRestoreTask = RestoreCompatibilityAudioAsync(ordinal, Interlocked.Read(ref _playbackGeneration));
        _ = ObserveTaskAsync(_compatibilityAudioRestoreTask);
    }

    private async Task RestoreCompatibilityAudioAsync(int ordinal, long generation)
    {
        var media = _currentMedia;
        bool Current() => !_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
            _windowsPlayer is null && media is not null && ReferenceEquals(media, _currentMedia);

        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5));
        try
        {
            while (Current())
            {
                deadline.ThrowIfExpired("The compatibility player did not discover the requested audio track.");
                if (CanReadPlayer && _mediaPlayer.AudioTrackDescription.Count(track => track.Id >= 0) > Math.Max(0, ordinal)) break;
                await Task.Delay(20);
            }
            if (!Current()) return;
            var selectedId = -1;
            // Use the queue directly: ordinary fallback commands wait for restoration.
            var selection = _playbackOperations.Enqueue(() =>
            {
                if (!Current()) return;
                var tracks = _mediaPlayer.AudioTrackDescription.Where(track => track.Id >= 0).ToArray();
                if (ordinal >= tracks.Length || ordinal < -1)
                    throw new InvalidOperationException("The audio track is unavailable in compatibility playback.");
                selectedId = ordinal < 0 ? -1 : tracks[ordinal].Id;
                if (!_mediaPlayer.SetAudioTrack(selectedId))
                    throw new InvalidOperationException("The compatibility player could not restore the audio selection.");
            });
            _ = ObserveTaskAsync(selection);
            await selection.WaitAsync(deadline.Remaining);
            deadline.ThrowIfExpired("The restored audio selection did not become active.");
            while (Current() && (!CanReadPlayer || _mediaPlayer.AudioTrack != selectedId))
            {
                deadline.ThrowIfExpired("The restored audio selection did not become active.");
                await Task.Delay(20);
            }
            if (Current() && _pendingCompatibilityAudioOrdinal == ordinal)
            {
                _pendingCompatibilityAudioOrdinal = null;
                _refreshMetadata = true;
            }
        }
        catch (Exception exception)
        {
            if (Current()) FailCompatibilityRestoration(generation, "audio selection", exception);
            throw;
        }
    }

    private void FailCompatibilityRestoration(long generation, string feature, Exception exception)
    {
        if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
        Debug.WriteLine(exception);
        _ = ReleaseCurrentMedia();
        SetPlaybackState(PlaybackState.Error);
        EngineStatusText.Text = "RESTORE FAILED";
        EngineStatusDot.Fill = Brushes.OrangeRed;
        StatusText.Text = $"Compatibility playback could not restore {feature}";
        StatusDot.Fill = Brushes.OrangeRed;
        SetVideoSurfaceActive(false);
        ShowNotice($"Could not restore {feature} · {exception.Message}");
    }

    private void RestoreCompatibilityPosition(long position)
    {
        var generation = Interlocked.Read(ref _playbackGeneration);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
            SeekCompatibilityTime(position);
            if (_playbackState != PlaybackState.Paused) return;
            // SetTime initially reports the requested value before VLC's input
            // finishes seeking. Stream restoration can then overwrite that clock.
            // Wait on the playback worker and retry once if the settled clock moved.
            var clock = Stopwatch.StartNew();
            while (!_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
                _playbackState == PlaybackState.Paused && clock.Elapsed < TimeSpan.FromMilliseconds(250))
                Thread.Sleep(5);
            if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration) ||
                _playbackState != PlaybackState.Paused || Math.Abs(_mediaPlayer.Time - position) <= 500) return;
        }
    }

    // Unsupported native operations move to VLC before issuing the original command. Wait for
    // the new source to open so subtitles, snapshots, tracks and chapters target real media.
    private async Task<bool> EnsureVlcForFeatureAsync()
    {
        var item = _currentItem;
        if (item is null || _isClosing) return false;
        if (_windowsPlayer is not null && !await SwitchToVlcAsync()) return false;
        var generation = Interlocked.Read(ref _playbackGeneration);
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (!IsCurrentPlayback(generation, item)) return false;
            if (PlaybackRestorationPolicy.Failed(_compatibilityAudioRestoreTask, _compatibilitySubtitleRestoreTask)) return false;
            if (PlaybackRestorationPolicy.IsReady(_playbackState, _pauseAfterStart, _hasPendingPosition,
                _pendingCompatibilityAudioOrdinal is not null, _pendingCompatibilitySubtitles is not null || _compatibilitySubtitleLoadInProgress,
                _compatibilityAudioRestoreTask, _compatibilitySubtitleRestoreTask) && CanReadPlayer)
            {
                // Publish the restored IDs and selection before dependent controls
                // run; the metadata timer may still show the pre-restore choice.
                RefreshTrackSelectors();
                return true;
            }
            if (_playbackState is PlaybackState.Error or PlaybackState.Ended or PlaybackState.Stopped) return false;
            await Task.Delay(25);
        }
        return false;
    }

    private Task PlayerCommandAsync(Action command, Action<WindowsMediaEngine>? windowsCommand = null)
    {
        var task = RunPlayerCommandAsync(command, windowsCommand);
        _ = ObserveTaskAsync(task);
        return task;
    }

    private async Task PauseMpegCompatibilityAfterFirstPictureAsync(long generation)
    {
        // MPEG can report Playing before its first picture. Pausing at that point
        // leaves no frame for snapshot/subtitle restoration during native handoff.
        var media = _currentMedia;
        var item = _currentItem;
        var expectedAudio = item is null ? 0 : await Task.Run(() => MpegStreamInspection.TryRead(item.Source)?.AudioCount ?? 0);
        var clock = Stopwatch.StartNew();
        int? restoredPicture = null;
        while (!_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
            ReferenceEquals(media, _currentMedia) && clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            // Keep the worker available to attach the captions before pausing.
            // One more picture after selection lets VLC compose the selected cue.
            if (CanReadPlayer && _pendingCompatibilitySubtitles is null &&
                media?.Statistics is { DisplayedPictures: > 0 } statistics &&
                _mediaPlayer.AudioTrackDescription.Count(track => track.Id >= 0) >= expectedAudio)
            {
                restoredPicture ??= statistics.DisplayedPictures;
                if (statistics.DisplayedPictures > restoredPicture.Value) break;
            }
            await Task.Delay(20);
        }
        if (!_isClosing && generation == Interlocked.Read(ref _playbackGeneration))
            await PlayerCommandAsync(() => _mediaPlayer.SetPause(true), player => player.Pause());
    }

    private async Task RunPlayerCommandAsync(Action command, Action<WindowsMediaEngine>? windowsCommand)
    {
        if (_isClosing || _playbackDisposed) return;
        var player = _windowsPlayer;
        if (windowsCommand is null && (player is not null || _pendingCompatibilitySubtitles is not null || _compatibilitySubtitleLoadInProgress ||
            _pendingCompatibilityAudioOrdinal is not null))
        {
            if (!await EnsureVlcForFeatureAsync()) return;
            player = null;
        }
        var generation = Interlocked.Read(ref _playbackGeneration);
        try
        {
            await _playbackOperations.Enqueue(() =>
            {
                if (_isClosing || generation != Interlocked.Read(ref _playbackGeneration)) return;
                if (player is not null) windowsCommand!(player);
                else command();
            });
        }
        catch (Exception exception) when (player is not null && !_isClosing && generation == Interlocked.Read(ref _playbackGeneration))
        {
            Debug.WriteLine(exception);
            _windowsFailureReason = $"0x{exception.HResult:X8}: {exception.Message}";
            if (await EnsureVlcForFeatureAsync()) await PlayerCommandAsync(command);
        }
    }
}
