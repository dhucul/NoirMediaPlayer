using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NoirMediaPlayer.Services;

internal sealed unsafe partial class WindowsMediaEngine
{
    private bool _dvdStreamOpened;
    private long _dvdFrames;
    private long? _dvdFrameTime100ns;
    internal long? DvdFrameTime100ns { get { lock (_sync) { EnsureAlive(); return _dvdFrameTime100ns; } } }
    private DvdSubpictureOverlay? _dvdSubpicture;
    private DvdSubpictureTiming? _dvdSubpictureTiming;
    private DvdSubpictureFrame? _dvdTimedFrame;
    private DvdButtonHighlight? _dvdTimedHighlight;
    private bool _dvdMenuWasPlaying;
    private bool _dvdHeld;
    private bool _dvdTimedFrameSet;
    private bool _dvdClockNeedsStart, _dvdPlayWhenStarted;
    private bool _dvdSourceInitiallyPaused;
    private string? _dvdFrameFailure;
    internal string? DvdFrameFailure { get { lock (_sync) { EnsureAlive(); return _dvdFrameFailure; } } }
    internal long? DvdSubpictureTime90Khz { get { lock (_sync) { EnsureAlive(); return _dvdSubpictureTiming?.Current90Khz; } } }
    internal bool DvdSubpictureVisible { get { lock (_sync) { EnsureAlive(); return _dvdSubpicture is not null; } } }

    // Configure before the first frame. Sampling and compositing happen inside
    // the native frame pump lock, before that same video frame can be painted.
    internal void SetDvdSubpictureTiming(DvdSubpictureTiming timing)
    {
        lock (_sync)
        {
            EnsureAlive();
            if (!_dvdStreamOpened || !_softwareVideo || !_dvdSourceInitiallyPaused || _dvdFrames != 0 || !IsPaused) throw new InvalidOperationException("Configure DVD subpicture timing at a paused source entry before its first frame.");
            _dvdSourceInitiallyPaused = false;
            _dvdSubpictureTiming = timing; _dvdTimedFrame = null; _dvdTimedFrameSet = false;
            _dvdClockNeedsStart = _dvdPlayWhenStarted = true;
        }
    }
    private void PrepareDvdSubpicture(long timestamp)
    {
        if (_dvdSubpictureTiming is null) return;
        if (_dvdClockNeedsStart)
        {
            if (!IsPaused || _dvdFrames != 0) throw new NotSupportedException("DVD timing requires the first decoded frame at paused source entry.");
            _dvdSubpictureTiming.Menu?.ValidateCanvas(checked((int)VideoSize.Height));
            _dvdSubpictureTiming.BindFirstFrame(timestamp);
        }
        var frame = _dvdSubpictureTiming.Sample(timestamp);
        var highlight = _dvdSubpictureTiming.Menu?.Highlight;
        if (_dvdTimedFrameSet && ReferenceEquals(frame, _dvdTimedFrame) && highlight == _dvdTimedHighlight) return;
        var size = VideoSize;
        _dvdSubpicture = frame?.Colorize(_dvdSubpictureTiming.Palette, checked((int)size.Height), highlight);
        _dvdTimedFrame = frame; _dvdTimedHighlight = highlight; _dvdTimedFrameSet = true;
    }
    internal (DvdMovingMenuCommand? Command, DvdVmState? Registers) PollDvdMenu()
    {
        lock (_sync)
        {
            EnsureAlive(); var menu = _dvdSubpictureTiming?.Menu;
            if (menu is null || _softwareSurface?.PresentationDrained != true) return default;
            if (menu.Pending is null) { menu.Painted(); RefreshDvdMenu(); }
            return _softwareSurface.PresentationDrained ? (menu.Pending, menu.Pending is null ? menu.Registers : null) : default;
        }
    }
    internal DvdMenuAction DvdMenuInput(DvdMenuInput input)
    {
        lock (_sync)
        {
            EnsureAlive(); var menu = _dvdSubpictureTiming?.Menu;
            if (menu is null || _softwareSurface?.PresentationDrained != true || menu.Pending is not null)
                return new(DvdMenuOutcome.Inactive, Reason: "DVD menu input needs the current painted frame.");
            if (input.Kind is DvdMenuInputKind.Point or DvdMenuInputKind.Click)
            {
                if (!GetClientRect(_window, out var bounds)) return new(DvdMenuOutcome.Inactive);
                var point = DvdMenuViewport.Point(bounds.Right, bounds.Bottom, PresentationAspectRatio, _rotation, checked((int)VideoSize.Height), input.X, input.Y);
                if (point is null) return new(DvdMenuOutcome.Inactive);
                input = input with { X = point.Value.X, Y = point.Value.Y };
            }
            var action = menu.Painted(input); RefreshDvdMenu(); return action;
        }
    }
    private void RefreshDvdMenu()
    {
        var menu = _dvdSubpictureTiming!.Menu!;
        if (menu.Pending is not null)
        {
            _dvdMenuWasPlaying = !IsPaused;
            Pause(); // No more frame ticks until this painted action is resolved.
        }
        var highlight = menu.Highlight;
        if (highlight == _dvdTimedHighlight) return;
        _dvdSubpicture = _dvdTimedFrame?.Colorize(_dvdSubpictureTiming.Palette, checked((int)VideoSize.Height), highlight);
        _dvdTimedHighlight = highlight; _softwareSurface!.SetSubpicture(_dvdSubpicture);
    }
    internal void CompleteDvdMenu(Guid ticket)
    {
        lock (_sync)
        {
            EnsureAlive(); (_dvdSubpictureTiming?.Menu ?? throw new InvalidOperationException("No active moving DVD menu.")).Complete(ticket);
            if (_dvdMenuWasPlaying && !_dvdHeld) Play(); _dvdMenuWasPlaying = false;
        }
    }
    private void StartDvdAfterFirstFrame()
    {
        if (!_dvdClockNeedsStart) return;
        _dvdClockNeedsStart = false;
        if (_dvdPlayWhenStarted) Play();
    }
    internal void SetDvdPaused(bool paused)
    {
        lock (_sync)
        {
            EnsureAlive(); _dvdPlayWhenStarted = !paused;
            if (_dvdHeld) return; // The session's still clock handles pause.
            if (_dvdSubpictureTiming?.Menu?.Pending is not null)
            { _dvdMenuWasPlaying = !paused; if (paused) Pause(); return; }
            if (paused) Pause(); else if (!_dvdClockNeedsStart) Play();
        }
    }

    internal void HoldDvdPicture()
    {
        lock (_sync)
        {
            EnsureAlive();
            if (_dvdHeld) return;
            if (_dvdSubpictureTiming is null || _dvdFrames == 0 || !GetBoolean(27) ||
                _softwareSurface?.PresentationDrained != true || _dvdSubpictureTiming.Menu?.Pending is not null)
                throw new InvalidOperationException("DVD hold requires native end and the final painted picture with no pending menu action.");
            var frame = _dvdSubpictureTiming.EnterHold();
            var highlight = _dvdSubpictureTiming.Menu?.Highlight;
            _dvdHeld = true; _dvdMenuWasPlaying = _dvdPlayWhenStarted = false;
            Pause();
            if (!ReferenceEquals(frame, _dvdTimedFrame) || highlight != _dvdTimedHighlight)
            {
                _dvdSubpicture = frame?.Colorize(_dvdSubpictureTiming.Palette, checked((int)VideoSize.Height), highlight);
                _dvdTimedFrame = frame; _dvdTimedHighlight = highlight;
                _softwareSurface!.SetSubpicture(_dvdSubpicture);
            }
        }
    }

    internal void SetDvdSubpicture(DvdSubpictureOverlay? overlay)
    {
        lock (_sync)
        {
            EnsureAlive();
            if (!_dvdStreamOpened || !_softwareVideo) throw new InvalidOperationException("DVD subpictures require the native DVD software stream.");
            _softwareSurface?.SetSubpicture(overlay);
            _dvdSubpicture = overlay;
            // Final NAV/held-menu composition deliberately freezes the media
            // clock. It must not be overwritten by a later frame callback.
            _dvdSubpictureTiming = null; _dvdTimedFrame = null; _dvdTimedFrameSet = false;
            _dvdTimedHighlight = null; _dvdMenuWasPlaying = false;
            _dvdClockNeedsStart = _dvdPlayWhenStarted = false;
        }
    }

    // A new engine per cell prevents late events from an older source from being
    // mistaken for completion of the next cell. The session owns stream lifetime.
    internal void OpenDvdStream(IStream stream, int audioStreams, int volume = 75, bool muted = false, bool autoPlay = true)
    {
        lock (_sync)
        {
            EnsureAlive();
            if (_source is not null || _dvdStreamOpened || !_softwareVideo) throw new InvalidOperationException("Use a fresh software-rendering engine for the DVD cell.");
            if (audioStreams is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(audioStreams));
            nint byteStream = 0, attributes = 0;
            var url = Marshal.StringToBSTR("noir-dvd-cell.mpg");
            try
            {
                Check(MFCreateMFByteStreamOnStream(stream, out byteStream));
                var iid = new Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3");
                Check(Marshal.QueryInterface(byteStream, in iid, out attributes));
                SetString(attributes, new Guid("FC358289-3CB6-460C-A424-B6681260375A"), "video/mpeg");
                _mpegSource = true; _mpegInventory = new(1, audioStreams); _dvdStreamOpened = true;
                _dvdSourceInitiallyPaused = !autoPlay;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(_extended, 45))(_extended, byteStream, url));
                SetVolume(volume); SetMuted(muted); if (autoPlay) Play();
            }
            finally
            {
                Marshal.FreeBSTR(url);
                if (attributes != 0) Marshal.Release(attributes);
                if (byteStream != 0) Marshal.Release(byteStream);
                GC.KeepAlive(stream);
            }
        }
    }
    private void RecordDvdFrame(long timestamp) { if (_dvdStreamOpened) { _dvdFrames++; _dvdFrameTime100ns = timestamp; } }
    internal bool DvdCanPresent
    {
        get { lock (_sync) { EnsureAlive(); return ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_engine, 14))(_engine) >= 2; } }
    }
    internal (bool Ended, long Frames, bool Painted) DvdPresentation
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                return (_dvdHeld || GetBoolean(27), _dvdFrames, _softwareSurface?.PresentationDrained == true);
            }
        }
    }
    internal static int ProbeDvdMpeg2()
    {
        using var apartment = new ComApartment();
        var cls = new Guid("2D709E52-123F-49B5-9CBC-9AF5CDE28FB9");
        var iid = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
        var result = CoCreateInstance(in cls, 0, 1, in iid, out var decoder);
        if (decoder != 0) Marshal.Release(decoder);
        return result;
    }
}
