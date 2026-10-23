using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal sealed unsafe partial class WindowsMediaEngine
{
    private readonly bool _softwareVideo;
    private nint _softwareFactory, _softwareBitmap;
    private WindowsSoftwareVideoSurface? _softwareSurface;
    private System.Threading.Timer? _softwareTimer;
    private NativeRect _softwareSize;
    private int _softwareError;

    internal bool UsesSoftwareVideo => _softwareVideo;

    private void UpdateSoftwareVideo()
    {
        uint width = 0, height = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(_engine, 41))(_engine, &width, &height));
        if (width == 0 || height == 0) throw new NotSupportedException("The software video's display aspect is unavailable.");
        var size = AspectSurface(width, height);
        // Bound both WIC's frame and the presenter's DIB to 64 MiB each. There is
        // only one retained frame and no queue of decoded images or dispatcher work.
        if ((long)size.Right * size.Bottom * 4 > 64 * 1024 * 1024)
            throw new NotSupportedException("The software video frame exceeds the native presentation limit.");
        if (_softwareBitmap == 0 || size != _softwareSize)
        {
            if (_softwareFactory == 0)
            {
                var classId = new Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A");
                var interfaceId = new Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
                Check(CoCreateInstance(in classId, 0, 1, in interfaceId, out _softwareFactory));
            }
            nint bitmap = 0;
            var pixelFormat = new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F"); // WIC 32bppBGRA
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, Guid*, uint, nint*, int>)Slot(_softwareFactory, 17))(
                _softwareFactory, (uint)size.Right, (uint)size.Bottom, &pixelFormat, 1, &bitmap));
            if (_softwareBitmap != 0) Marshal.Release(_softwareBitmap);
            _softwareBitmap = bitmap;
            _softwareSurface?.Dispose();
            _softwareSurface = null;
            _softwareSurface = new WindowsSoftwareVideoSurface(_window, size.Right, size.Bottom, retainBaseFrame: _dvdStreamOpened);
            if (_dvdSubpicture is not null) _softwareSurface.SetSubpicture(_dvdSubpicture);
            _softwareSize = size;
        }
        _softwareSurface!.Configure(_rotation, PresentationAspectRatio);
        _softwareTimer ??= new System.Threading.Timer(_ => PumpSoftwareVideo(), null, 0, 16);
    }

    private void PumpSoftwareVideo()
    {
        // A slow frame never accumulates callbacks or delays commands/shutdown.
        if (!Monitor.TryEnter(_sync)) return;
        var failed = false;
        try
        {
            if (_engine == 0 || _softwareTimer is null || _softwareSurface is null) return;
            if (_dvdHeld || _dvdSubpictureTiming?.Menu?.Pending is not null) return;
            _softwareSurface.CheckPresentation();
            using var apartment = new ComApartment();
            long timestamp = 0;
            var result = ((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(_engine, 44))(_engine, &timestamp);
            Check(result, "OnVideoStreamTick");
            if (result != 0) return; // S_FALSE: retain the last frame while paused or buffering.
            var destination = _softwareSize;
            uint black = 0xFF000000;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, NativeRect*, uint*, int>)Slot(_engine, 43))(
                _engine, _softwareBitmap, 0, &destination, &black), "TransferVideoFrame");
            PrepareDvdSubpicture(timestamp);
            _softwareSurface.Present(_softwareBitmap, _dvdSubpicture, replaceSubpicture: _dvdStreamOpened);
            RecordDvdFrame(timestamp);
            StartDvdAfterFirstFrame();
        }
        catch (Exception exception)
        {
            if (_dvdStreamOpened) _dvdFrameFailure = exception.Message;
            System.Diagnostics.Debug.WriteLine(exception);
            _softwareError = exception.HResult;
            _softwareTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            failed = true;
        }
        finally { Monitor.Exit(_sync); }
        // Dispatch through the existing generation-scoped fallback and restoration.
        if (failed) _notify.EventNotify(1014, 0, 0);
    }

    private void ReleaseSoftwareVideo()
    {
        _softwareTimer?.Dispose();
        _softwareTimer = null;
        _softwareSurface?.Dispose();
        _softwareSurface = null;
        if (_softwareBitmap != 0) Marshal.Release(_softwareBitmap);
        if (_softwareFactory != 0) Marshal.Release(_softwareFactory);
        _softwareBitmap = _softwareFactory = 0;
        _dvdSubpicture = null;
        _dvdSubpictureTiming = null; _dvdTimedFrame = null; _dvdTimedFrameSet = false; _dvdFrameTime100ns = null;
        _dvdTimedHighlight = null; _dvdMenuWasPlaying = _dvdHeld = false;
        _dvdClockNeedsStart = _dvdPlayWhenStarted = false;
        _dvdSourceInitiallyPaused = false; _dvdFrameFailure = null;
    }
}
