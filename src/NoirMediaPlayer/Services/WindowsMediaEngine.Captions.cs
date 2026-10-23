using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

[ComVisible(true), Guid("DF6B87B6-CE12-45DB-ABA7-432FE054E57D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsTimedTextNotify
{
    [PreserveSig] void TrackAdded(uint id);
    [PreserveSig] void TrackRemoved(uint id);
    [PreserveSig] void TrackSelected(uint id, int selected);
    [PreserveSig] void TrackReadyStateChanged(uint id);
    [PreserveSig] void Error(uint code, int extended, uint id);
    [PreserveSig] void Cue(uint code, double time, nint cue);
    [PreserveSig] void Reset();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class WindowsTimedTextNotify(Action changed, Action<uint, nint> cue, Action reset, Action failed) : IWindowsTimedTextNotify
{
    // Native callbacks must neither throw through COM nor take the engine command lock.
    private static void Run(Action action) { try { action(); } catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); } }
    public void TrackAdded(uint id) => Run(changed);
    public void TrackRemoved(uint id) => Run(changed);
    public void TrackSelected(uint id, int selected) => Run(changed);
    public void TrackReadyStateChanged(uint id) => Run(changed);
    public void Error(uint code, int extended, uint id) => Run(failed);
    public void Cue(uint code, double time, nint value) => Run(() => cue(code, value));
    public void Reset() => Run(reset);
}

internal sealed record WindowsCaptionTrack(int Id, int Ordinal, string Name, bool Selected, Guid Format);

internal sealed unsafe partial class WindowsMediaEngine
{
    private static readonly Guid SubRipFormat = new("5E467F2E-77CA-4CA5-8391-D142ED4B76C8");
    private readonly object _captionSync = new();
    private WindowsTimedTextNotify? _captionNotify;
    private SubtitleCue? _captionCue;
    private readonly Dictionary<int, SubtitleCue> _recentCaptionCues = [];
    private uint _captionCueId;
    private int _selectedCaptionId = -1;
    private bool _captionChoiceMade;
    private volatile bool _captionFailed, _captionDisposed;

    private void InitializeCaptionNotifications()
    {
        _captionNotify = new WindowsTimedTextNotify(
            () => { if (!_captionDisposed) _notify.EventNotify(1010, 0, 0); },
            ReceiveCaption, ClearCaption, FailCaption);
        var pointer = Marshal.GetComInterfaceForObject(_captionNotify, typeof(IWindowsTimedTextNotify));
        try { Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_timedText, 3))(_timedText, pointer)); }
        finally { Marshal.Release(pointer); }
    }

    internal IReadOnlyList<WindowsCaptionTrack> CaptionTracks
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                var result = new List<WindowsCaptionTrack>();
                if (_timedText == 0) return result;
                nint tracks = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_timedText, 11))(_timedText, &tracks));
                    if (tracks == 0) return result;
                    var count = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(tracks, 3))(tracks);
                    if (count > 32) throw new NotSupportedException("Too many embedded caption tracks.");
                    for (uint index = 0; index < count; index++)
                    {
                        nint track = 0, label = 0, language = 0;
                        try
                        {
                            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(tracks, 4))(tracks, index, &track));
                            var id = checked((int)((delegate* unmanaged[Stdcall]<nint, uint>)Slot(track, 3))(track));
                            var kind = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(track, 7))(track);
                            if (kind is not (1 or 2)) throw new NotSupportedException("Unknown embedded caption kind.");
                            var format = Guid.Empty;
                            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(track, 13))(track, &format));
                            _ = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(track, 4))(track, &label);
                            _ = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(track, 6))(track, &language);
                            var name = Marshal.PtrToStringUni(label);
                            var tag = Marshal.PtrToStringUni(language);
                            if (!string.IsNullOrWhiteSpace(tag))
                            {
                                try { tag = CultureInfo.GetCultureInfo(tag).DisplayName; }
                                catch (CultureNotFoundException) { }
                            }
                            name = string.IsNullOrWhiteSpace(name) ? $"Embedded subtitles {index + 1}" : name;
                            if (!string.IsNullOrWhiteSpace(tag)) name += $" · {tag}";
                            result.Add(new WindowsCaptionTrack(id, (int)index, name,
                                ((delegate* unmanaged[Stdcall]<nint, int>)Slot(track, 10))(track) != 0, format));
                        }
                        finally { Marshal.FreeCoTaskMem(language); Marshal.FreeCoTaskMem(label); if (track != 0) Marshal.Release(track); }
                    }
                }
                finally { if (tracks != 0) Marshal.Release(tracks); }
                return result;
            }
        }
    }

    internal bool HasSupportedCaptionTracks()
    {
        if (_captionFailed || _simpleCaptionDefaults is false) return false;
        var tracks = CaptionTracks;
        if (tracks.Count == 0) return true;
        if (Uri.TryCreate(_source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return false;
        // Validate the common local Matroska path first. Other formats and network
        // captions retain compatibility playback until their timelines are verified.
        return string.Equals(Path.GetExtension(_source), ".mkv", StringComparison.OrdinalIgnoreCase) &&
            _simpleCaptionDefaults is true && tracks.All(track => track.Format == SubRipFormat);
    }

    internal void InitializeCaptionSelection(int? requestedOrdinal, bool externalSelected)
    {
        lock (_sync)
        {
            EnsureAlive();
            if (_captionChoiceMade)
            {
                if (externalSelected && Volatile.Read(ref _selectedCaptionId) >= 0) SelectCaptionTrack(-1);
                return;
            }
            var tracks = CaptionTracks;
            if (tracks.Count == 0) return;
            // The pinned compatibility backend picks the last of equally default
            // Matroska subtitle tracks. Customized flags were rejected above.
            var ordinal = externalSelected ? -1 : requestedOrdinal ?? (tracks.Count - 1);
            if (ordinal < -1 || ordinal >= tracks.Count) throw new NotSupportedException("The embedded caption selection is unavailable.");
            SelectCaptionTrackCore(ordinal == -1 ? -1 : tracks[ordinal].Id, refreshCue: false);
        }
    }

    internal void SelectCaptionTrack(int id)
        => SelectCaptionTrackCore(id, refreshCue: true);

    private void SelectCaptionTrackCore(int id, bool refreshCue)
    {
        long position;
        bool seek;
        lock (_sync)
        {
            EnsureAlive();
            var tracks = CaptionTracks;
            if (id != -1 && !tracks.Any(track => track.Id == id && track.Format == SubRipFormat))
                throw new NotSupportedException("This caption track requires compatibility playback.");
            position = Time;
            _captionChoiceMade = true;
            Volatile.Write(ref _selectedCaptionId, id);
            ClearCaption();
            foreach (var track in tracks)
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_timedText, 4))(_timedText, (uint)track.Id, track.Id == id ? 1 : 0));
            // Embedded cues are streamed, not stored in GetCueList. Re-seek so
            // enabling a track midway through a cue also works while paused.
            seek = refreshCue && id >= 0 && IsSeekable;
        }
        if (!seek) return;
        // Reusing a cue already delivered by Windows makes paused off/on and
        // external/embedded toggles immediate, without a redundant seek.
        if (CaptionAt(position) is not null) return;
        // SetCurrentTime at the existing clock can be a no-op. Settle a distinct
        // nearby seek first so a newly selected in-band stream re-emits its cue.
        SeekChapter(position == 0 ? 1 : Math.Max(0, position - 250));
        SeekChapter(position);
    }

    internal SubtitleCue? CaptionAt(long milliseconds)
    {
        lock (_captionSync)
        {
            if (_captionFailed || _captionDisposed) return null;
            var id = Volatile.Read(ref _selectedCaptionId);
            return _recentCaptionCues.TryGetValue(id, out var cue) && milliseconds >= cue.Start && milliseconds < cue.End ? cue : null;
        }
    }

    private void ReceiveCaption(uint code, nint cue)
    {
        if (_captionDisposed) return;
        if (code == 2) { ClearCaption(); return; }
        if (cue == 0) return;
        try
        {
            var track = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(cue, 8))(cue);
            if (track != Volatile.Read(ref _selectedCaptionId)) return;
            var id = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(cue, 3))(cue);
            if (code == 1)
            {
                lock (_captionSync) { if (_captionCueId == id) _captionCue = null; }
                return;
            }
            if (code != 0) throw new NotSupportedException("Unknown caption event.");
            var parsed = ReadPlainCaption(cue);
            lock (_captionSync)
            {
                if (_captionDisposed) return;
                // Windows may deactivate the earlier cue or reuse its ID before
                // delivering an overlapping cue. Compare the copied time ranges.
                var previous = _captionCue ?? _recentCaptionCues.GetValueOrDefault((int)track);
                if (previous is not null &&
                    (previous.Start != parsed.Start || previous.End != parsed.End || previous.Text != parsed.Text) &&
                    previous.Start < parsed.End && parsed.Start < previous.End)
                    throw new NotSupportedException("Overlapping embedded captions require compatibility playback.");
                if (!_recentCaptionCues.ContainsKey((int)track) && _recentCaptionCues.Count >= 32)
                    throw new NotSupportedException("The embedded caption cache exceeds its track limit.");
                _captionCueId = id; _captionCue = parsed;
                _recentCaptionCues[(int)track] = parsed;
            }
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); FailCaption(); }
    }

    private static SubtitleCue ReadPlainCaption(nint cue)
    {
        nint region = 0, style = 0, binary = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(cue, 9))(cue, &binary));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(cue, 10))(cue, &region));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(cue, 11))(cue, &style));
            if (binary != 0 || region != 0 || style != 0) throw new NotSupportedException("Styled or binary captions require compatibility playback.");
            var start = ((delegate* unmanaged[Stdcall]<nint, double>)Slot(cue, 6))(cue);
            var duration = ((delegate* unmanaged[Stdcall]<nint, double>)Slot(cue, 7))(cue);
            if (!double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration <= 0 || start + duration > long.MaxValue / 1000d)
                throw new NotSupportedException("Invalid embedded caption timing.");
            var count = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(cue, 12))(cue);
            if (count is 0 or > 32) throw new NotSupportedException("Unsupported embedded caption size.");
            var lines = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                nint line = 0, text = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(cue, 13))(cue, index, &line));
                    if (((delegate* unmanaged[Stdcall]<nint, uint>)Slot(line, 4))(line) != 0)
                        throw new NotSupportedException("Formatted embedded text requires compatibility playback.");
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(line, 3))(line, &text));
                    var characters = (char*)text;
                    var length = 0;
                    while (characters != null && length <= 8192 && characters[length] != '\0') length++;
                    if (length > 8192) throw new NotSupportedException("Embedded text exceeds the caption size limit.");
                    var value = characters == null ? string.Empty : new string(characters, 0, length);
                    if (value.Contains('<') || value.Contains('{'))
                        throw new NotSupportedException("Embedded markup requires compatibility playback.");
                    lines.Add(value);
                }
                finally { Marshal.FreeCoTaskMem(text); if (line != 0) Marshal.Release(line); }
            }
            var startMilliseconds = (long)Math.Round(start * 1000);
            var endMilliseconds = (long)Math.Round((start + duration) * 1000);
            if (endMilliseconds <= startMilliseconds) throw new NotSupportedException("Embedded caption timing is outside the supported clock.");
            return new SubtitleCue(startMilliseconds, endMilliseconds, string.Join('\n', lines));
        }
        finally { if (binary != 0) Marshal.Release(binary); if (style != 0) Marshal.Release(style); if (region != 0) Marshal.Release(region); }
    }

    private void ClearCaption() { lock (_captionSync) _captionCue = null; }
    private void FailCaption()
    {
        if (_captionDisposed) return;
        _captionFailed = true;
        ClearCaption();
        _notify.EventNotify(1014, 0, 0);
    }
}
