using System.Buffers.Binary;
using System.IO;
using System.Net.Http;

namespace NoirMediaPlayer.Services;

internal static class WindowsHttpMediaProbe
{
    internal static WindowsPlaybackPlan? Inspect(string source, WindowsPlaybackPlan plan, bool asfOnly = false)
    {
        try
        {
            using var input = new WindowsSnapshotHttpStream(source, TimeSpan.FromSeconds(2), byteLimit: 8 * 1024 * 1024);
            using var stream = input.AsReadStream();
            Span<byte> header = stackalloc byte[16];
            stream.ReadExactly(header);
            var mime = input.ContentType.ToLowerInvariant();
            var generic = mime == "application/octet-stream";
            if (new Guid(header) == new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C"))
            {
                if (!(generic || mime is "video/x-ms-asf" or "application/vnd.ms-asf" or "video/x-ms-wmv" or "audio/x-ms-wma")) return null;
                // Let Windows validate the finite, unprotected ASF timeline and
                // supply canonical markers. Never admit a header signature alone.
                stream.Position = 0;
                var markers = WindowsAsfChapters.TryRead(input);
                return markers is null ? null : plan with
                {
                    Chapters = markers, DetectedContentType = "video/x-ms-asf", UsesAsfMarkers = true
                };
            }
            if (asfOnly) return null;
            if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0x1A45DFA3)
            {
                if (!(generic || mime is "video/x-matroska" or "audio/x-matroska")) return null;
                // The complete physical timeline must pass the same inspection as
                // a named MKV. A signature alone cannot rule out virtual editions.
                var markers = MatroskaChapters.TryRead(stream);
                return markers is null ? null : plan with { Chapters = markers, DetectedContentType = "video/x-matroska" };
            }
            if (header[..4].SequenceEqual("RIFF"u8))
            {
                // Multi-RIFF/OpenDML, RF64 and uncertain lengths retain VLC.
                if ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8 != (ulong)input.Length) return null;
                if (header[8..12].SequenceEqual("WAVE"u8) &&
                    (generic || mime is "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/vnd.wave")) return plan with { DetectedContentType = "audio/wav" };
                if (header[8..12].SequenceEqual("AVI "u8) &&
                    (generic || mime is "video/avi" or "video/msvideo" or "video/x-msvideo")) return plan with { DetectedContentType = "video/x-msvideo" };
                return null;
            }
            if (!header[4..8].SequenceEqual("ftyp"u8) ||
                BinaryPrimitives.ReadUInt32BigEndian(header) is < 16 or > 4096 ||
                !(generic || mime is "video/mp4" or "audio/mp4" or "application/mp4" or "video/quicktime" or "video/3gpp" or "audio/3gpp")) return null;
            // A successful complete chapter scan also rules out uncertain MP4
            // layouts and visible chapter tracks before admitting an unnamed source.
            var chapters = Mp4Chapters.TryRead(stream, out var requiresCompatibility);
            return chapters is null || requiresCompatibility ? null : plan with { Chapters = chapters, DetectedContentType = "video/mp4" };
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }
}
