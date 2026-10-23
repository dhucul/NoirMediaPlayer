using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal sealed unsafe partial class WindowsMediaEngine
{
    // Direct Windows SDK ABIs: no graphics, media, or COM wrapper package is used.
    private nint _compositionDevice, _compositionTarget, _compositionVisual, _compositionSurface;
    private nint _videoDevice, _videoDeviceManager;
    private int _rotation;
    private double? _aspectRatio;

    internal static bool SupportsAspectRatio(string? value) => string.IsNullOrWhiteSpace(value) ||
        value is "16:9" or "16:10" or "4:3" or "1:1" or "21:9";

    internal void SetAspectRatio(string? value)
    {
        if (!SupportsAspectRatio(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_sync)
        {
            EnsureAlive();
            _aspectRatio = value switch
            {
                "16:9" => 16d / 9, "16:10" => 1.6, "4:3" => 4d / 3,
                "1:1" => 1, "21:9" => 21d / 9, _ => null
            };
            UpdateVideo(force: true);
        }
    }

    internal double PresentationAspectRatio
    {
        get
        {
            lock (_sync)
            {
                if (_aspectRatio is { } forced) return forced;
                var aspect = DisplayAspectRatio;
                return _rotation is 90 or 270 && aspect > 0 ? 1 / aspect : aspect;
            }
        }
    }

    private void InitializeCompositionDevice(nint attributes)
    {
        Check(D3D11CreateDevice(0, 1, 0, 0x820, 0, 0, 7, out _videoDevice, 0, 0));
        var multithreadId = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
        Check(Marshal.QueryInterface(_videoDevice, in multithreadId, out var multithread));
        try { _ = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(multithread, 5))(multithread, 1); }
        finally { Marshal.Release(multithread); }
        Check(MFCreateDXGIDeviceManager(out var token, out _videoDeviceManager));
        Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(_videoDeviceManager, 7))(_videoDeviceManager, _videoDevice, token));
        var managerKey = new Guid("065702DA-1094-486D-8617-EE7CC4EE4648");
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(attributes, 27))(attributes, &managerKey, _videoDeviceManager));
    }

    private void ReleaseCompositionDevice()
    {
        if (_videoDeviceManager != 0) Marshal.Release(_videoDeviceManager);
        if (_videoDevice != 0) Marshal.Release(_videoDevice);
        _videoDevice = _videoDeviceManager = 0;
    }

    internal void SetRotation(int rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation));
        lock (_sync)
        {
            EnsureAlive();
            _rotation = rotation;
            // Before metadata arrives, retain the setting for the first rendered frame.
            // Audio-only playback has no picture to transform and keeps its transport.
            UpdateVideo(force: true);
        }
    }

    private void EnsureComposition()
    {
        if (_compositionDevice != 0) return;
        var deviceId = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        nint device = 0, target = 0, visual = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_extended, 71))(_extended, 1)); // EnableWindowlessSwapchainMode
            Check(DCompositionCreateDevice(0, in deviceId, out device));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(device, 6))(device, _window, 1, &target));
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(device, 7))(device, &visual));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(target, 3))(target, visual));
            _compositionDevice = device;
            _compositionTarget = target;
            _compositionVisual = visual;
            device = target = visual = 0;
        }
        finally
        {
            if (visual != 0) Marshal.Release(visual);
            if (target != 0) Marshal.Release(target);
            if (device != 0) Marshal.Release(device);
        }
    }

    private NativeRect AspectSurface(uint aspectWidth, uint aspectHeight)
    {
        var a = aspectWidth;
        var b = aspectHeight;
        while (b != 0) (a, b) = (b, a % b);
        var numerator = aspectWidth / a;
        var denominator = aspectHeight / a;
        var (nativeWidth, nativeHeight) = VideoSize;
        if (nativeWidth == 0 || nativeHeight == 0) throw new NotSupportedException("The native video dimensions are unavailable.");
        var scale = Math.Ceiling(Math.Max((double)nativeWidth / numerator, (double)nativeHeight / denominator));
        var width = numerator * scale;
        var height = denominator * scale;
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || width * height * 4 > 256 * 1024 * 1024)
            throw new NotSupportedException("The video aspect surface exceeds the native presentation limit.");
        return new NativeRect(0, 0, (int)width, (int)height);
    }

    private void UpdateComposition(NativeRect bounds, NativeRect destination)
    {
        nint handle = 0, surface = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_extended, 72))(_extended, &handle), "GetVideoSwapchainHandle");
        if (handle == 0) throw new InvalidOperationException("Windows did not provide a video surface.");
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_compositionDevice, 10))(_compositionDevice, handle, &surface), "CreateSurfaceFromHandle");
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_compositionVisual, 15))(_compositionVisual, surface), "SetContent");
            if (_compositionSurface != 0) Marshal.Release(_compositionSurface);
            _compositionSurface = surface;
            surface = 0;
        }
        finally
        {
            if (surface != 0) Marshal.Release(surface);
            CloseHandle(handle);
        }
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        float scaleX = 1, scaleY = 1;
        if (_aspectRatio is { } forced)
        {
            // The override surface has exactly the source DAR, so Windows fills it
            // without borders. Stretch the full picture to the final requested DAR,
            // then center it in the client area; captions are composed separately.
            double pictureWidth = destination.Right, pictureHeight = destination.Bottom;
            if (_rotation is 90 or 270) (pictureWidth, pictureHeight) = (pictureHeight, pictureWidth);
            var destinationWidth = Math.Min(width, height * forced);
            var destinationHeight = destinationWidth / forced;
            scaleX = (float)(destinationWidth / pictureWidth);
            scaleY = (float)(destinationHeight / pictureHeight);
        }
        var matrix = _rotation switch
        {
            90 => new CompositionMatrix(0, scaleY, -scaleX, 0, (width + destination.Bottom * scaleX) / 2, (height - destination.Right * scaleY) / 2),
            180 => new CompositionMatrix(-scaleX, 0, 0, -scaleY, (width + destination.Right * scaleX) / 2, (height + destination.Bottom * scaleY) / 2),
            270 => new CompositionMatrix(0, -scaleY, scaleX, 0, (width - destination.Bottom * scaleX) / 2, (height + destination.Right * scaleY) / 2),
            _ => new CompositionMatrix(scaleX, 0, 0, scaleY, (width - destination.Right * scaleX) / 2, (height - destination.Bottom * scaleY) / 2)
        };
        // MSVC's COM ABI places the matrix overload after the object overload.
        Check(((delegate* unmanaged[Stdcall]<nint, CompositionMatrix*, int>)Slot(_compositionVisual, 8))(_compositionVisual, &matrix), "SetTransform");
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_compositionDevice, 3))(_compositionDevice), "Commit");
    }

    private void ReleaseComposition()
    {
        if (_compositionTarget != 0) _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_compositionTarget, 3))(_compositionTarget, 0);
        if (_compositionDevice != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_compositionDevice, 3))(_compositionDevice);
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_compositionDevice, 4))(_compositionDevice);
        }
        if (_compositionSurface != 0) Marshal.Release(_compositionSurface);
        if (_compositionVisual != 0) Marshal.Release(_compositionVisual);
        if (_compositionTarget != 0) Marshal.Release(_compositionTarget);
        if (_compositionDevice != 0) Marshal.Release(_compositionDevice);
        _compositionSurface = _compositionVisual = _compositionTarget = _compositionDevice = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CompositionMatrix(float M11, float M12, float M21, float M22, float Dx, float Dy);

    [DllImport("dcomp.dll", ExactSpelling = true)]
    private static extern int DCompositionCreateDevice(nint dxgiDevice, in Guid interfaceId, out nint device);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint featureLevels, uint levelCount, uint sdkVersion, out nint device, nint selectedLevel, nint context);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGIDeviceManager(out uint token, out nint manager);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
