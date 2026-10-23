using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal sealed record WindowsDvdTrack(uint Id, string Language);
internal sealed record WindowsDvdEvent(int Code, long Parameter1, long Parameter2);
internal sealed record WindowsDvdCapability(string Name, string HResult, uint? Value, Guid? ClassId = null);
internal sealed record WindowsDvdPinDiagnostic(string Id, int Direction, bool Connected, string ConnectionResult);
internal sealed record WindowsDvdFilterDiagnostic(Guid ClassId, IReadOnlyList<WindowsDvdPinDiagnostic> Pins);
internal sealed record WindowsDvdBuildDiagnostic(string NativeResult, uint Flags, int StreamCount, int FailedStreams,
    uint FailedStreamFlags, bool InvalidVolume, bool UnknownVolume, IReadOnlyList<WindowsDvdFilterDiagnostic> Filters, string? InspectionError,
    IReadOnlyList<WindowsDvdConnectionAttempt>? ExplicitConnections = null, string? ExplicitResult = null);
internal sealed record WindowsDvdConnectionAttempt(string Step, string OutputPin, string InputPin, string HResult,
    IReadOnlyList<WindowsDvdMediaTypeDiagnostic>? MediaTypes = null, string? InspectionError = null);
internal sealed record WindowsDvdMediaTypeDiagnostic(Guid MajorType, Guid Subtype, Guid FormatType, uint FormatBytes,
    string QueryAcceptResult, string? ConnectResult);
internal sealed record WindowsDvdState(int GraphState, uint Domain, uint Title, uint Chapter,
    uint TitleCount, uint ChapterCount, long? PositionMilliseconds, long? DurationMilliseconds,
    uint? ProhibitedOperations, uint? AudioId, uint? SubtitleId, bool SubtitlesDisabled,
    IReadOnlyList<WindowsDvdTrack> Audio, IReadOnlyList<WindowsDvdTrack> Subtitles);

/// <summary>
/// First-party DirectShow DVD backend. Create, call and dispose on one COM STA with a
/// message pump. The caller owns the video HWND. Not yet enabled by the main player's routing.
/// ABI definitions follow the Windows SDK strmif.h, control.h and vmr9.h.
/// </summary>
internal sealed unsafe partial class WindowsDvdSession : IDisposable
{
    private static readonly Guid BuilderClass = new("FCC152B7-F372-11D0-8E00-00C04FD7C08B");
    private static readonly Guid BuilderInterface = new("FCC152B6-F372-11D0-8E00-00C04FD7C08B");
    private static readonly Guid VideoDecoder = new("212690FB-83E5-4526-8FD7-74478B7939CD");
    private static readonly Guid AudioDecoder = new("E1F1A0B8-BEEE-490D-BA7C-066C40B5E2B9");
    private static readonly Guid Vmr9 = new("51B4ABF3-748F-4E3B-A276-C828330E926A");
    private static readonly Guid BaseFilter = new("56A86895-0AD4-11CE-B03A-0020AF0BA770");
    private static readonly HashSet<Guid> AllowedFilters =
    [
        VideoDecoder, AudioDecoder, Vmr9,
        new("9B8C4620-2C1A-11D0-8493-00A02438AD48"), // Windows DVD Navigator
        new("79376820-07D0-11CF-A24D-0020AFD79767"), // DirectSound renderer
        new("E30629D1-27E5-11CE-875D-00608CB78066")  // Windows audio renderer
    ];
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly nint _window;
    private bool _apartmentInitialized, _disposed, _opened, _openAttempted;
    private bool _hasDestination;
    private NativeRect _lastDestination;
    private nint _builder, _graph, _control, _info, _mediaControl, _audio, _events, _video, _renderer;
    internal WindowsDvdBuildDiagnostic? LastBuildDiagnostic { get; private set; }

    internal WindowsDvdSession(nint window)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("The Windows DVD backend requires its own STA thread.");
        if (window == 0 || !IsWindow(window)) throw new ArgumentException("A live playback window is required.", nameof(window));
        _window = window;
        try
        {
            Check(CoInitializeEx(0, 2));
            _apartmentInitialized = true;
            Check(CoCreateInstance(in BuilderClass, 0, 1, in BuilderInterface, out _builder));
            nint graph = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_builder, 3))(_builder, &graph));
            _graph = graph;
            AddDecoder(VideoDecoder, "Microsoft DVD video decoder");
            AddDecoder(AudioDecoder, "Microsoft DVD audio decoder");

            nint configuration = 0;
            try
            {
                configuration = GetDvdInterface(new Guid("5A804648-4F66-4867-9C43-4F5C822CF1B8"));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(configuration, 8))(configuration, 2)); // Windowless
                _video = Query(configuration, new Guid("8F537D09-F85E-4414-B23B-502E54C79927"));
                _renderer = Query(configuration, BaseFilter);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_video, 10))(_video, window));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(_video, 9))(_video, 1)); // Original aspect, letterbox
                Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(_video, 14))(_video, 0)); // Black border
            }
            finally { Release(ref configuration); }
            _mediaControl = Query(_graph, new Guid("56A868B1-0AD4-11CE-B03A-0020AF0BA770"));
            _events = Query(_graph, new Guid("56A868B6-0AD4-11CE-B03A-0020AF0BA770"));
            _audio = Query(_graph, new Guid("56A868B3-0AD4-11CE-B03A-0020AF0BA770"));
        }
        catch { Dispose(); throw; }
    }

    internal void Open(string source)
    {
        EnsureAlive();
        if (_openAttempted) throw new InvalidOperationException("Create a new DVD session when changing sources.");
        var directory = ResolveDirectory(source);
        _openAttempted = true;
        var status = new RenderStatus();
        int result;
        fixed (char* path = directory)
        {
            // The configured renderer was selected through GetDvdInterface. Do not
            // combine VMR9_ONLY with that API. Keep the explicit Microsoft decoders.
            result = ((delegate* unmanaged[Stdcall]<nint, char*, uint, RenderStatus*, int>)Slot(_builder, 5))
                (_builder, path, 0x208, &status); // SWDEC_ONLY | DO_NOT_CLEAR
        }
        LastBuildDiagnostic = CaptureBuildDiagnostic(result, 0x208, status);
        var explicitGraph = false;
        if (!IsCompleteGraph(result, status))
        {
            if (!CanConnectExplicitly(LastBuildDiagnostic)) throw CreateBuildException(result, status);
            var attempts = new List<WindowsDvdConnectionAttempt>();
            var connectionResult = ConnectWindowsFilters(attempts);
            LastBuildDiagnostic = CaptureBuildDiagnostic(result, 0x208, status) with
            {
                ExplicitConnections = attempts.AsReadOnly(), ExplicitResult = $"0x{connectionResult:X8}"
            };
            if (connectionResult < 0)
            {
                var failed = attempts.FirstOrDefault(attempt => attempt.HResult != "0x00000000");
                throw new COMException($"Windows rejected the explicit DVD connection{(failed is null ? "" : " at " + failed.Step)} " +
                    $"(0x{connectionResult:X8}). The diagnostic report contains the connection results.", connectionResult);
            }
            explicitGraph = true;
        }
        var classes = GetFilterClasses();
        if (!classes.Contains(Vmr9) || !classes.Contains(VideoDecoder) ||
            classes.Any(value => !AllowedFilters.Contains(value)) || !RendererIsInGraph())
            throw new NotSupportedException("The DVD graph did not retain the selected Windows renderer and decoders.");
        try
        {
            // Unlike renderer configuration, navigation interfaces are available
            // from the builder only after RenderDvdVideoVolume succeeds.
            if (explicitGraph) GetNavigatorInterfaces();
            else
            {
                _control = GetDvdInterface(new Guid("33BC7430-EEC0-11D2-8201-00A0C9D74842"));
                _info = GetDvdInterface(new Guid("34151510-EEC0-11D2-8201-00A0C9D74842"));
            }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(_control, 36))(_control, 1, 0)); // Preserve resume state on graph stop
            _opened = true;
            SetVolume(75, false);
            UpdateVideo();
        }
        catch { _opened = false; throw; }
    }

    internal static string ResolveDirectory(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) || source.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local DVD drive or VIDEO_TS folder.", nameof(source));
        var full = Path.GetFullPath(source);
        var directory = new DirectoryInfo(full);
        var videoTs = directory.Name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase)
            ? directory.FullName : Path.Combine(full, "VIDEO_TS");
        // Explicit source only: never ask the DVD Navigator to scan other drives.
        using var file = new FileStream(Path.Combine(videoTs, "VIDEO_TS.IFO"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> signature = stackalloc byte[12];
        file.ReadExactly(signature);
        if (!signature.SequenceEqual("DVDVIDEO-VMG"u8)) throw new InvalidDataException("The folder has no valid DVD video manager.");
        return videoTs;
    }

    internal void Play() { EnsureOpen(); Check(Call(_mediaControl, 7)); }
    internal void Pause() { EnsureOpen(); Check(Call(_mediaControl, 8)); }
    internal void Stop() { EnsureOpen(); Check(Call(_mediaControl, 9)); }
    internal void ShowMenu(bool title = false) => CommandValue(15, title ? 2u : 3u);
    internal void Resume() => Command(16);
    internal void NextChapter() => Command(12);
    internal void PreviousChapter() => Command(10);
    internal void PlayTitle(uint title) { if (title == 0) throw new ArgumentOutOfRangeException(nameof(title)); CommandValue(3, title); }
    internal void SelectAudio(uint id) => CommandValue(23, id);
    internal void SelectSubtitle(uint id) => CommandValue(24, id);
    internal void EnableSubtitles(bool enabled) => CommandValue(25, enabled ? 1u : 0u);
    internal void SelectAngle(uint angle) { if (angle == 0) throw new ArgumentOutOfRangeException(nameof(angle)); CommandValue(26, angle); }

    internal void Navigate(uint direction)
    {
        EnsureOpen();
        if (direction is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(direction));
        Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(_control, 17))(_control, direction));
    }
    internal void ActivateButton() { EnsureOpen(); Check(Call(_control, 18)); }
    internal void ExitStill() { EnsureOpen(); Check(Call(_control, 21)); }

    internal void Seek(long milliseconds)
    {
        EnsureOpen();
        var time = DvdTimeCode.FromMilliseconds(milliseconds);
        nint command = 0;
        try { Check(((delegate* unmanaged[Stdcall]<nint, DvdTimeCode*, uint, nint*, int>)Slot(_control, 8))(_control, &time, 1, &command)); }
        finally { Release(ref command); }
    }
    internal void SetRate(double rate)
    {
        EnsureOpen();
        if (!double.IsFinite(rate) || rate is < 0.25 or > 4) throw new ArgumentOutOfRangeException(nameof(rate));
        nint command = 0;
        try { Check(((delegate* unmanaged[Stdcall]<nint, double, uint, nint*, int>)Slot(_control, 13))(_control, rate, 1, &command)); }
        finally { Release(ref command); }
    }
    internal void SetVolume(int volume, bool muted)
    {
        EnsureOpen();
        Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_audio, 7))(_audio, VolumeAttenuation(volume, muted)));
    }
    internal static int VolumeAttenuation(int volume, bool muted) => muted || volume <= 0 ? -10000 :
        Math.Clamp((int)Math.Round(2000 * Math.Log10(Math.Clamp(volume, 1, 100) / 100d)), -10000, 0);

    internal void UpdateVideo()
    {
        EnsureAlive();
        if (!GetClientRect(_window, out var rectangle) || rectangle.Right <= 0 || rectangle.Bottom <= 0) return;
        if (_hasDestination && rectangle.Left == _lastDestination.Left && rectangle.Top == _lastDestination.Top &&
            rectangle.Right == _lastDestination.Right && rectangle.Bottom == _lastDestination.Bottom) return;
        Check(((delegate* unmanaged[Stdcall]<nint, NativeRect*, NativeRect*, int>)Slot(_video, 6))(_video, null, &rectangle));
        _lastDestination = rectangle; _hasDestination = true;
    }
    internal void Repaint(nint dc = 0)
    {
        EnsureAlive();
        if (!_opened) return;
        var owned = dc == 0;
        if (owned) dc = GetDC(_window);
        try
        {
            // Before the first decoded picture the VMR can legitimately return E_FAIL.
            if (dc != 0) _ = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(_video, 11))(_video, _window, dc);
        }
        finally { if (owned && dc != 0) _ = ReleaseDC(_window, dc); }
    }
    internal void DisplayChanged() { EnsureAlive(); Check(Call(_video, 12)); _hasDestination = false; UpdateVideo(); }

    internal WindowsDvdState GetState()
    {
        EnsureOpen();
        int graphState = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int, int*, int>)Slot(_mediaControl, 10))(_mediaControl, 0, &graphState));
        uint domain = 0, titles = 0, volumes = 0, volume = 0, side = 0, chapters = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_info, 3))(_info, &domain));
        _ = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, uint*, uint*, int>)Slot(_info, 21))(_info, &volumes, &volume, &side, &titles);
        var location = new DvdLocation();
        var hasLocation = domain == 4 && ((delegate* unmanaged[Stdcall]<nint, DvdLocation*, int>)Slot(_info, 4))(_info, &location) >= 0;
        var duration = new DvdTimeCode(); uint durationFlags = 0, prohibited = 0;
        var hasDuration = domain == 4 && ((delegate* unmanaged[Stdcall]<nint, DvdTimeCode*, uint*, int>)Slot(_info, 5))(_info, &duration, &durationFlags) >= 0;
        if (hasLocation) _ = ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(_info, 27))(_info, location.Title, &chapters);
        var hasProhibited = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(_info, 10))(_info, &prohibited) >= 0;
        uint audioCount = 0, audioId = 0, subtitleCount = 0, subtitleId = 0; int disabled = 1;
        var hasAudio = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(_info, 8))(_info, &audioCount, &audioId) >= 0;
        var hasSubtitles = ((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int*, int>)Slot(_info, 9))(_info, &subtitleCount, &subtitleId, &disabled) >= 0;
        if (audioCount > 8 || subtitleCount > 32 || titles > 99) throw new InvalidDataException("Invalid Windows DVD stream inventory.");
        return new WindowsDvdState(graphState, domain, location.Title, location.Chapter, titles, chapters,
            hasLocation ? location.Time.ToMilliseconds(location.Flags) : null,
            hasDuration ? duration.ToMilliseconds(durationFlags) : null, hasProhibited ? prohibited : null,
            hasAudio ? audioId : null, hasSubtitles ? subtitleId : null, disabled != 0,
            ReadTracks(hasAudio ? audioCount : 0, 13), ReadTracks(hasSubtitles ? subtitleCount : 0, 14));
    }

    internal IReadOnlyList<WindowsDvdEvent> DrainEvents()
    {
        EnsureAlive();
        var result = new List<WindowsDvdEvent>();
        for (var index = 0; index < 64; index++)
        {
            int code = 0; nint first = 0, second = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, int*, nint*, nint*, int, int>)Slot(_events, 8))(_events, &code, &first, &second, 0);
            if (status == unchecked((int)0x80004004)) break; // E_ABORT: empty queue
            Check(status);
            try { result.Add(new WindowsDvdEvent(code, (long)first, (long)second)); }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, int>)Slot(_events, 12))(_events, code, first, second)); }
        }
        return result.AsReadOnly();
    }

    internal IReadOnlyList<Guid> GetFilterClasses()
    {
        EnsureAlive();
        var result = new List<Guid>();
        VisitFilters(filter =>
        {
            var id = new Guid();
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(filter, 3))(filter, &id));
            result.Add(id);
        });
        return result.AsReadOnly();
    }
    internal IReadOnlyList<WindowsDvdCapability> GetVideoDecoderCapabilities()
    {
        EnsureAlive();
        var result = new List<WindowsDvdCapability>();
        VisitFilters(filter =>
        {
            var id = new Guid();
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(filter, 3))(filter, &id));
            if (id != VideoDecoder) return;
            var capabilityId = new Guid("C0DFF467-D499-4986-972B-E1D9090FA941");
            var query = Marshal.QueryInterface(filter, in capabilityId, out var capability);
            try
            {
                foreach (var (name, index) in new[] { ("DVD", 3u), ("VMR9", 6u), ("EVR", 7u) })
                {
                    uint value = 0;
                    var status = query < 0 ? query :
                        ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(capability, 3))(capability, index, &value);
                    result.Add(new WindowsDvdCapability(name, $"0x{status:X8}", status >= 0 ? value : null));
                }
            }
            finally { Release(ref capability); }
        });
        return result.AsReadOnly();
    }
    internal WindowsDvdBuildDiagnostic InspectGraph()
    {
        EnsureAlive();
        return LastBuildDiagnostic ?? CaptureBuildDiagnostic(unchecked((int)0x80040279), 0, new RenderStatus());
    }

    internal IReadOnlyList<WindowsDvdCapability> GetOptionalCodecAvailability()
    {
        EnsureAlive();
        // Registration/activation only. Media Foundation MPEG-2 is optional and
        // does not replace the DirectShow DVD/CSS path. Its absence is context for
        // a connection failure, never a reason to refuse an otherwise working DVD.
        var result = new List<WindowsDvdCapability>();
        var transform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
        foreach (var (name, classId) in new[]
        {
            ("Media Foundation MPEG-2", new Guid("2D709E52-123F-49B5-9CBC-9AF5CDE28FB9")), // CLSID_CMSMPEGDecoderMFT, wmcodecdsp.h
            ("Media Foundation H.264", new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D"))
        })
        {
            nint codec = 0;
            try
            {
                var status = CoCreateInstance(in classId, 0, 1, in transform, out codec);
                result.Add(new WindowsDvdCapability(name, $"0x{status:X8}", status >= 0 ? 1u : 0u, classId));
            }
            finally { Release(ref codec); }
        }
        return result.AsReadOnly();
    }
    private WindowsDvdBuildDiagnostic CaptureBuildDiagnostic(int result, uint flags, RenderStatus status)
    {
        var filters = new List<WindowsDvdFilterDiagnostic>();
        string? error = null;
        try
        {
            VisitFilters(filter =>
            {
                var id = new Guid();
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(filter, 3))(filter, &id));
                filters.Add(new WindowsDvdFilterDiagnostic(id, AllowedFilters.Contains(id) ? ReadPins(filter) : []));
            });
        }
        catch (Exception exception) when (exception is COMException or InvalidDataException)
        {
            // Diagnostics must preserve the original graph-building error.
            error = $"0x{exception.HResult:X8}: {exception.Message}";
        }
        return new WindowsDvdBuildDiagnostic($"0x{result:X8}", flags, status.StreamCount, status.FailedStreams,
            status.FailedStreamFlags, status.InvalidVolume != 0, status.UnknownVolume != 0, filters.AsReadOnly(), error);
    }
    private static IReadOnlyList<WindowsDvdPinDiagnostic> ReadPins(nint filter)
    {
        var result = new List<WindowsDvdPinDiagnostic>();
        nint enumerator = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(filter, 10))(filter, &enumerator));
            for (var index = 0; index < 64; index++)
            {
                nint pin = 0; uint fetched = 0;
                var next = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(enumerator, 3))(enumerator, 1, &pin, &fetched);
                try
                {
                    Check(next);
                    if (next == 1) return result.AsReadOnly();
                    if (pin == 0 || fetched != 1) throw new InvalidDataException("Invalid DVD pin enumeration.");
                    nint text = 0, connected = 0;
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pin, 10))(pin, &text));
                        var id = Marshal.PtrToStringUni(text) ?? "";
                        int direction = 0;
                        Check(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(pin, 9))(pin, &direction));
                        var connection = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pin, 6))(pin, &connected);
                        result.Add(new WindowsDvdPinDiagnostic(id, direction, connection >= 0 && connected != 0, $"0x{connection:X8}"));
                    }
                    finally { if (text != 0) Marshal.FreeCoTaskMem(text); Release(ref connected); }
                }
                finally { Release(ref pin); }
            }
            throw new InvalidDataException("Too many DVD graph pins.");
        }
        finally { Release(ref enumerator); }
    }
    private bool RendererIsInGraph()
    {
        var expected = Query(_renderer, new Guid("00000000-0000-0000-C000-000000000046"));
        var found = false;
        try
        {
            VisitFilters(filter =>
            {
                var identity = Query(filter, new Guid("00000000-0000-0000-C000-000000000046"));
                try { found |= identity == expected; }
                finally { Release(ref identity); }
            });
        }
        finally { Release(ref expected); }
        return found;
    }
    private void VisitFilters(Action<nint> visit)
    {
        nint enumerator = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_graph, 5))(_graph, &enumerator));
            for (var index = 0; index < 64; index++)
            {
                nint filter = 0; uint fetched = 0;
                var result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)Slot(enumerator, 3))(enumerator, 1, &filter, &fetched);
                try
                {
                    Check(result);
                    if (result == 1) return;
                    if (fetched != 1 || filter == 0) throw new InvalidDataException("Invalid graph enumeration.");
                    visit(filter);
                }
                finally { Release(ref filter); }
            }
            throw new InvalidDataException("The DVD graph has too many filters.");
        }
        finally { Release(ref enumerator); }
    }
    private IReadOnlyList<WindowsDvdTrack> ReadTracks(uint count, int slot)
    {
        var tracks = new List<WindowsDvdTrack>();
        for (uint id = 0; id < count; id++)
        {
            uint language = 0;
            var result = ((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(_info, slot))(_info, id, &language);
            var name = "Unknown language";
            if (result >= 0 && language != 0)
            {
                try { name = CultureInfo.GetCultureInfo((int)language).DisplayName; }
                catch (CultureNotFoundException) { }
            }
            tracks.Add(new WindowsDvdTrack(id, name));
        }
        return tracks.AsReadOnly();
    }
    private void AddDecoder(Guid classId, string name)
    {
        nint decoder = 0;
        try
        {
            Check(CoCreateInstance(in classId, 0, 1, in BaseFilter, out decoder));
            fixed (char* label = name)
                Check(((delegate* unmanaged[Stdcall]<nint, nint, char*, int>)Slot(_graph, 3))(_graph, decoder, label));
        }
        finally { Release(ref decoder); }
    }
    private nint GetDvdInterface(Guid id)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(_builder, 4))(_builder, &id, &result));
        return result;
    }
    private void Command(int slot)
    {
        EnsureOpen(); nint command = 0;
        try { Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_control, slot))(_control, 1, &command)); }
        finally { Release(ref command); }
    }
    private void CommandValue(int slot, uint value)
    {
        EnsureOpen(); nint command = 0;
        try { Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(_control, slot))(_control, value, 1, &command)); }
        finally { Release(ref command); }
    }
    private void EnsureAlive()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Use the DVD session's owning STA thread.");
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private void EnsureOpen() { EnsureAlive(); if (!_opened) throw new InvalidOperationException("No complete DVD playback graph is open."); }
    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Dispose the DVD session on its owning thread.");
        if (_disposed) return;
        _disposed = true;
        try { if (_mediaControl != 0) _ = Call(_mediaControl, 9); }
        finally
        {
            Release(ref _events); Release(ref _audio); Release(ref _mediaControl); Release(ref _info);
            Release(ref _control); Release(ref _video); Release(ref _renderer); Release(ref _graph); Release(ref _builder);
            if (_apartmentInitialized) { CoUninitialize(); _apartmentInitialized = false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderStatus
    {
        internal int VpeResult, InvalidVolume, UnknownVolume, NoLine21Input, NoLine21Output, StreamCount, FailedStreams;
        internal uint FailedStreamFlags;
    }
    internal static bool IsCompleteGraph(int result, RenderStatus status) => result == 0 &&
        status.StreamCount > 0 && status.InvalidVolume == 0 && status.UnknownVolume == 0 &&
        status.FailedStreams == 0 && status.FailedStreamFlags == 0;
    internal static COMException CreateBuildException(int result, RenderStatus status)
    {
        var explanation = result == unchecked((int)0x8004027B)
            ? "Windows could not connect suitable DVD decoders."
            : "Windows could not build a complete DVD playback graph.";
        return new COMException($"{explanation} Native error 0x{result:X8}; " +
            $"failed streams 0x{status.FailedStreamFlags:X}; invalid volume {status.InvalidVolume != 0}. " +
            "The diagnostic report records the decoder capabilities and graph connections.", result < 0 ? result : unchecked((int)0x80004005));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DvdLocation { internal uint Title, Chapter; internal DvdTimeCode Time; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct DvdTimeCode
    {
        internal byte Hours, Minutes, Seconds, Frames;
        internal long? ToMilliseconds(uint flags)
        {
            if (Minutes >= 60 || Seconds >= 60) return null;
            var seconds = ((long)Hours * 60 + Minutes) * 60 + Seconds;
            if ((flags & 1) != 0 && (flags & 2) == 0 && Frames < 25) return seconds * 1000 + Frames * 40;
            if ((flags & 2) != 0 && (flags & 1) == 0 && Frames < 30)
            {
                var frames = seconds * 30 + Frames;
                if ((flags & 4) != 0)
                {
                    var minutes = Hours * 60 + Minutes;
                    if (Minutes % 10 != 0 && Seconds == 0 && Frames < 2) return null;
                    frames -= 2 * (minutes - minutes / 10);
                    return frames * 1001 / 30;
                }
                return frames * 1000 / 30;
            }
            return Frames == 0 ? seconds * 1000 : null;
        }
        internal static DvdTimeCode FromMilliseconds(long milliseconds)
        {
            if (milliseconds is < 0 or >= 256L * 60 * 60 * 1000) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var seconds = milliseconds / 1000;
            return new DvdTimeCode { Hours = (byte)(seconds / 3600), Minutes = (byte)(seconds / 60 % 60), Seconds = (byte)(seconds % 60) };
        }
    }
    private static nint Slot(nint pointer, int index) => (*(nint**)pointer)[index];
    private static int Call(nint pointer, int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(pointer, slot))(pointer);
    private static nint Query(nint pointer, Guid id) { Check(Marshal.QueryInterface(pointer, in id, out var value)); return value; }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(ref nint pointer) { if (pointer != 0) Marshal.Release(Interlocked.Exchange(ref pointer, 0)); }
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
}
