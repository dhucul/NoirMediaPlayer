using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NoirMediaPlayer.Services;

/// <summary>Reads ASF markers through Windows' metadata APIs without changing the source.</summary>
internal static unsafe class WindowsAsfChapters
{
    internal static bool SupportsExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".wmv" or ".wma" or ".asf";

    internal static bool SupportsHttpSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        (SupportsExtension(uri.AbsolutePath) || WindowsPlaybackPolicy.IsWebEndpoint(Path.GetExtension(uri.AbsolutePath)));

    internal static NativeChapterMap? TryReadHttp(string source)
    {
        if (!SupportsHttpSource(source)) return null;
        if (!SupportsExtension(new Uri(source).AbsolutePath))
            return WindowsHttpMediaProbe.Inspect(source, new WindowsPlaybackPlan([]), asfOnly: true)?.Chapters;
        try
        {
            using var input = new WindowsSnapshotHttpStream(source, TimeSpan.FromSeconds(2), byteLimit: 8 * 1024 * 1024);
            return TryRead(input);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            Debug.WriteLine(exception);
            return null;
        }
    }

    internal static NativeChapterMap? TryRead(string path)
    {
        nint editor = 0, editor2 = 0, header = 0;
        var apartment = CoInitializeEx(0, 0);
        var opened = false;
        try
        {
            if (apartment < 0 && apartment != unchecked((int)0x80010106)) Check(apartment);
            if (!File.Exists(path)) return null;
            var clock = Stopwatch.StartNew();
            Check(WMCreateEditor(out editor));
            var editorId = new Guid("203CFFE3-2E18-4FDF-B59D-6E71530534CF");
            Check(Marshal.QueryInterface(editor, in editorId, out editor2));
            // IWMMetadataEditor::Open requests write access. OpenEx is necessary
            // even for read-only files; never call Flush on this playback path.
            fixed (char* filename = path)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, uint, int>)Slot(editor2, 6))(
                    editor2, filename, 0x80000000, 1)); // GENERIC_READ, FILE_SHARE_READ
            opened = true;
            var headerId = new Guid("96406BDA-2B2B-11D3-B36B-00C04F6108FF");
            Check(Marshal.QueryInterface(editor, in headerId, out header));
            return ReadMarkers(header, clock);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException or OverflowException)
        {
            Debug.WriteLine(exception);
            return null;
        }
        finally
        {
            if (opened) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(editor, 4))(editor);
            if (header != 0) Marshal.Release(header);
            if (editor2 != 0) Marshal.Release(editor2);
            if (editor != 0) Marshal.Release(editor);
            if (apartment >= 0) CoUninitialize();
        }
    }

    internal static NativeChapterMap? TryRead(IStream stream)
    {
        nint reader = 0, header = 0, input = 0;
        var apartment = CoInitializeEx(0, 0);
        var opened = false;
        try
        {
            if (apartment < 0 && apartment != unchecked((int)0x80010106)) Check(apartment);
            var clock = Stopwatch.StartNew();
            Check(WMCreateSyncReader(0, 0, out reader));
            input = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            // Inspect metadata only; never request samples, invoke license APIs,
            // edit metadata or save a temporary media file.
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(reader, 23))(reader, input));
            opened = true;
            var headerId = new Guid("96406BDA-2B2B-11D3-B36B-00C04F6108FF");
            Check(Marshal.QueryInterface(reader, in headerId, out header));
            return ReadMarkers(header, clock);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException or OverflowException or OperationCanceledException or
            HttpRequestException or NotSupportedException)
        {
            Debug.WriteLine(exception);
            return null;
        }
        finally
        {
            if (opened) _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(reader, 4))(reader);
            if (header != 0) Marshal.Release(header);
            if (reader != 0) Marshal.Release(reader);
            if (input != 0) Marshal.Release(input);
            if (apartment >= 0) CoUninitialize();
            GC.KeepAlive(stream);
        }
    }

    private static NativeChapterMap? ReadMarkers(nint header, Stopwatch clock)
    {
        var duration = Attribute(header, "Duration", 4, 8);
        if (duration == 0 || duration > long.MaxValue || Attribute(header, "Is_Protected", 3, 4) != 0 ||
            Attribute(header, "Broadcast", 3, 4) != 0 || Attribute(header, "Seekable", 3, 4) == 0) return null;

        ushort count = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, ushort*, int>)Slot(header, 7))(header, &count));
        if (count > 4096) return null;
        var starts = new long[count];
        for (ushort index = 0; index < count; index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
            ushort length = 0;
            ulong time = 0;
            // The size probe may report BUFFER_TOO_SMALL. The bounded second
            // call must succeed before its returned marker can be admitted.
            _ = ((delegate* unmanaged[Stdcall]<nint, ushort, char*, ushort*, ulong*, int>)Slot(header, 8))(
                header, index, null, &length, &time);
            if (length is 0 or > 4096) return null;
            var name = new char[length];
            fixed (char* text = name)
                Check(((delegate* unmanaged[Stdcall]<nint, ushort, char*, ushort*, ulong*, int>)Slot(header, 8))(
                    header, index, text, &length, &time));
            if (length > name.Length || time >= duration || time % 10_000 != 0) return null;
            starts[index] = checked((long)(time / 10_000));
            // Duplicate times cannot be navigated by the playback clock alone.
            if (index > 0 && starts[index] <= starts[index - 1]) return null;
        }
        return clock.Elapsed > TimeSpan.FromSeconds(2) ? null : new NativeChapterMap(starts);
    }

    private static ulong Attribute(nint header, string name, int expectedType, ushort expectedLength)
    {
        ushort stream = 0, length = 8;
        var type = 0;
        ulong value = 0;
        fixed (char* key = name)
            Check(((delegate* unmanaged[Stdcall]<nint, ushort*, char*, int*, byte*, ushort*, int>)Slot(header, 5))(
                header, &stream, key, &type, (byte*)&value, &length));
        if (type != expectedType || length != expectedLength || stream != 0)
            throw new InvalidDataException("The ASF timeline attribute is unavailable.");
        return value;
    }

    // Windows SDK wmsdkidl.h ABI. No managed COM wrapper or media library is bundled.
    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    [DllImport("wmvcore.dll", ExactSpelling = true)] private static extern int WMCreateEditor(out nint editor);
    [DllImport("wmvcore.dll", ExactSpelling = true)] private static extern int WMCreateSyncReader(nint certificate, uint rights, out nint reader);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
