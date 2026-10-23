using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NoirMediaPlayer.Controls;

namespace NoirMediaPlayer.Services;

internal sealed unsafe partial class WindowsMediaEngine
{
    private const uint FirstVideoStream = 0xFFFFFFFC;

    // Read the source at the playback clock, independently of the HWND renderer. This
    // captures decoded pixels at source resolution without capturing the desktop or HUD.
    internal void SaveSnapshot(string path, PlainSubtitleTrack? subtitles = null)
    {
        string source;
        string? contentType;
        long time;
        int manualRotation;
        SubtitleCue? embeddedCue;
        lock (_sync)
        {
            EnsureAlive();
            // Source Reader shares the MPEG source's imprecise seek behavior.
            if (_mpegSource) throw new NotSupportedException("Exact MPEG frame capture requires compatibility playback.");
            if (!HasVideo || _source is null)
                throw new NotSupportedException("Native snapshots require a video source.");
            source = _source;
            contentType = _sourceContentType;
            time = Time;
            manualRotation = _rotation;
            if (_captionFailed) throw new NotSupportedException("Embedded captions require compatibility capture.");
            embeddedCue = subtitles is null ? CaptionAt(time) : null;
            // Ratio overrides affect presentation only. Keep source-resolution
            // exports, as the compatibility renderer does, with upright captions.
            if (!File.Exists(source) && (!IsSeekable || Length <= 0))
                throw new NotSupportedException("Network snapshots require a finite, seekable video source.");
        }

        SaveSourceSnapshot(source, path, time, contentType, subtitles, manualRotation, embeddedCue);
    }

    // Source Reader capture can also serve validated sources whose live playback
    // needs another backend. It does not seek, pause or recreate that player.
    internal static void SaveSourceSnapshot(string source, string path, long time,
        string? contentType = null, PlainSubtitleTrack? subtitles = null, int manualRotation = 0, SubtitleCue? embeddedCue = null)
    {
        if (time < 0) throw new ArgumentOutOfRangeException(nameof(time));
        if (manualRotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(manualRotation));
        using var apartment = new ComApartment();
        nint attributes = 0, reader = 0, format = 0, currentFormat = 0, sourceFormat = 0, selectedSample = 0, byteStream = 0, byteAttributes = 0;
        WindowsSnapshotHttpStream? httpStream = null;
        Check(MFStartup(0x20070, 0));
        try
        {
            Check(MFCreateAttributes(out attributes, 2));
            SetUInt32(attributes, new Guid("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D"), 1); // video processing
            SetUInt32(attributes, new Guid("AA456CFD-3943-4A1E-A77D-1838C0EA2E35"), 1); // software snapshot decode
            if (File.Exists(source)) Check(MFCreateSourceReaderFromURL(source, attributes, out reader));
            else
            {
                httpStream = new WindowsSnapshotHttpStream(source);
                Check(MFCreateMFByteStreamOnStream(httpStream, out byteStream));
                var attributeId = new Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3");
                Check(Marshal.QueryInterface(byteStream, in attributeId, out byteAttributes));
                SetString(byteAttributes, new Guid("FC358288-3CB6-460C-A424-B6681260375A"), source);
                SetString(byteAttributes, new Guid("FC358289-3CB6-460C-A424-B6681260375A"), contentType ?? httpStream.ContentType);
                Check(MFCreateSourceReaderFromByteStream(byteStream, attributes, out reader));
            }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, 0xFFFFFFFE, 0));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, 4))(reader, FirstVideoStream, 1));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(reader, 5))(reader, FirstVideoStream, 0, &sourceFormat));
            var rotation = SnapshotRotation(sourceFormat);
            Check(MFCreateMediaType(out format));
            SetGuid(format, MajorTypeKey, VideoType);
            SetGuid(format, new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), new Guid("00000016-0000-0010-8000-00AA00389B71")); // RGB32
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(reader, 7))(reader, FirstVideoStream, 0, format));
            var target = new PropVariant { Type = 20, Int64 = checked(time * 10_000) };
            var timeFormat = Guid.Empty;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(reader, 8))(reader, &timeFormat, &target));

            var deadline = Stopwatch.StartNew();
            var frameFound = false;
            for (var attempt = 0; attempt < 600 && deadline.Elapsed < TimeSpan.FromSeconds(8); attempt++)
            {
                uint flags = 0, stream = 0;
                long timestamp = 0;
                nint sample = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9))(
                    reader, FirstVideoStream, 0, &stream, &flags, &timestamp, &sample));
                if ((flags & 1) != 0) { if (sample != 0) Marshal.Release(sample); throw new InvalidOperationException("Snapshot decoding failed."); }
                if (sample != 0)
                {
                    if (selectedSample != 0) Marshal.Release(selectedSample);
                    selectedSample = sample;
                    long duration = 0;
                    _ = ((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(sample, 37))(sample, &duration);
                    if (timestamp >= target.Int64 || (duration > 0 && timestamp + duration > target.Int64))
                    {
                        frameFound = true;
                        break;
                    }
                }
                if ((flags & 2) != 0) { frameFound = selectedSample != 0; break; } // final frame at end of stream
                if (attempt == 599 || deadline.Elapsed >= TimeSpan.FromSeconds(8))
                    throw new TimeoutException("Snapshot seeking took too long.");
            }
            if (!frameFound) throw new TimeoutException("Snapshot seeking took too long.");
            if (selectedSample == 0) throw new InvalidOperationException("No video frame is available at this position.");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(reader, 6))(reader, FirstVideoStream, &currentFormat));
            var outputRotation = SnapshotRotation(currentFormat);
            if (outputRotation != 0 && outputRotation != rotation)
                throw new NotSupportedException("The snapshot decoder changed the source orientation.");
            var bitmap = ApplySnapshotOrientation(DecodeSnapshotPixels(selectedSample, currentFormat), rotation);
            bitmap = ApplySnapshotOrientation(bitmap, (uint)manualRotation);
            if (subtitles is not null || embeddedCue is not null) bitmap = SubtitlePresenter.Composite(bitmap, subtitles?.CueAt(time) ?? embeddedCue);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            File.WriteAllBytes(path, encoded.ToArray());
        }
        finally
        {
            if (selectedSample != 0) Marshal.Release(selectedSample);
            if (currentFormat != 0) Marshal.Release(currentFormat);
            if (sourceFormat != 0) Marshal.Release(sourceFormat);
            if (format != 0) Marshal.Release(format);
            if (reader != 0) Marshal.Release(reader);
            if (byteAttributes != 0) Marshal.Release(byteAttributes);
            if (byteStream != 0) Marshal.Release(byteStream);
            httpStream?.Dispose();
            if (attributes != 0) Marshal.Release(attributes);
            MFShutdown();
        }
    }

    private static BitmapSource DecodeSnapshotPixels(nint sample, nint format)
    {
        var sizeKey = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
        ulong size = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(format, 8))(format, &sizeKey, &size));
        var width = checked((int)(size >> 32));
        var height = checked((int)(size & uint.MaxValue));
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height * 4 > 256 * 1024 * 1024)
            throw new NotSupportedException("The snapshot dimensions exceed the native capture limit.");
        var rowBytes = checked(width * 4);
        var strideKey = new Guid("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
        uint rawStride = 0;
        var strideResult = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(format, 7))(format, &strideKey, &rawStride);
        if (strideResult < 0) Check(MFGetStrideForBitmapInfoHeader(22, (uint)width, out rawStride));
        var stride = unchecked((int)rawStride);
        var pitch = Math.Abs((long)stride);
        if (pitch < rowBytes || pitch * height > int.MaxValue) throw new NotSupportedException("The video frame stride is invalid.");

        nint buffer = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
            nint data = 0;
            uint maximum = 0, length = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &maximum, &length));
            try
            {
                if (data == 0 || (height - 1) * pitch + rowBytes > length) throw new InvalidDataException("The decoded video frame is incomplete.");
                var pixels = new byte[checked(rowBytes * height)];
                for (var row = 0; row < height; row++)
                {
                    var offset = checked((int)((stride < 0 ? height - 1 - row : row) * pitch));
                    Marshal.Copy(data + offset, pixels, row * rowBytes, rowBytes);
                }
                BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, rowBytes);
                var aspectKey = new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6");
                ulong aspect = 0;
                if (((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(format, 8))(format, &aspectKey, &aspect) >= 0 &&
                    (uint)aspect != 0 && (uint)(aspect >> 32) != 0 && (uint)aspect != (uint)(aspect >> 32))
                {
                    var scale = (double)(aspect >> 32) / (uint)aspect;
                    if (!double.IsFinite(scale) || width * scale > 16384 || width * scale < 1 || width * scale * height * 4 > 256 * 1024 * 1024)
                        throw new NotSupportedException("The snapshot pixel aspect ratio is unsupported.");
                    bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, 1));
                }
                bitmap.Freeze();
                return bitmap;
            }
            finally { _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer); }
        }
        finally { if (buffer != 0) Marshal.Release(buffer); }
    }

    private static uint SnapshotRotation(nint format)
    {
        var key = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");
        uint rotation = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(format, 7))(format, &key, &rotation);
        if (result == unchecked((int)0xC00D36E6)) return 0; // MF_E_ATTRIBUTENOTFOUND
        Check(result);
        if (rotation is not (0 or 90 or 180 or 270))
            throw new NotSupportedException("The snapshot source orientation is unsupported.");
        return rotation;
    }

    private static BitmapSource ApplySnapshotOrientation(BitmapSource image, uint rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new NotSupportedException("The snapshot source orientation is unsupported.");
        if (rotation == 0) return image;
        // Correct the stored counter-clockwise orientation after pixel-aspect
        // correction, before composing upright captions in the final picture.
        var oriented = new TransformedBitmap(image, new RotateTransform(rotation));
        oriented.Freeze();
        return oriented;
    }

    private static void SetUInt32(nint attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value));
    private static void SetGuid(nint attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value));
    private static void SetString(nint attributes, Guid key, string value)
    {
        fixed (char* text = value)
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, char*, int>)Slot(attributes, 25))(attributes, &key, text));
    }

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    private static extern int MFCreateSourceReaderFromByteStream(nint stream, nint attributes, out nint reader);
    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMFByteStreamOnStream([MarshalAs(UnmanagedType.Interface)] IStream stream, out nint byteStream);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out nint mediaType);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFGetStrideForBitmapInfoHeader(uint format, uint width, out uint stride);
}
