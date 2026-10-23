using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NoirMediaPlayer.Services;

internal static partial class Mp4Chapters
{
    // Layouts follow Apple's QuickTime File Format documentation:
    // https://developer.apple.com/documentation/quicktime-file-format/chapter_lists
    // Only a disabled, self-contained text track with an unmodified timeline is
    // migrated. Enabled text can be visible captions and needs the full renderer.
    private sealed record ChapterTrack(uint Id, uint Flags, ulong Duration, Box? Media, Box? Edits, uint[] References);

    private static ChapterTrack ReadTrack(Reader reader, Box track)
    {
        uint id = 0, flags = 0;
        ulong duration = 0;
        Box? media = null, edits = null;
        uint[]? references = null;
        var headerFound = false;
        foreach (var box in reader.Children(track.End))
        {
            switch (box.Type)
            {
                case "tkhd":
                    if (headerFound) throw new InvalidDataException();
                    headerFound = true;
                    var header = reader.Payload(box, 96);
                    if (header.Length < 4) throw new InvalidDataException();
                    flags = BinaryPrimitives.ReadUInt32BigEndian(header) & 0xffffff;
                    if (header[0] == 0 && header.Length == 84)
                    {
                        id = U32(header, 12);
                        duration = U32(header, 20);
                    }
                    else if (header[0] == 1 && header.Length == 96)
                    {
                        id = U32(header, 20);
                        duration = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(28));
                    }
                    else throw new NotSupportedException();
                    if (id == 0) throw new InvalidDataException();
                    break;
                case "mdia":
                    if (media is not null) throw new InvalidDataException();
                    media = box;
                    break;
                case "edts":
                    if (edits is not null) throw new InvalidDataException();
                    edits = box;
                    break;
                case "tref":
                    foreach (var reference in reader.Children(box.End))
                    {
                        if (reference.Type != "chap") continue;
                        if (references is not null) throw new NotSupportedException("Multiple chapter reference lists.");
                        var data = reader.Payload(reference, 1024);
                        if (data.Length == 0 || data.Length % 4 != 0) throw new InvalidDataException();
                        references = new uint[data.Length / 4];
                        for (var index = 0; index < references.Length; index++) references[index] = U32(data, index * 4);
                    }
                    break;
            }
        }
        return new ChapterTrack(id, flags, duration, media, edits, references ?? []);
    }

    private static long[] ReadQuickTime(Reader reader, List<ChapterTrack> tracks, List<Box> media, uint movieScale)
    {
        var owners = tracks.Where(track => track.References.Length > 0).ToArray();
        if (owners.Length == 0) return [];
        if (tracks.Any(track => track.Id == 0) || tracks.Select(track => track.Id).Distinct().Count() != tracks.Count ||
            owners.Any(track => (track.Flags & 1) == 0 || track.References.Length != 1)) throw new NotSupportedException("Ambiguous chapter ownership.");
        var id = owners[0].References[0];
        if (owners.Any(track => track.References[0] != id)) throw new NotSupportedException("Alternate chapter tracks.");
        var chapter = tracks.SingleOrDefault(track => track.Id == id);
        if (chapter is null || chapter.References.Length > 0 || (chapter.Flags & 1) != 0 || chapter.Flags > 7 || chapter.Media is not { } mdia)
            throw new NotSupportedException("Unsupported chapter track.");
        var boxes = ChildrenAt(reader, mdia);
        var header = reader.PayloadAt(Required(boxes, "mdhd"), 36);
        FullBox(header, allowVersionOne: true);
        uint scale;
        ulong duration;
        if (header[0] == 0 && header.Length == 24) { scale = U32(header, 12); duration = U32(header, 16); }
        else if (header[0] == 1 && header.Length == 36) { scale = U32(header, 20); duration = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24)); }
        else throw new NotSupportedException();
        if (scale == 0 || duration == 0 || (UInt128)chapter.Duration * scale != (UInt128)duration * movieScale)
            throw new NotSupportedException("The chapter track has a different timeline.");
        var handler = reader.PayloadAt(Required(boxes, "hdlr"), 4096);
        FullBox(handler);
        if (handler.Length < 24 || !handler.AsSpan(8, 4).SequenceEqual("text"u8)) throw new NotSupportedException("Unsupported chapter media type.");
        CheckIdentityEdit(reader, chapter, movieScale, scale, duration);
        var information = ChildrenAt(reader, Required(boxes, "minf"));
        CheckDataReference(reader, Required(information, "dinf"));
        var table = ChildrenAt(reader, Required(information, "stbl"));
        if (table.Any(box => box.Type is not ("stsd" or "stts" or "stsc" or "stsz" or "stco" or "co64" or "ctts" or "stss" or "free" or "skip")))
            throw new NotSupportedException("Unsupported chapter sample table.");
        CheckSampleDescription(reader.PayloadAt(Required(table, "stsd"), 128 * 1024));
        var starts = ReadTimes(reader.PayloadAt(Required(table, "stts"), 32776), scale, duration);
        var sizes = ReadSizes(reader.PayloadAt(Required(table, "stsz"), 16396), starts.Length);
        CheckCompositionTimes(reader, table, starts.Length);
        CheckSamples(reader, table, media, sizes);
        return starts;
    }

    private static Box[] ChildrenAt(Reader reader, Box parent)
    {
        reader.Position = parent.Start;
        return reader.Children(parent.End).ToArray();
    }

    private static Box Required(Box[] boxes, string type)
    {
        var matching = boxes.Where(box => box.Type == type).ToArray();
        return matching.Length == 1 ? matching[0] : throw new InvalidDataException("Missing or duplicate chapter metadata.");
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));

    private static void FullBox(byte[] data, bool allowVersionOne = false)
    {
        if (data.Length < 4 || data[0] > (allowVersionOne ? 1 : 0) || data[1] != 0 || data[2] != 0 || data[3] != 0)
            throw new NotSupportedException("Unsupported metadata version or flags.");
    }

    private static void CheckIdentityEdit(Reader reader, ChapterTrack chapter, uint movieScale, uint mediaScale, ulong mediaDuration)
    {
        if (chapter.Edits is not { } edits) return;
        var children = ChildrenAt(reader, edits);
        if (children.Length != 1) throw new NotSupportedException("Complex chapter edits.");
        var data = reader.PayloadAt(Required(children, "elst"), 28);
        FullBox(data, allowVersionOne: true);
        if (data.Length < 8 || U32(data, 4) != 1) throw new NotSupportedException("Multiple chapter edits.");
        ulong duration, start;
        uint rate;
        if (data[0] == 0 && data.Length == 20) { duration = U32(data, 8); start = U32(data, 12); rate = U32(data, 16); }
        else if (data[0] == 1 && data.Length == 28)
        {
            duration = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(8));
            start = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(16));
            rate = U32(data, 24);
        }
        else throw new InvalidDataException();
        if (start != 0 || rate != 0x10000 || duration != chapter.Duration || (UInt128)duration * mediaScale != (UInt128)mediaDuration * movieScale)
            throw new NotSupportedException("The chapter edit changes the playback timeline.");
    }

    private static void CheckDataReference(Reader reader, Box information)
    {
        var reference = Required(ChildrenAt(reader, information), "dref");
        var data = reader.PayloadAt(reference, 128);
        FullBox(data);
        // Full-box header + one self-contained url entry, with no external path.
        if (data.Length != 20 || U32(data, 4) != 1 || U32(data, 8) != 12 ||
            !data.AsSpan(12, 4).SequenceEqual("url "u8) || U32(data, 16) != 1)
            throw new NotSupportedException("External chapter data references.");
    }

    private static void CheckSampleDescription(byte[] data)
    {
        FullBox(data);
        if (data.Length < 24 || U32(data, 4) != 1 || U32(data, 8) != data.Length - 8 ||
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22)) != 1) throw new NotSupportedException("Unsupported chapter sample description.");
        var format = Encoding.Latin1.GetString(data, 12, 4);
        if (format == "tx3g" ? data.Length < 54 : format != "text" || data.Length < 68)
            throw new NotSupportedException("Unsupported chapter text format.");
    }

    private static long[] ReadTimes(byte[] data, uint scale, ulong duration)
    {
        FullBox(data);
        if (data.Length < 8 || U32(data, 4) > 4096 || data.Length != 8 + U32(data, 4) * 8) throw new InvalidDataException();
        var starts = new List<long>();
        ulong time = 0;
        for (var entry = 8; entry < data.Length; entry += 8)
        {
            var count = U32(data, entry);
            var delta = U32(data, entry + 4);
            if (count == 0 || count > 4096 - starts.Count || delta == 0) throw new NotSupportedException("Unsupported chapter timing.");
            for (var index = 0; index < count; index++)
            {
                starts.Add(checked((long)(time / scale) * 1000 + (long)(time % scale * 1000 / scale)));
                time = checked(time + delta);
            }
        }
        if (starts.Count == 0 || time != duration) throw new InvalidDataException("Inconsistent chapter duration.");
        var result = starts.ToArray();
        ValidateOrder(result);
        return result;
    }

    private static uint[] ReadSizes(byte[] data, int count)
    {
        FullBox(data);
        if (data.Length < 12 || U32(data, 8) != count) throw new InvalidDataException();
        var fixedSize = U32(data, 4);
        if (data.Length != (fixedSize == 0 ? 12 + count * 4 : 12)) throw new InvalidDataException();
        var sizes = new uint[count];
        for (var index = 0; index < count; index++) sizes[index] = fixedSize == 0 ? U32(data, 12 + index * 4) : fixedSize;
        if (sizes.Any(size => size < 3 || size > 128 * 1024) || sizes.Sum(size => (long)size) > 4 * 1024 * 1024)
            throw new NotSupportedException("Unsupported chapter text sample size.");
        return sizes;
    }

    private static void CheckCompositionTimes(Reader reader, Box[] table, int samples)
    {
        if (!table.Any(box => box.Type == "ctts")) return;
        var data = reader.PayloadAt(Required(table, "ctts"), 32776);
        FullBox(data, allowVersionOne: true);
        if (data.Length < 8 || U32(data, 4) > 4096 || data.Length != 8 + U32(data, 4) * 8) throw new InvalidDataException();
        long count = 0;
        for (var entry = 8; entry < data.Length; entry += 8)
        {
            if (U32(data, entry) == 0 || U32(data, entry + 4) != 0) throw new NotSupportedException("Chapter composition offsets.");
            count += U32(data, entry);
        }
        if (count != samples) throw new InvalidDataException();
    }

    private static void CheckSamples(Reader reader, Box[] table, List<Box> media, uint[] sizes)
    {
        var offsets = table.Where(box => box.Type is "stco" or "co64").ToArray();
        if (offsets.Length != 1) throw new InvalidDataException();
        var offsetData = reader.PayloadAt(offsets[0], 32776);
        FullBox(offsetData);
        var width = offsets[0].Type == "stco" ? 4 : 8;
        if (offsetData.Length < 8) throw new InvalidDataException();
        var chunks = U32(offsetData, 4);
        if (chunks == 0 || chunks > sizes.Length || offsetData.Length != 8 + chunks * width) throw new InvalidDataException();
        var mapping = reader.PayloadAt(Required(table, "stsc"), 49160);
        FullBox(mapping);
        if (mapping.Length < 20) throw new InvalidDataException();
        var entries = U32(mapping, 4);
        if (entries == 0 || entries > chunks || mapping.Length != 8 + entries * 12 || U32(mapping, 8) != 1) throw new InvalidDataException();
        for (var entry = 8; entry < mapping.Length; entry += 12)
            if (U32(mapping, entry) == 0 || U32(mapping, entry) > chunks || U32(mapping, entry + 4) == 0 || U32(mapping, entry + 8) != 1 ||
                (entry > 8 && U32(mapping, entry) <= U32(mapping, entry - 12))) throw new NotSupportedException("Unsupported chapter chunk mapping.");
        var current = 8;
        var sample = 0;
        for (var chunk = 1; chunk <= chunks; chunk++)
        {
            if (current + 12 < mapping.Length && U32(mapping, current + 12) == chunk) current += 12;
            var count = U32(mapping, current + 4);
            if (count > sizes.Length - sample) throw new InvalidDataException();
            var offset = 8 + (chunk - 1) * width;
            var position = width == 4 ? U32(offsetData, offset) : BinaryPrimitives.ReadUInt64BigEndian(offsetData.AsSpan(offset));
            for (var index = 0; index < count; index++, sample++)
            {
                var end = checked(position + sizes[sample]);
                if (end > long.MaxValue || !media.Any(box => position >= (ulong)box.Start && end <= (ulong)box.End)) throw new InvalidDataException("Chapter sample outside media data.");
                var length = reader.TitleLength((long)position);
                if (length == 0 || length > sizes[sample] - 2) throw new InvalidDataException("Truncated or empty chapter title.");
                position = end;
            }
        }
        if (sample != sizes.Length) throw new InvalidDataException("Inconsistent chapter sample count.");
    }
}
