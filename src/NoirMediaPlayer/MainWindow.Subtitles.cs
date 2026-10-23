using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private sealed record SubtitleSession(PlainSubtitleTrack[] Tracks, string? SelectedPath, int? SelectedEmbeddedOrdinal = null);
    private readonly List<PlainSubtitleTrack> _plainSubtitles = [];
    private readonly ConcurrentDictionary<int, PlainSubtitleTrack> _compatibilityPlainSubtitles = new();
    private PlainSubtitleTrack? _selectedPlainSubtitle;
    private SubtitleSession? _pendingCompatibilitySubtitles;
    private Task? _compatibilitySubtitleRestoreTask;
    private DispatcherTimer? _subtitleTimer;
    private int? _selectedEmbeddedSubtitleOrdinal;

    private SubtitleSession? CaptureSubtitleSession() => _plainSubtitles.Count == 0 && _selectedEmbeddedSubtitleOrdinal is null ? null :
        new SubtitleSession(_plainSubtitles.ToArray(), _selectedPlainSubtitle?.Path, _selectedEmbeddedSubtitleOrdinal);

    private void RestoreSubtitleSession(SubtitleSession? session, bool compatibility)
    {
        if (session is null) return;
        _plainSubtitles.AddRange(session.Tracks);
        _selectedEmbeddedSubtitleOrdinal = session.SelectedEmbeddedOrdinal;
        _selectedPlainSubtitle = session.Tracks.FirstOrDefault(track => string.Equals(track.Path, session.SelectedPath, StringComparison.OrdinalIgnoreCase));
        if (compatibility) _pendingCompatibilitySubtitles = session;
        else
        {
            RefreshSubtitleClock();
            // Discovery can finish after the first stream-metadata refresh. Publish
            // the managed caption inventory immediately, without querying a busy
            // playback worker, so visible captions can always be selected again.
            var wasRefreshing = _isRefreshingTracks;
            _isRefreshingTracks = true;
            try { RefreshNativeSubtitleSelector(); }
            finally { _isRefreshingTracks = wasRefreshing; }
            _refreshMetadata = true;
        }
    }

    private async Task<bool> LoadExternalSubtitleAsync(string path)
    {
        var item = _currentItem;
        var generation = Interlocked.Read(ref _playbackGeneration);
        if (item is null || _isClosing) return false;
        var track = await Task.Run(() => PlainSubtitleTrack.TryLoad(path));
        if (!IsCurrentPlayback(generation, item)) return false;
        // MPEG caption clocks and paused snapshot composition still require VLC.
        // Switch before presenting a native cue that cannot survive that handoff.
        if (track is not null && _windowsPlayer is not null && MpegStreamInspection.SupportsExtension(item.Source))
        {
            // Carry the request in the restored inventory. Otherwise VLC may
            // discover this newly created sidecar and then add the same file again.
            _plainSubtitles.Add(track);
            _selectedPlainSubtitle = track;
            _selectedEmbeddedSubtitleOrdinal = -1;
            return await EnsureVlcForFeatureAsync() && ReferenceEquals(item, _currentItem) && !_isClosing;
        }
        if (track is not null && _windowsPlayer is not null)
        {
            _plainSubtitles.RemoveAll(previous => previous.Path.Equals(track.Path, StringComparison.OrdinalIgnoreCase));
            _plainSubtitles.Add(track);
            _selectedPlainSubtitle = track;
            _selectedEmbeddedSubtitleOrdinal = -1;
            await PlayerCommandAsync(() => { }, player => player.SelectCaptionTrack(-1));
            if (!IsCurrentPlayback(generation, item)) return false;
            RefreshSubtitleClock();
            UpdateNativeSubtitles(PlaybackTime);
            RefreshTrackSelectors();
            return true;
        }
        if (!await EnsureVlcForFeatureAsync()) return false;
        generation = Interlocked.Read(ref _playbackGeneration);
        var media = _currentMedia;
        bool Current() => IsCurrentPlayback(generation, item) && media is not null && ReferenceEquals(media, _currentMedia);
        var previousIds = new HashSet<int>();
        var added = false;
        await _playbackOperations.Enqueue(() =>
        {
            if (!Current()) return;
            previousIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).Select(option => option.Id).ToHashSet();
            added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        });
        if (!added || !Current()) return false;

        // AddSlave's selection request can race an earlier restored track. Identify
        // the new input and confirm its selection before reporting a successful load.
        var clock = Stopwatch.StartNew();
        int? selectedId = null;
        var identified = false;
        while (Current() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (CanReadPlayer)
            {
                var addedIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0 && !previousIds.Contains(option.Id)).Select(option => option.Id).ToArray();
                if (addedIds.Length > 0)
                {
                    selectedId = addedIds.Contains(_mediaPlayer.Spu) ? _mediaPlayer.Spu : addedIds[0];
                    identified = addedIds.Length == 1;
                    break;
                }
            }
            await Task.Delay(20);
        }
        if (!Current() || selectedId is null) return false;
        await _playbackOperations.Enqueue(() =>
        {
            if (Current() && _mediaPlayer.Spu != selectedId.Value) _mediaPlayer.SetSpu(selectedId.Value);
        });
        while (Current() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (CanReadPlayer && _mediaPlayer.Spu == selectedId.Value)
            {
                if (identified && track is not null) _compatibilityPlainSubtitles[selectedId.Value] = track;
                RefreshTrackSelectors();
                return true;
            }
            await Task.Delay(20);
        }
        return false;
    }

    private void RefreshSubtitleClock()
    {
        if (_windowsPlayer is null || _selectedPlainSubtitle is null && _selectedEmbeddedSubtitleOrdinal is not >= 0)
        {
            _subtitleTimer?.Stop();
            NativeSubtitlePresenter.SetCue(null);
            return;
        }
        if (_subtitleTimer is null)
        {
            _subtitleTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
            _subtitleTimer.Tick += (_, _) =>
            {
                if (_isClosing) { _subtitleTimer.Stop(); return; }
                if (CanReadPlayer && _windowsPlayer is not null) UpdateNativeSubtitles(PlaybackTime);
            };
        }
        _subtitleTimer.Start();
    }

    private void RefreshNativeSubtitleSelector()
    {
        SubtitleTrackComboBox.Items.Clear();
        SubtitleTrackComboBox.Items.Add(new ComboBoxItem { Content = "Subtitles off", Tag = -1 });
        if (_windowsPlayer is { } player)
        {
            foreach (var track in player.CaptionTracks)
            {
                var option = new ComboBoxItem { Content = track.Name, Tag = track };
                SubtitleTrackComboBox.Items.Add(option);
                if (_selectedPlainSubtitle is null && track.Ordinal == _selectedEmbeddedSubtitleOrdinal)
                    SubtitleTrackComboBox.SelectedItem = option;
            }
        }
        foreach (var track in _plainSubtitles)
        {
            var option = new ComboBoxItem { Content = track.Name, Tag = track };
            SubtitleTrackComboBox.Items.Add(option);
            if (ReferenceEquals(track, _selectedPlainSubtitle)) SubtitleTrackComboBox.SelectedItem = option;
        }
        if (SubtitleTrackComboBox.SelectedItem is null) SubtitleTrackComboBox.SelectedIndex = 0;
    }

    private void UpdateNativeSubtitles(long time)
    {
        if (_windowsPlayer is not { } player ||
            _playbackState is not (PlaybackState.Playing or PlaybackState.Paused))
        {
            NativeSubtitlePresenter.SetCue(null);
            return;
        }
        // Audio has no picture aspect ratio. Use the whole playback pane for its
        // captions and avoid asking Media Engine for unavailable video metadata.
        NativeSubtitlePresenter.PictureAspectRatio = player.HasVideo ? player.PresentationAspectRatio : 0;
        NativeSubtitlePresenter.SetCue(_selectedPlainSubtitle?.CueAt(time) ??
            (_selectedEmbeddedSubtitleOrdinal is >= 0 ? player.CaptionAt(time) : null));
    }

    private void InitializeEmbeddedSubtitleSelection(WindowsMediaEngine player)
    {
        var tracks = player.CaptionTracks;
        if (tracks.Count > 0)
        {
            // Preserve the container's automatic selection when its format or
            // default/forced flags still need compatibility playback.
            if (!player.HasSupportedCaptionTracks())
                throw new NotSupportedException("These embedded captions require compatibility playback.");
            _selectedEmbeddedSubtitleOrdinal ??= _selectedPlainSubtitle is null ? tracks.Count - 1 : -1;
            player.InitializeCaptionSelection(_selectedEmbeddedSubtitleOrdinal, _selectedPlainSubtitle is not null);
        }
        RefreshSubtitleClock();
    }

    private void SelectCompatibilityEmbeddedSubtitle(int ordinal)
    {
        var tracks = _mediaPlayer.SpuDescription.Where(track => track.Id >= 0).ToArray();
        if (ordinal < 0 || ordinal >= tracks.Length || !_mediaPlayer.SetSpu(tracks[ordinal].Id))
            throw new InvalidOperationException("The embedded subtitle track is unavailable in compatibility playback.");
    }

    private void TryApplyPendingCompatibilitySubtitles()
    {
        if (_pendingCompatibilitySubtitles is not { } session || _windowsPlayer is not null || !CanReadPlayer ||
            _playbackState is not (PlaybackState.Playing or PlaybackState.Paused) ||
            _compatibilitySubtitleRestoreTask is { IsCompleted: false }) return;
        _compatibilitySubtitleRestoreTask = RestoreCompatibilitySubtitlesAsync(session, Interlocked.Read(ref _playbackGeneration));
        _ = ObserveTaskAsync(_compatibilitySubtitleRestoreTask);
    }

    private async Task RestoreCompatibilitySubtitlesAsync(SubtitleSession session, long generation)
    {
        var media = _currentMedia;
        bool Current() => !_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
            _windowsPlayer is null && media is not null && ReferenceEquals(media, _currentMedia);

        try
        {
            var selectedId = -1;
            if (session.SelectedEmbeddedOrdinal is >= 0)
            {
                var inventoryClock = Stopwatch.StartNew();
                while (Current())
                {
                    var embedded = CanReadPlayer ? _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).ToArray() : [];
                    if (embedded.Length > session.SelectedEmbeddedOrdinal.Value)
                    {
                        selectedId = embedded[session.SelectedEmbeddedOrdinal.Value].Id;
                        break;
                    }
                    if (inventoryClock.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("The embedded subtitle track was not discovered.");
                    await Task.Delay(20);
                }
            }
            foreach (var track in session.Tracks)
            {
                if (!Current()) return;
                var previousIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).Select(option => option.Id).ToHashSet();
                var added = false;
                await _playbackOperations.Enqueue(() =>
                {
                    if (Current()) added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(track.Path).AbsoluteUri, false);
                });
                if (!Current()) return;
                if (!added)
                    throw new InvalidOperationException("The compatibility player could not restore an external subtitle.");

                // AddSlave queues an input asynchronously. Its 'select' flag can lose
                // to automatic selection, so associate the new ID before selecting it.
                int? addedId = null;
                var clock = Stopwatch.StartNew();
                while (Current() && clock.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (CanReadPlayer)
                    {
                        var ids = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0 && !previousIds.Contains(option.Id)).ToArray();
                        if (ids.Length > 1) throw new InvalidOperationException("The restored subtitle track could not be identified.");
                        if (ids.Length == 1) { addedId = ids[0].Id; break; }
                    }
                    await Task.Delay(20);
                }
                if (!Current()) return;
                if (addedId is null) throw new TimeoutException("The compatibility player did not discover the restored subtitle.");
                _compatibilityPlainSubtitles[addedId.Value] = track;
                if (string.Equals(track.Path, session.SelectedPath, StringComparison.OrdinalIgnoreCase)) selectedId = addedId.Value;
            }

            await _playbackOperations.Enqueue(() =>
            {
                if (Current() && _mediaPlayer.Spu != selectedId && !_mediaPlayer.SetSpu(selectedId))
                    throw new InvalidOperationException("The compatibility player could not restore the subtitle selection.");
            });
            var selectionClock = Stopwatch.StartNew();
            while (Current() && (!CanReadPlayer || _mediaPlayer.Spu != selectedId))
            {
                if (selectionClock.Elapsed >= TimeSpan.FromSeconds(5)) throw new TimeoutException("The restored subtitle selection did not become active.");
                await Task.Delay(20);
            }
        }
        finally
        {
            if (Current() && ReferenceEquals(_pendingCompatibilitySubtitles, session))
            {
                _pendingCompatibilitySubtitles = null;
                _refreshMetadata = true;
            }
        }
    }

    private void ClearSubtitleSession()
    {
        _plainSubtitles.Clear();
        _compatibilityPlainSubtitles.Clear();
        _selectedPlainSubtitle = null;
        _selectedEmbeddedSubtitleOrdinal = null;
        _pendingCompatibilitySubtitles = null;
        _compatibilitySubtitleRestoreTask = null;
        _subtitleTimer?.Stop();
        NativeSubtitlePresenter.SetCue(null);
    }
}
