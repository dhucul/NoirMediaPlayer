using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

/// <summary>A first-party Win32 presenter for Media Engine's software-decoded WIC frames.</summary>
internal sealed unsafe class WindowsSoftwareVideoSurface : IDisposable
{
    private static readonly ConcurrentDictionary<nint, WindowsSoftwareVideoSurface> Surfaces = new();
    private readonly object _sync = new();
    private readonly nint _window;
    private readonly int _width, _height;
    private readonly bool _retainBaseFrame;
    private nint _dc, _bitmap, _previous, _pixels;
    private nint _retainedWic;
    private DvdSubpictureOverlay? _subpicture;
    private bool _hasFrame;
    private long _frameVersion, _paintedVersion;
    internal bool PresentationDrained { get { lock (_sync) return _hasFrame && _frameVersion == _paintedVersion && _error >= 0; } }
    private int _rotation;
    private double _aspect;
    private int _error;

    internal WindowsSoftwareVideoSurface(nint window, int width, int height, bool retainBaseFrame = false)
    {
        _window = window;
        _width = width;
        _height = height;
        _retainBaseFrame = retainBaseFrame;
        try
        {
            // A DC made with NULL belongs to its creating thread and becomes
            // invalid if that thread exits. Playback workers are thread-pool jobs.
            var windowDc = GetDC(window);
            if (windowDc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { _dc = CreateCompatibleDC(windowDc); }
            finally { ReleaseDC(window, windowDc); }
            if (_dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            _bitmap = CreateDIBSection(_dc, &info, 0, out _pixels, 0, 0);
            if (_bitmap == 0 || _pixels == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previous = SelectObject(_dc, _bitmap);
            if (_previous == 0 || _previous == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Surfaces.TryAdd(window, this)) throw new InvalidOperationException("The video window already has a software presenter.");
        }
        catch { Dispose(); throw; }
    }

    internal void Configure(int rotation, double aspect)
    {
        lock (_sync) { _rotation = rotation; _aspect = aspect; }
        InvalidateRect(_window, 0, false);
    }

    internal void Present(nint wicBitmap, DvdSubpictureOverlay? subpicture = null, bool replaceSubpicture = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_dc == 0, this);
            if (replaceSubpicture)
            {
                if (!_retainBaseFrame) throw new InvalidOperationException("Subpicture composition requires a retained native DVD frame.");
                _subpicture = subpicture;
            }
            // IWICBitmapSource::CopyPixels writes directly to the retained top-down
            // DIB. Paint never observes a partially copied frame.
            CopyFrame(wicBitmap);
            if (_retainBaseFrame && _retainedWic != wicBitmap)
            {
                Marshal.AddRef(wicBitmap);
                if (_retainedWic != 0) Marshal.Release(_retainedWic);
                _retainedWic = wicBitmap;
            }
            _subpicture?.Composite(new Span<byte>((void*)_pixels, _width * _height * 4), _width, _height);
            _hasFrame = true;
            _frameVersion++;
        }
        InvalidateRect(_window, 0, false);
    }

    // Called under the engine lock, which also serializes writes to the WIC
    // bitmap. Recopy unmodified pixels, never blend over an earlier highlight.
    internal void SetSubpicture(DvdSubpictureOverlay? subpicture)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_dc == 0, this);
            if (!_retainBaseFrame) throw new InvalidOperationException("Subpicture composition requires a retained native DVD frame.");
            _subpicture = subpicture;
            if (_retainedWic != 0)
            {
                CopyFrame(_retainedWic);
                _subpicture?.Composite(new Span<byte>((void*)_pixels, _width * _height * 4), _width, _height);
                _frameVersion++;
            }
        }
        InvalidateRect(_window, 0, false);
    }
    private void CopyFrame(nint wicBitmap)
    {
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, nint, int>)(*(nint**)wicBitmap)[7])(
            wicBitmap, 0, (uint)_width * 4, (uint)(_width * _height * 4), _pixels);
        Marshal.ThrowExceptionForHR(result);
    }

    internal void CheckPresentation() => Marshal.ThrowExceptionForHR(Volatile.Read(ref _error));

    internal static bool TryPaint(nint window, int message, nint dc)
    {
        if (message is not (0x000F or 0x0014 or 0x0318) || !Surfaces.TryGetValue(window, out var surface)) return false;
        if (message == 0x0014) return true; // Paint clears the complete client area itself.
        if (message == 0x0318) surface.Paint(dc); // WM_PRINTCLIENT, including PrintWindow.
        else
        {
            var paintDc = BeginPaint(window, out var paint);
            try { surface.Paint(paintDc); }
            finally { EndPaint(window, in paint); }
        }
        return true;
    }

    private void Paint(nint dc)
    {
        lock (_sync)
        {
            if (_dc == 0 || dc == 0 || !GetClientRect(_window, out var bounds)) return;
            FillRect(dc, in bounds, GetStockObject(4)); // BLACK_BRUSH
            if (!_hasFrame || _aspect <= 0) return;
            var width = (int)Math.Round(Math.Min(bounds.Right, bounds.Bottom * _aspect));
            var height = (int)Math.Round(width / _aspect);
            if (width < 1 || height < 1) return; // A tiny/hidden viewport is not a presentation failure.
            var left = (bounds.Right - width) / 2;
            var top = (bounds.Bottom - height) / 2;
            var right = left + width;
            var bottom = top + height;
            // GDI maps the source's top-left, top-right and bottom-left into the
            // complete fitted picture. Rotation never crops the source or captions.
            var points = stackalloc Point[3];
            switch (_rotation)
            {
                case 90: points[0] = new(right, top); points[1] = new(right, bottom); points[2] = new(left, top); break;
                case 180: points[0] = new(right, bottom); points[1] = new(left, bottom); points[2] = new(right, top); break;
                case 270: points[0] = new(left, bottom); points[1] = new(left, top); points[2] = new(right, bottom); break;
                default: points[0] = new(left, top); points[1] = new(right, top); points[2] = new(left, bottom); break;
            }
            var saved = SaveDC(dc);
            try
            {
                SetStretchBltMode(dc, 4); // HALFTONE
                SetBrushOrgEx(dc, 0, 0, 0);
                if (!PlgBlt(dc, points, _dc, 0, 0, _width, _height, 0, 0, 0))
                {
                    var error = Marshal.GetHRForLastWin32Error();
                    Volatile.Write(ref _error, error < 0 ? error : unchecked((int)0x80004005));
                }
                GdiFlush(); // Complete use of the DIB before the worker writes it again.
                if (_error >= 0) _paintedVersion = _frameVersion;
            }
            finally { if (saved != 0) RestoreDC(dc, saved); }
        }
    }

    public void Dispose()
    {
        Surfaces.TryRemove(new KeyValuePair<nint, WindowsSoftwareVideoSurface>(_window, this));
        lock (_sync)
        {
            if (_retainedWic != 0) Marshal.Release(_retainedWic);
            _retainedWic = 0; _subpicture = null;
            if (_dc != 0 && _previous != 0 && _previous != -1) SelectObject(_dc, _previous);
            if (_bitmap != 0) DeleteObject(_bitmap);
            if (_dc != 0) DeleteDC(_dc);
            _dc = _bitmap = _previous = _pixels = 0;
        }
        InvalidateRect(_window, 0, true);
    }

    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public nint Dc; public int Erase; public Rect Paint; public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, BitmapInfo* info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
    [DllImport("gdi32.dll")] private static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool PlgBlt(nint dc, Point* points, nint source, int x, int y, int width, int height, nint mask, int maskX, int maskY);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, in Rect rect, nint brush);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint window, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint window, out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint window, in PaintStruct paint);
}
