using System.Collections.Concurrent;
using System.IO;
using LibVLCSharp.Shared;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private sealed record SubtitleSession(PlainSubtitleTrack[] Tracks, string? SelectedPath, int? SelectedEmbeddedOrdinal = null,
        string[]? CompatibilityPaths = null, bool AllowCompatibilityAutodetection = false);
    private readonly List<PlainSubtitleTrack> _plainSubtitles = [];
    private readonly ConcurrentDictionary<int, PlainSubtitleTrack> _compatibilityPlainSubtitles = new();
    private readonly ConcurrentDictionary<int, string> _compatibilityExternalSubtitlePaths = new();
    private bool _compatibilityAutodetection = true;
    private bool _compatibilitySubtitleLoadInProgress;
    private PlainSubtitleTrack? _selectedPlainSubtitle;
    private SubtitleSession? _pendingCompatibilitySubtitles;
    private int? _selectedEmbeddedSubtitleOrdinal;

    // Run on the serialized playback worker. Preserve manual files, including
    // compatibility-only formats, while leaving VLC's automatic inventory enabled
    // when that inventory was not originally supplied by the native session.
    private SubtitleSession CaptureCompatibilitySubtitleSession()
    {
        if (_compatibilitySubtitleLoadInProgress)
            throw new InvalidOperationException("Wait for subtitle loading to finish before rotating.");
        var ids = _mediaPlayer.SpuDescription.Where(track => track.Id >= 0).Select(track => track.Id).ToArray();
        var paths = _compatibilityExternalSubtitlePaths.ToArray().ToDictionary(entry => entry.Key, entry => entry.Value);
        var plainTracks = _compatibilityPlainSubtitles.ToArray().ToDictionary(entry => entry.Key, entry => entry.Value);
        var external = ids.Where(paths.ContainsKey).Select(id => (Id: id, Path: paths[id])).ToArray();
        var plain = external.Where(entry => plainTracks.ContainsKey(entry.Id)).Select(entry => plainTracks[entry.Id]).ToArray();
        var selected = _mediaPlayer.Spu;
        var selectedPath = external.FirstOrDefault(entry => entry.Id == selected).Path;
        var automaticIds = ids.Where(id => !external.Any(entry => entry.Id == id)).ToArray();
        var ordinal = selectedPath is null ? PlaybackTrackSelection.Ordinal(automaticIds, selected) : -1;
        var extraPaths = external.Select(entry => entry.Path)
            .Where(path => !plain.Any(track => string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new SubtitleSession(plain, selectedPath, ordinal, extraPaths, _compatibilityAutodetection);
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
        if (_compatibilitySubtitleLoadInProgress || !await EnsureVlcForFeatureAsync()) return false;
        if (_compatibilitySubtitleLoadInProgress) return false;
        generation = Interlocked.Read(ref _playbackGeneration);
        var media = _currentMedia;
        bool Current() => IsCurrentPlayback(generation, item) && media is not null && ReferenceEquals(media, _currentMedia);
        _compatibilitySubtitleLoadInProgress = true;
        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5));
        try
        {
            var fullPath = Path.GetFullPath(path);
            var previousIds = new HashSet<int>();
            var added = false;
            await QueueCompatibilitySubtitleCommandAsync(deadline, Current, () =>
            {
                previousIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).Select(option => option.Id).ToHashSet();
                // Select only after the new track has an unambiguous file identity.
                added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(fullPath).AbsoluteUri, false);
            });
            if (!added || !Current()) return false;

            int? selectedId = null;
            while (Current())
            {
                deadline.ThrowIfExpired("The compatibility player did not discover the loaded subtitle.");
                if (CanReadPlayer)
                {
                    var addedIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0 && !previousIds.Contains(option.Id)).Select(option => option.Id).ToArray();
                    if (addedIds.Length > 1)
                        throw new InvalidOperationException("The loaded subtitle could not be distinguished from other newly discovered tracks.");
                    if (addedIds.Length == 1) { selectedId = addedIds[0]; break; }
                }
                await Task.Delay(20);
            }
            if (!Current() || selectedId is null) return false;
            await QueueCompatibilitySubtitleCommandAsync(deadline, Current, () =>
            {
                if (_mediaPlayer.Spu != selectedId.Value && !_mediaPlayer.SetSpu(selectedId.Value))
                    throw new InvalidOperationException("The compatibility player could not select the loaded subtitle.");
            });
            while (Current() && (!CanReadPlayer || _mediaPlayer.Spu != selectedId.Value))
            {
                deadline.ThrowIfExpired("The loaded subtitle selection did not become active.");
                await Task.Delay(20);
            }
            if (!Current()) return false;
            _compatibilityExternalSubtitlePaths[selectedId.Value] = fullPath;
            if (track is not null) _compatibilityPlainSubtitles[selectedId.Value] = track;
            RefreshTrackSelectors();
            return true;
        }
        catch (Exception exception)
        {
            // A partially attached, unidentified file must not be treated as an
            // automatic track by a later rotation. Retire this uncertain session.
            if (Current()) FailCompatibilityRestoration(generation, "subtitle loading", exception);
            return false;
        }
        finally
        {
            if (Current()) _compatibilitySubtitleLoadInProgress = false;
        }
    }

    private async Task RestoreCompatibilitySubtitlesAsync(SubtitleSession session, long generation)
    {
        var media = _currentMedia;
        bool Current() => !_isClosing && generation == Interlocked.Read(ref _playbackGeneration) &&
            _windowsPlayer is null && media is not null && ReferenceEquals(media, _currentMedia);

        var restored = false;
        var deadline = new PlaybackRestorationDeadline(TimeSpan.FromSeconds(5));
        try
        {
            var selectedId = -1;
            if (session.SelectedEmbeddedOrdinal is >= 0)
            {
                while (Current())
                {
                    deadline.ThrowIfExpired("The embedded subtitle track was not discovered.");
                    var embedded = CanReadPlayer ? _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).ToArray() : [];
                    if (embedded.Length > session.SelectedEmbeddedOrdinal.Value)
                    {
                        selectedId = embedded[session.SelectedEmbeddedOrdinal.Value].Id;
                        break;
                    }
                    await Task.Delay(20);
                }
            }
            var paths = session.Tracks.Select(track => track.Path).Concat(session.CompatibilityPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in paths)
            {
                if (!Current()) return;
                deadline.ThrowIfExpired("The compatibility player did not restore subtitles in time.");
                if (!File.Exists(path)) throw new FileNotFoundException("A subtitle file needed for restoration is unavailable.", path);
                var track = session.Tracks.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
                var previousIds = new HashSet<int>();
                var added = false;
                await QueueCompatibilitySubtitleCommandAsync(deadline, Current, () =>
                {
                    previousIds = _mediaPlayer.SpuDescription.Where(option => option.Id >= 0).Select(option => option.Id).ToHashSet();
                    added = _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, false);
                });
                if (!Current()) return;
                if (!added)
                    throw new InvalidOperationException("The compatibility player could not restore an external subtitle.");

                // AddSlave queues an input asynchronously. Its 'select' flag can lose
                // to automatic selection, so associate the new ID before selecting it.
                int? addedId = null;
                while (Current())
                {
                    deadline.ThrowIfExpired("The compatibility player did not discover the restored subtitle.");
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
                _compatibilityExternalSubtitlePaths[addedId.Value] = path;
                if (track is not null) _compatibilityPlainSubtitles[addedId.Value] = track;
                if (string.Equals(path, session.SelectedPath, StringComparison.OrdinalIgnoreCase)) selectedId = addedId.Value;
            }

            await QueueCompatibilitySubtitleCommandAsync(deadline, Current, () =>
            {
                if (_mediaPlayer.Spu != selectedId && !_mediaPlayer.SetSpu(selectedId))
                    throw new InvalidOperationException("The compatibility player could not restore the subtitle selection.");
            });
            while (Current() && (!CanReadPlayer || _mediaPlayer.Spu != selectedId))
            {
                deadline.ThrowIfExpired("The restored subtitle selection did not become active.");
                await Task.Delay(20);
            }
            restored = Current();
        }
        catch (Exception exception)
        {
            if (Current()) FailCompatibilityRestoration(generation, "subtitles", exception);
            throw;
        }
        finally
        {
            if (restored && Current() && ReferenceEquals(_pendingCompatibilitySubtitles, session))
            {
                _pendingCompatibilitySubtitles = null;
                _refreshMetadata = true;
            }
        }
    }

    private async Task QueueCompatibilitySubtitleCommandAsync(PlaybackRestorationDeadline deadline, Func<bool> current, Action command)
    {
        const string timeoutMessage = "The compatibility player did not restore subtitles in time.";
        deadline.ThrowIfExpired(timeoutMessage);
        var operation = _playbackOperations.Enqueue(() =>
        {
            if (!current()) return;
            deadline.ThrowIfExpired(timeoutMessage);
            command();
        });
        // A timeout only abandons this waiter. The queue still owns the operation
        // and eventual teardown; observe late faults and suppress stale commands.
        _ = ObserveTaskAsync(operation);
        await operation.WaitAsync(deadline.Remaining);
        if (current()) deadline.ThrowIfExpired(timeoutMessage);
    }
}
