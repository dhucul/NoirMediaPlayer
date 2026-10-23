using System.IO;
using System.Runtime.InteropServices;

namespace NoirMediaPlayer.Services;

internal sealed unsafe partial class WindowsDvdSession
{
    private static readonly Guid NavigatorClass = new("9B8C4620-2C1A-11D0-8493-00A02438AD48");
    private static readonly Guid DirectSoundClass = new("79376820-07D0-11CF-A24D-0020AFD79767");

    internal static bool CanConnectExplicitly(WindowsDvdBuildDiagnostic diagnostic)
    {
        // Repair only the observed wholly disconnected graph. Partial rendering,
        // invalid media and unknown components retain their original failure.
        if (diagnostic.NativeResult != "0x8004027B" || diagnostic.InvalidVolume || diagnostic.UnknownVolume ||
            diagnostic.StreamCount != 3 || diagnostic.FailedStreams != 3 || diagnostic.FailedStreamFlags != 7 ||
            diagnostic.InspectionError is not null || diagnostic.Filters.Any(filter =>
                !AllowedFilters.Contains(filter.ClassId) || filter.Pins.Count == 0 || filter.Pins.Any(pin => pin.Connected))) return false;
        return new[] { NavigatorClass, VideoDecoder, AudioDecoder, Vmr9 }
            .All(id => diagnostic.Filters.Count(filter => filter.ClassId == id) == 1);
    }

    private int ConnectWindowsFilters(List<WindowsDvdConnectionAttempt> attempts)
    {
        nint navigator = 0, video = 0, audio = 0, sink = 0;
        try
        {
            navigator = FindFilter(NavigatorClass);
            video = FindFilter(VideoDecoder);
            audio = FindFilter(AudioDecoder);
            if (!GetFilterClasses().Contains(DirectSoundClass)) AddDecoder(DirectSoundClass, "Windows DirectSound renderer");
            sink = FindFilter(DirectSoundClass);
            var audioOutput = ReadPins(navigator).Where(pin => pin.Direction == 1 &&
                pin.Id != "Video" && pin.Id != "SubPicture").ToArray();
            var audioInput = ReadPins(sink).Where(pin => pin.Direction == 0).ToArray();
            if (audioOutput.Length != 1 || audioInput.Length != 1)
                throw new InvalidDataException("The Windows DVD audio pin layout is not supported.");

            // ConnectDirect negotiates the actual Windows media types and preserves
            // the Navigator/decoder's normal protection handshake. It cannot insert
            // another decoder or renderer. No graph is run until all six edges work.
            var results = new List<int>();
            var videoResult = ConnectPins(navigator, "Video", video, "Video Input", "Video decoder", attempts);
            results.Add(videoResult);
            if (videoResult == 0)
            {
                results.Add(ConnectPins(navigator, "SubPicture", video, "Subpicture Input", "Subpicture decoder", attempts));
                results.Add(ConnectPins(video, "Video Output 1", _renderer, "VMR Input0", "Video renderer", attempts));
                // The Microsoft decoder creates this output only after accepting
                // DVD_ENCRYPTED_PACK video. It carries decoded subtitles/highlights.
                var subpictureOutputs = ReadPins(video).Where(pin => pin.Direction == 1 &&
                    pin.Id != "Video Output 1" && !pin.Id.StartsWith('~')).ToArray();
                if (subpictureOutputs.Length != 1)
                    throw new InvalidDataException("The Windows DVD subpicture output is unavailable.");
                results.Add(ConnectPins(video, subpictureOutputs[0].Id, _renderer, "VMR Input1", "Subpicture renderer", attempts));
            }
            var audioResult = ConnectPins(navigator, audioOutput[0].Id, audio, "In", "Audio decoder", attempts);
            results.Add(audioResult);
            if (audioResult == 0) results.Add(ConnectPins(audio, "Out", sink, audioInput[0].Id, "Audio renderer", attempts));
            var firstFailure = results.FirstOrDefault(value => value != 0);
            if (firstFailure != 0) return firstFailure;
            if (results.Count != 6) return unchecked((int)0x80004005);
            var composition = ConfigureSubpictureMixer();
            if (composition != 0)
                attempts.Add(new WindowsDvdConnectionAttempt("Subpicture composition", "VMR Input1", "VMR9 mixer", $"0x{composition:X8}"));
            return composition;
        }
        catch (COMException exception) { return exception.HResult; }
        catch (InvalidDataException) { return unchecked((int)0x80040207); }
        finally { Release(ref sink); Release(ref audio); Release(ref video); Release(ref navigator); }
    }

    private int ConfigureSubpictureMixer()
    {
        nint mixer = 0;
        try
        {
            mixer = Query(_renderer, new Guid("1A777EAA-47C8-4930-B2C9-8FEE1C1B0F3B"));
            var result = ((delegate* unmanaged[Stdcall]<nint, uint, float, int>)Slot(mixer, 3))(mixer, 1, 1);
            if (result == 0) result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(mixer, 5))(mixer, 1, 0);
            return result > 0 ? unchecked((int)0x80004005) : result;
        }
        catch (COMException exception) { return exception.HResult; }
        finally { Release(ref mixer); }
    }

    private int ConnectPins(nint outputFilter, string outputId, nint inputFilter, string inputId,
        string step, List<WindowsDvdConnectionAttempt> attempts)
    {
        nint output = 0, input = 0;
        var result = unchecked((int)0x80004005);
        var mediaTypes = new List<WindowsDvdMediaTypeDiagnostic>();
        string? inspectionError = null;
        try
        {
            output = FindPin(outputFilter, outputId);
            input = FindPin(inputFilter, inputId);
            result = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Slot(_graph, 7))(_graph, output, input, 0);
            if (result == unchecked((int)0x80040207) && (outputId is "Video" or "SubPicture" || inputId == "In"))
            {
                // A null media type hides individual negotiation errors. Retry only
                // the Navigator's own complete types, unchanged, with ConnectDirect.
                // This retains the Windows protection handshake and cannot add filters.
                try { result = ConnectAdvertisedTypes(output, input, mediaTypes, result); }
                catch (Exception exception) when (exception is COMException or InvalidDataException)
                {
                    inspectionError = $"0x{exception.HResult:X8}: {exception.Message}";
                }
            }
            if (result > 0) result = unchecked((int)0x80004005); // No partial-success connections.
        }
        catch (COMException exception) { result = exception.HResult; }
        finally
        {
            attempts.Add(new WindowsDvdConnectionAttempt(step, outputId, inputId, $"0x{result:X8}",
                mediaTypes.Count == 0 ? null : mediaTypes.AsReadOnly(), inspectionError));
            Release(ref input); Release(ref output);
        }
        return result;
    }

    private int ConnectAdvertisedTypes(nint output, nint input, List<WindowsDvdMediaTypeDiagnostic> diagnostics, int originalResult)
    {
        nint enumerator = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(output, 12))(output, &enumerator));
            if (enumerator == 0) throw new InvalidDataException("The DVD pin returned no media-type enumerator.");
            var result = originalResult;
            for (var index = 0; index < 32; index++)
            {
                nint pointer = 0;
                try
                {
                    var next = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, int>)Slot(enumerator, 3))
                        (enumerator, 1, &pointer, 0);
                    if (next == 1) return result;
                    Check(next);
                    if (next != 0 || pointer == 0) throw new InvalidDataException("Invalid DVD media-type enumeration.");
                    var type = (NativeMediaType*)pointer;
                    if (type->FormatBytes > 64 * 1024 || type->FormatBytes > 0 && type->Format == 0)
                        throw new InvalidDataException("Invalid DVD media-type format block.");
                    var accepts = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(input, 11))(input, pointer);
                    int? connection = null;
                    // QueryAccept's S_FALSE is a refusal, not permission to connect.
                    if (accepts == 0)
                    {
                        connection = ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, int>)Slot(_graph, 7))
                            (_graph, output, input, pointer);
                        result = connection.Value > 0 ? unchecked((int)0x80004005) : connection.Value;
                    }
                    diagnostics.Add(new WindowsDvdMediaTypeDiagnostic(type->MajorType, type->Subtype, type->FormatType,
                        type->FormatBytes, $"0x{accepts:X8}", connection is null ? null : $"0x{connection.Value:X8}"));
                    if (connection == 0) return 0;
                }
                finally { FreeMediaType(pointer); }
            }
            throw new InvalidDataException("The DVD pin exceeded the media-type inspection limit.");
        }
        finally { Release(ref enumerator); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMediaType
    {
        internal Guid MajorType, Subtype;
        internal int FixedSizeSamples, TemporalCompression;
        internal uint SampleSize;
        internal Guid FormatType;
        internal nint Unknown;
        internal uint FormatBytes;
        internal nint Format;
    }

    private static void FreeMediaType(nint pointer)
    {
        if (pointer == 0) return;
        var type = (NativeMediaType*)pointer;
        try { if (type->Unknown != 0) Marshal.Release(type->Unknown); }
        finally
        {
            Marshal.FreeCoTaskMem(type->Format);
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private nint FindFilter(Guid expected)
    {
        nint found = 0;
        try
        {
            VisitFilters(filter =>
            {
                var id = new Guid();
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(filter, 3))(filter, &id));
                if (id != expected) return;
                if (found != 0) throw new InvalidDataException("The DVD graph contains duplicate components.");
                Marshal.AddRef(filter); found = filter;
            });
            if (found == 0) throw new COMException("A required Windows DVD component is absent from the graph.", unchecked((int)0x80040216));
            var result = found; found = 0; return result;
        }
        finally { Release(ref found); }
    }
    private static nint FindPin(nint filter, string id)
    {
        nint result = 0;
        fixed (char* name = id)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(filter, 11))(filter, name, &result));
        if (result == 0) throw new COMException("A required Windows DVD pin is absent.", unchecked((int)0x80040216));
        return result;
    }
    private void GetNavigatorInterfaces()
    {
        var navigator = FindFilter(NavigatorClass);
        try
        {
            // The graph builder's success flag is still false after its automatic
            // attempt. The connected Navigator itself owns the same control APIs.
            _control = Query(navigator, new Guid("33BC7430-EEC0-11D2-8201-00A0C9D74842"));
            _info = Query(navigator, new Guid("34151510-EEC0-11D2-8201-00A0C9D74842"));
        }
        finally { Release(ref navigator); }
    }
}
