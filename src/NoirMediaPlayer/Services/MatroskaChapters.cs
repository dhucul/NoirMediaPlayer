using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

/// <summary>Bounded first-party EBML reader; complex timelines retain compatibility playback.</summary>
internal static class MatroskaChapters
{
    // Timed Text does not expose Matroska's default/forced/enabled flags. Keep
    // customized automatic selection with VLC until that policy is also migrated.
    // This scans only EBML headers and small track metadata, never media payloads.
    internal static bool HasSimpleCaptionDefaults(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            var reader = new Reader(file);
            var header = reader.Element(file.Length);
            if (header.Id != 0x1A45DFA3 || header.End - reader.Position > 4096) return false;
            reader.Position = header.End;
            var segment = reader.Element(file.Length, allowUnknownSize: true);
            if (segment.Id != 0x18538067 || segment.End != file.Length) return false;
            var foundTracks = false;
            while (reader.Position < segment.End)
            {
                var element = reader.Element(segment.End);
                if (element.Id == 0x1654AE6B)
                {
                    if (foundTracks || element.End - reader.Position > 1024 * 1024) return false;
                    foundTracks = true;
                    var entries = 0;
                    while (reader.Position < element.End)
                    {
                        var entry = reader.Element(element.End);
                        if (entry.Id == 0xAE)
                        {
                            if (++entries > 64) return false;
                            ulong? type = null;
                            var simple = true;
                            var flags = new HashSet<uint>();
                            while (reader.Position < entry.End)
                            {
                                var property = reader.Element(entry.End);
                                if (property.Id == 0x83)
                                {
                                    if (type is not null) return false;
                                    type = reader.Unsigned(property.End);
                                }
                                else if (property.Id is 0xB9 or 0x88 or 0x55AA or 0x55AB or 0x55AC or 0x55AD or 0x55AE or 0x55AF)
                                {
                                    if (!flags.Add(property.Id)) return false;
                                    var flag = reader.Unsigned(property.End);
                                    if (flag > 1) return false;
                                    simple &= flag == (property.Id is 0xB9 or 0x88 ? 1ul : 0ul);
                                }
                                reader.Position = property.End;
                            }
                            if (type is null || type == 17 && !simple) return false;
                        }
                        reader.Position = entry.End;
                    }
                }
                reader.Position = element.End;
            }
            return foundTracks;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    // Matroska schema: https://www.matroska.org/technical/elements.html
    internal static NativeChapterMap? TryRead(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return TryRead(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static NativeChapterMap? TryRead(Stream file)
    {
        try
        {
            if (!file.CanRead || !file.CanSeek) return null;
            file.Position = 0;
            var reader = new Reader(file);
            var header = reader.Element(file.Length);
            if (header.Id != 0x1A45DFA3 || header.End - file.Position > 4096) return null;
            var documentType = false;
            Span<byte> name = stackalloc byte[8];
            while (file.Position < header.End)
            {
                var child = reader.Element(header.End);
                if (child.Id == 0x4282)
                {
                    if (documentType || child.End - file.Position != 8) return null;
                    file.ReadExactly(name);
                    if (!name.SequenceEqual("matroska"u8)) return null;
                    documentType = true;
                }
                file.Position = child.End;
            }
            if (!documentType) return null;
            var segment = reader.Element(file.Length, allowUnknownSize: true);
            if (segment.Id != 0x18538067 || segment.End != file.Length) return null;
            NativeChapterMap? chapters = null;
            var infoFound = false;
            while (file.Position < segment.End)
            {
                var element = reader.Element(segment.End);
                if (element.Id == 0x1549A966)
                {
                    if (infoFound || element.End - file.Position > 1024 * 1024) return null;
                    infoFound = true;
                    while (file.Position < element.End)
                    {
                        var property = reader.Element(element.End);
                        // Linked segments and chapter codecs can alter playback itself.
                        if (property.Id is 0x3CB923 or 0x3EB923 or 0x6924) return null;
                        file.Position = property.End;
                    }
                }
                else if (element.Id == 0x1043A770)
                {
                    if (chapters is not null || element.End - file.Position > 4 * 1024 * 1024) return null;
                    chapters = ReadChapters(reader, element.End);
                }
                else if (element.Id == 0x18538067) return null;
                file.Position = element.End; // Skip encoded audio/video without reading it.
            }
            return infoFound ? chapters ?? new NativeChapterMap([]) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private static NativeChapterMap ReadChapters(Reader reader, long end)
    {
        var foundEdition = false;
        var starts = new List<long>();
        while (reader.Position < end)
        {
            var element = reader.Element(end);
            if (element.Id is 0xEC or 0xBF) { reader.Position = element.End; continue; }
            if (element.Id != 0x45B9 || foundEdition) throw new NotSupportedException("Multiple or unknown chapter editions.");
            foundEdition = true;
            var flags = new HashSet<uint>();
            while (reader.Position < element.End)
            {
                var child = reader.Element(element.End);
                switch (child.Id)
                {
                    case 0xB6:
                        var start = ReadChapter(reader, child.End);
                        if (starts.Count >= 4096 || (starts.Count > 0 && start <= starts[^1]))
                            throw new NotSupportedException("Unsupported chapter ordering or precision.");
                        starts.Add(start);
                        break;
                    case 0x45BD: // EditionFlagHidden
                    case 0x45DD: // EditionFlagOrdered
                        if (!flags.Add(child.Id) || reader.Unsigned(child.End) != 0) throw new NotSupportedException("Complex chapter edition.");
                        break;
                    case 0x45DB: // EditionFlagDefault
                        if (!flags.Add(child.Id) || reader.Unsigned(child.End) > 1) throw new InvalidDataException();
                        break;
                    case 0x45BC: // EditionUID
                    case 0x4520: // EditionDisplay
                    case 0xEC:
                    case 0xBF:
                        break;
                    default: throw new NotSupportedException("Unknown chapter edition feature.");
                }
                reader.Position = child.End;
            }
        }
        if (!foundEdition || starts.Count == 0) throw new InvalidDataException("Empty chapter edition.");
        return new NativeChapterMap(starts.ToArray());
    }

    private static long ReadChapter(Reader reader, long end)
    {
        ulong? start = null, finish = null;
        var fields = new HashSet<uint>();
        while (reader.Position < end)
        {
            var element = reader.Element(end);
            switch (element.Id)
            {
                case 0x91:
                    if (start is not null) throw new InvalidDataException();
                    start = reader.Unsigned(element.End);
                    break;
                case 0x92:
                    if (finish is not null) throw new InvalidDataException();
                    finish = reader.Unsigned(element.End);
                    break;
                case 0x98: // ChapterFlagHidden
                    if (!fields.Add(element.Id) || reader.Unsigned(element.End) != 0) throw new NotSupportedException("Hidden chapter.");
                    break;
                case 0x4598: // ChapterFlagEnabled
                    if (!fields.Add(element.Id) || reader.Unsigned(element.End) != 1) throw new NotSupportedException("Disabled chapter.");
                    break;
                case 0x73C4: // ChapterUID
                case 0x5654: // ChapterStringUID
                case 0x63C3: // ChapterPhysicalEquiv
                case 0x80:   // ChapterDisplay: navigation does not interpret display labels.
                case 0xEC:
                case 0xBF:
                    break;
                // Nested, linked, track-specific chapters and chapter commands need VLC.
                default: throw new NotSupportedException("Complex chapter feature.");
            }
            reader.Position = element.End;
        }
        if (start is null || finish < start) throw new InvalidDataException("Invalid chapter timing.");
        return checked((long)(start.Value / 1_000_000)); // Chapter times are nanoseconds, independent of TimestampScale.
    }

    private sealed class Reader(Stream source)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _elements;
        internal long Position { get => source.Position; set => source.Position = value; }

        internal (uint Id, long End) Element(long parentEnd, bool allowUnknownSize = false)
        {
            if (++_elements > 100_000 || _clock.Elapsed > TimeSpan.FromSeconds(2)) throw new NotSupportedException("Chapter metadata scan limit.");
            var id = Vint(parentEnd, id: true, out _);
            var size = Vint(parentEnd, id: false, out var unknown);
            if (unknown && !allowUnknownSize) throw new NotSupportedException("Unknown-size element.");
            if (!unknown && size > (ulong)(parentEnd - Position)) throw new InvalidDataException("Truncated EBML element.");
            return (checked((uint)id), unknown ? parentEnd : checked(Position + (long)size));
        }

        internal ulong Unsigned(long end)
        {
            var length = end - Position;
            if (length is < 1 or > 8) throw new InvalidDataException("Invalid EBML integer.");
            ulong value = 0;
            while (Position < end) value = (value << 8) | Byte(end);
            return value;
        }

        private ulong Vint(long end, bool id, out bool unknown)
        {
            var first = Byte(end);
            var marker = 0x80;
            var length = 1;
            while ((first & marker) == 0 && length <= 8) { marker >>= 1; length++; }
            if (length > (id ? 4 : 8)) throw new InvalidDataException("Invalid EBML header.");
            ulong value = id ? first : (ulong)(first & (marker - 1));
            for (var index = 1; index < length; index++) value = (value << 8) | Byte(end);
            unknown = !id && value == (1ul << (length * 7)) - 1;
            return value;
        }

        private byte Byte(long end)
        {
            if (Position >= end) throw new EndOfStreamException();
            var value = source.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return (byte)value;
        }
    }
}
