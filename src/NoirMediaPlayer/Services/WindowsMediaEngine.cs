using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace NoirMediaPlayer.Services;

// ABI declarations below follow mfmediaengine.h and mfobjects.h in the Windows SDK.
// No codec, rendering library, or managed COM wrapper is bundled by this backend.
[ComVisible(true), Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsMediaEngineNotify
{
    [PreserveSig]
    int EventNotify(uint mediaEvent, nuint parameter1, uint parameter2);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class WindowsMediaEngineNotify(Action<uint> notify) : IWindowsMediaEngineNotify
{
    public int EventNotify(uint mediaEvent, nuint parameter1, uint parameter2)
    {
        // Stable-state requests must be signaled on the callback thread, never dispatched.
        if (mediaEvent == 1008) SetEvent((nint)parameter1);
        else
        {
            try { notify(mediaEvent); }
            catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
        }
        return 0;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint handle);
}

/// <summary>
/// Windows Media Foundation's IMFMediaEngine with DirectComposition, software video, or audio-only output. COM pointers are
/// owned here and protected against concurrent reads, commands, and shutdown.
/// </summary>
internal sealed unsafe partial class WindowsMediaEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly WindowsMediaEngineNotify _notify;
    private readonly nint _window;
    private readonly bool _requireAudioOnly;
    private nint _engine;
    private nint _extended;
    private nint _timedText;
    private bool _started;
    private NativeRect _lastDestination;
    private NativeRect _lastWindowBounds;
    private string? _source;
    private string? _sourceContentType;
    private bool? _simpleCaptionDefaults;
    private MpegStreamInventory? _mpegInventory;
    private bool _mpegSource;
    private long? _settledChapterTime;

    internal WindowsMediaEngine(nint window, Action<uint> notify, bool requireAudioOnly = false, bool softwareVideo = false)
    {
        _window = window;
        _requireAudioOnly = window == 0 || requireAudioOnly;
        _softwareVideo = softwareVideo && !_requireAudioOnly;
        _notify = new WindowsMediaEngineNotify(notify);
        nint attributes = 0, factory = 0, callback = 0;
        using var apartment = new ComApartment();
        try
        {
            Check(MFStartup(0x20070, 0));
            _started = true;
            Check(MFCreateAttributes(out attributes, 2));
            callback = Marshal.GetComInterfaceForObject(_notify, typeof(IWindowsMediaEngineNotify));
            var callbackKey = new Guid("C60381B8-83A4-41F8-A3D0-DE05076849A9");
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(attributes, 27))(attributes, &callbackKey, callback));
            if (_softwareVideo)
            {
                // Frame-server mode without a DXGI manager guarantees software decoding
                // and processing. A playback HWND would select a different engine mode.
                var formatKey = new Guid("5066893C-8CF9-42BC-8B8A-472212E52726");
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &formatKey, 87)); // B8G8R8A8_UNORM
            }
            else if (!_requireAudioOnly)
            {
                try { InitializeCompositionDevice(attributes); }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(exception);
                    ReleaseCompositionDevice();
                }
            }
            if (window != 0 && _videoDeviceManager == 0 && !_softwareVideo && !_requireAudioOnly)
            {
                // Keep the established Windows renderer when the adapter cannot
                // supply a composition device. Rotation can still hand off to VLC.
                var windowKey = new Guid("D988879B-67C9-4D92-BAA7-6EADD446039D");
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &windowKey, (ulong)window));
            }
            var classId = new Guid("B44392DA-499B-446B-A4CB-005FEAD0E6D5");
            var factoryId = new Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93");
            Check(CoCreateInstance(in classId, 0, 1, in factoryId, out factory));
            nint engine = 0;
            // Disable per-process third-party plug-ins; OS-installed codecs remain available.
            // Explicit audio-only clients still reject video through the full inventory.
            var flags = _requireAudioOnly ? 0x11u : 0x10u;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint*, int>)Slot(factory, 3))(factory, flags, attributes, &engine));
            _engine = engine;
            var extendedId = new Guid("83015EAD-B1E6-40D0-A98A-37145FFE1AD1");
            Check(Marshal.QueryInterface(_engine, in extendedId, out _extended));
            if (window != 0)
            {
                var service = new Guid("805EA411-92E0-4E59-9B6E-5C7D7915E64F");
                var timedTextId = new Guid("1F2A94C9-A3DF-430D-9D0F-ACD85DDC29AF");
                Check(MFGetService(_engine, in service, in timedTextId, out _timedText));
                // Media Engine can remove captions from its A/V stream inventory. Enable
                // in-band discovery and inspect the timed-text service before accepting it.
                Check(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_timedText, 15))(_timedText, 1));
                InitializeCaptionNotifications();
            }
        }
        catch { Dispose(); throw; }
        finally
        {
            if (callback != 0) Marshal.Release(callback);
            if (factory != 0) Marshal.Release(factory);
            if (attributes != 0) Marshal.Release(attributes);
        }
    }

    internal void Open(string source, int volume, bool muted, float rate, string? contentType = null, bool autoPlay = true)
    {
        lock (_sync)
        {
            EnsureAlive();
            _source = source;
            _sourceContentType = contentType;
            _mpegSource = MpegStreamInspection.SupportsExtension(source);
            _mpegInventory = _mpegSource ? MpegStreamInspection.TryRead(source) : null;
            _simpleCaptionDefaults = System.IO.File.Exists(source) && System.IO.Path.GetExtension(source).Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                ? MatroskaChapters.HasSimpleCaptionDefaults(source) : null;
            // SetSource starts loading asynchronously. Do not issue a second Load,
            // which can replace that load while its metadata callback is starting playback.
            if (!autoPlay) Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(_engine, 10))(_engine, 3)); // PRELOAD_METADATA
            var url = Marshal.StringToBSTR(new Uri(source).AbsoluteUri);
            try { Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_engine, 6))(_engine, url)); }
            finally { Marshal.FreeBSTR(url); }
            SetVolume(volume);
            SetMuted(muted);
            SetRate(rate);
            if (autoPlay) Play();
        }
    }

    internal long Time
    {
        get { lock (_sync) { EnsureAlive(); return Milliseconds(((delegate* unmanaged[Stdcall]<nint, double>)Slot(_engine, 16))(_engine)); } }
        set
        {
            lock (_sync)
            {
                EnsureAlive();
                // This Windows source accepts seeks but can land on a later GOP,
                // several seconds past the target. Handoff before changing its clock.
                if (_mpegSource) throw new NotSupportedException("Precise MPEG seeking requires compatibility playback.");
                Check(((delegate* unmanaged[Stdcall]<nint, double, int>)Slot(_engine, 17))(_engine, Math.Max(0, value) / 1000d));
                _settledChapterTime = null;
            }
        }
    }

    internal long ChapterTime
    {
        get
        {
            lock (_sync)
            {
                var time = Time;
                // Converting a double clock to milliseconds can land one millisecond
                // before an exact chapter marker. Only correct our confirmed chapter
                // seek; an explicit seek clears this hint and keeps exact boundaries.
                return _settledChapterTime is { } target && Math.Abs(time - target) <= 1 ? target : time;
            }
        }
    }

    internal void SeekChapter(long target)
    {
        Time = target;
        // SetCurrentTime queues a seek: the next serialized command can otherwise
        // observe the previous position and navigate to the same chapter again.
        // Wait off the dispatcher and release our lock between native clock reads.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(2))
        {
            lock (_sync)
            {
                EnsureAlive();
                var tolerance = IsPaused ? 1 : 150; // Playing clocks advance after the seek completes.
                if (!GetBoolean(15) && Math.Abs(Time - target) <= tolerance) // IsSeeking
                {
                    _settledChapterTime = target;
                    return;
                }
            }
            Thread.Sleep(5);
        }
        throw new TimeoutException("Windows did not confirm the chapter seek.");
    }

    internal long Length
    {
        get { lock (_sync) { EnsureAlive(); return Milliseconds(((delegate* unmanaged[Stdcall]<nint, double>)Slot(_engine, 19))(_engine)); } }
    }

    internal bool IsSeekable
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                nint range = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_engine, 26))(_engine, &range));
                    return range != 0 && ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(range, 3))(range) > 0;
                }
                finally { if (range != 0) Marshal.Release(range); }
            }
        }
    }

    internal bool HasVideo { get { lock (_sync) { EnsureAlive(); return GetBoolean(38); } } }
    internal bool HasAudio { get { lock (_sync) { EnsureAlive(); return GetBoolean(39); } } }
    internal bool IsPaused { get { lock (_sync) { EnsureAlive(); return GetBoolean(20); } } }

    internal double DisplayAspectRatio
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                uint width = 0, height = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(_engine, 41))(_engine, &width, &height));
                return height == 0 ? 0 : (double)width / height;
            }
        }
    }

    internal int ErrorCode
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                if (_softwareError < 0) return _softwareError;
                nint error = 0;
                try
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_engine, 3))(_engine, &error));
                    return error == 0 ? 0 : ((delegate* unmanaged[Stdcall]<nint, int>)Slot(error, 4))(error);
                }
                finally { if (error != 0) Marshal.Release(error); }
            }
        }
    }

    internal (uint Width, uint Height) VideoSize
    {
        get
        {
            lock (_sync)
            {
                EnsureAlive();
                uint width = 0, height = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(_engine, 40))(_engine, &width, &height));
                return (width, height);
            }
        }
    }

    internal void Play() { lock (_sync) { EnsureAlive(); Check(Call(32)); } }
    internal void Pause() { lock (_sync) { EnsureAlive(); Check(Call(33)); } }
    internal void SetMuted(bool value) { lock (_sync) { EnsureAlive(); _muted = value; ApplyAudioMute(); } }
    internal void SetVolume(int value) { lock (_sync) { EnsureAlive(); Check(((delegate* unmanaged[Stdcall]<nint, double, int>)Slot(_engine, 37))(_engine, Math.Clamp(value, 0, 100) / 100d)); } }
    internal void SetRate(float value) { lock (_sync) { EnsureAlive(); Check(((delegate* unmanaged[Stdcall]<nint, double, int>)Slot(_engine, 24))(_engine, value)); } }

    internal void UpdateVideo(bool force = false)
    {
        lock (_sync)
        {
            EnsureAlive();
            if (!GetBoolean(38) || !GetClientRect(_window, out var bounds)) return;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return;
            if (_softwareVideo) { UpdateSoftwareVideo(); return; }
            if (_videoDeviceManager != 0) EnsureComposition();
            else if (_rotation != 0 || _aspectRatio is not null)
                throw new NotSupportedException("Windows composition is unavailable for the requested video presentation.");
            uint aspectWidth = 0, aspectHeight = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(_engine, 41))(_engine, &aspectWidth, &aspectHeight));
            if (aspectWidth == 0 || aspectHeight == 0) throw new InvalidOperationException("The source display aspect ratio is unavailable.");
            // Windows corrects source orientation/PAR and letterboxes into this surface.
            // A quarter turn swaps the surface dimensions; DirectComposition rotates
            // the complete surface, keeping both picture and borders inside the window.
            var destination = bounds;
            // Ratio overrides start with a full source-aspect surface. Scaling a
            // letterboxed client surface would magnify rounded border edges.
            if (_aspectRatio is not null) destination = AspectSurface(aspectWidth, aspectHeight);
            else if (_compositionDevice != 0 && _rotation is 90 or 270)
                destination = new NativeRect(0, 0, bounds.Bottom, bounds.Right);
            if (!force && destination.Equals(_lastDestination) && bounds.Equals(_lastWindowBounds)) return;
            uint black = 0xFF000000;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, NativeRect*, uint*, int>)Slot(_extended, 47))(_extended, 0, &destination, &black));
            if (_compositionDevice != 0) UpdateComposition(bounds, destination);
            _lastDestination = destination;
            _lastWindowBounds = bounds;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _captionDisposed = true;
            ClearCaption();
            lock (_captionSync) _recentCaptionCues.Clear();
            if (_timedText != 0) _ = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_timedText, 3))(_timedText, 0);
            ReleaseSoftwareVideo();
            if (_engine != 0)
            {
                ReleaseComposition();
                try { Call(42); }
                finally
                {
                    if (_timedText != 0) Marshal.Release(_timedText);
                    _timedText = 0;
                    if (_extended != 0) Marshal.Release(_extended);
                    Marshal.Release(_engine);
                    _extended = _engine = 0;
                }
            }
            ReleaseCompositionDevice();
            if (_started) { MFShutdown(); _started = false; }
            GC.KeepAlive(_notify);
            GC.KeepAlive(_captionNotify);
        }
    }

    private int Call(int index) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_engine, index))(_engine);
    private bool GetBoolean(int index) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_engine, index))(_engine) != 0;
    private void EnsureAlive() => ObjectDisposedException.ThrowIf(_engine == 0, this);
    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    private static void Check(int result, [CallerMemberName] string operation = "")
    {
        if (result < 0) throw new COMException($"{operation}: {Marshal.GetExceptionForHR(result)?.Message}", result);
    }
    private static long Milliseconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? (long)Math.Min(seconds * 1000, long.MaxValue) : 0;

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    private readonly struct ComApartment : IDisposable
    {
        private readonly bool _owns;
        public ComApartment() { var result = CoInitializeEx(0, 0); _owns = result >= 0; if (result < 0 && result != unchecked((int)0x80010106)) Check(result); }
        public void Dispose() { if (_owns) CoUninitialize(); }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateAttributes(out nint attributes, uint initialSize);
    [DllImport("mf.dll", ExactSpelling = true)] private static extern int MFGetService(nint instance, in Guid service, in Guid interfaceId, out nint result);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rectangle);
}
