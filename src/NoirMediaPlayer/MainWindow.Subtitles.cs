using System.Windows.Controls;
using System.Windows.Threading;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private readonly PlaybackReplayMemory<SubtitleSession> _replaySubtitles = new();
    private Task? _compatibilitySubtitleRestoreTask;
    private DispatcherTimer? _subtitleTimer;

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

    private void ClearSubtitleSession()
    {
        _plainSubtitles.Clear();
        _compatibilityPlainSubtitles.Clear();
        _compatibilityExternalSubtitlePaths.Clear();
        _compatibilityAutodetection = true;
        _compatibilitySubtitleLoadInProgress = false;
        _selectedPlainSubtitle = null;
        _selectedEmbeddedSubtitleOrdinal = null;
        _pendingCompatibilitySubtitles = null;
        _compatibilitySubtitleRestoreTask = null;
        _subtitleTimer?.Stop();
        NativeSubtitlePresenter.SetCue(null);
    }
}
