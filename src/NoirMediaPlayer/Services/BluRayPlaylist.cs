using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace NoirMediaPlayer.Services;

[Flags]
internal enum BluRayPlaylistFeatures
{
    None = 0,
    NonSequential = 1,
    SubPaths = 2,
    MultiAngle = 4,
    StillFrames = 8,
    ExtendedData = 16,
    SecondaryStreams = 32,
    InteractiveGraphics = 64,
    UnknownStreamTypes = 128,
    UnknownCodecTypes = 256,
    UnknownConnections = 512,
    UnknownMarks = 1024,
    UnparsedData = 2048
}

internal sealed record BluRayClip(string Id, string Codec, byte StcId);

internal sealed record BluRayStream(
    string Kind, byte StreamType, ushort? PacketId, byte? SubPathId, byte? SubClipId,
    byte CodingType, string? Language, byte? Format, byte? Rate);

internal sealed record BluRayPlayItem(
    IReadOnlyList<BluRayClip> Clips, uint InTime, uint OutTime, long StartTime,
    byte ConnectionCondition, ulong UserOperationMask, bool RandomAccess,
    byte StillMode, ushort StillTime, byte AngleFlags, IReadOnlyList<BluRayStream> Streams)
{
    public long DurationTicks => (long)OutTime - InTime;
}

internal sealed record BluRayMark(
    byte Type, ushort PlayItemIndex, uint ClipTime, ushort EntryPacketId,
    uint Duration, long PlaylistTimeTicks);

/// <summary>
/// Read-only MPLS metadata. Times use the Blu-ray 45 kHz clock, not the MPEG 90 kHz clock.
/// A playlist is not a disc title selection or evidence of playable/decryptable media.
/// </summary>
internal sealed record BluRayPlaylist(
    string Version, byte PlaybackType, ushort PlaybackCount, ulong UserOperationMask,
    ushort ApplicationFlags, ushort SubPathCount, IReadOnlyList<BluRayPlayItem> Items,
    IReadOnlyList<BluRayMark> Marks, long SequentialDurationTicks, BluRayPlaylistFeatures Features)
{
    public long SequentialDurationMilliseconds => SequentialDurationTicks / 45;
    public IReadOnlyList<long>? LinearChapterStartsMilliseconds => CreateLinearChapterMap()?.Starts;

    internal NativeChapterMap? CreateLinearChapterMap()
    {
        const BluRayPlaylistFeatures unsupported = BluRayPlaylistFeatures.NonSequential |
            BluRayPlaylistFeatures.SubPaths | BluRayPlaylistFeatures.MultiAngle |
            BluRayPlaylistFeatures.StillFrames | BluRayPlaylistFeatures.ExtendedData |
            BluRayPlaylistFeatures.UnknownConnections | BluRayPlaylistFeatures.UnknownMarks |
            BluRayPlaylistFeatures.UnparsedData;
        if ((Features & unsupported) != 0 || SequentialDurationTicks <= 0) return null;
        var starts = new List<long>();
        foreach (var mark in Marks)
        {
            if (mark.Type != 1) continue; // Link marks are not chapter entries.
            var milliseconds = mark.PlaylistTimeTicks / 45;
            // Keep the original mark inventory, but never silently reorder, merge or
            // truncate chapter entries to manufacture a native navigation timeline.
            if (mark.PlaylistTimeTicks >= SequentialDurationTicks ||
                (starts.Count > 0 && milliseconds <= starts[^1])) return null;
            starts.Add(milliseconds);
        }
        return new NativeChapterMap(starts.ToArray());
    }
}

/// <summary>First-party bounded binary reader; does not load VLC, media streams or AACS data.</summary>
internal static class BluRayPlaylistReader
{
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumPlayItems = 4096;
    private const int MaximumStreams = 32768;

    internal static BluRayPlaylist Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 58 or > MaximumFileBytes)
            throw new InvalidDataException("The MPLS file size is outside the inspection limit.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("The MPLS file changed during inspection.");
        return Parse(bytes);
    }

    internal static BluRayPlaylist Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 58 or > MaximumFileBytes)
            throw new InvalidDataException("The MPLS file size is outside the inspection limit.");
        var header = new Cursor(bytes);
        if (!header.Take(4).SequenceEqual("MPLS"u8)) throw new InvalidDataException("Not an MPLS playlist.");
        var version = Encoding.ASCII.GetString(header.Take(4));
        if (version is not ("0100" or "0200" or "0300"))
            throw new InvalidDataException("Unsupported MPLS version.");
        var playlistOffset = header.UInt32();
        var marksOffset = header.UInt32();
        var extensionOffset = header.UInt32();
        header.Take(20);

        var features = BluRayPlaylistFeatures.None;
        var application = header.Section32();
        application.Byte();
        var playbackType = application.Byte();
        if (playbackType is < 1 or > 3) throw new InvalidDataException("Invalid playlist playback type.");
        var playbackCount = application.UInt16();
        if (playbackType != 1) features |= BluRayPlaylistFeatures.NonSequential;
        var operations = application.UInt64();
        var applicationFlags = application.UInt16();
        CheckRemainder(application, ref features);

        var playlist = SectionAt(bytes, playlistOffset, header.Position, out var playlistEnd);
        var marks = SectionAt(bytes, marksOffset, playlistEnd, out var marksEnd);
        if (extensionOffset != 0)
        {
            _ = SectionAt(bytes, extensionOffset, marksEnd, out _);
            features |= BluRayPlaylistFeatures.ExtendedData;
        }
        else CheckRemainder(new Cursor(bytes[marksEnd..]), ref features);

        playlist.UInt16();
        var itemCount = playlist.UInt16();
        var subPathCount = playlist.UInt16();
        if (itemCount > MaximumPlayItems || subPathCount > MaximumPlayItems)
            throw new InvalidDataException("Too many playlist items or subpaths.");
        var items = new BluRayPlayItem[itemCount];
        long duration = 0;
        var streamBudget = MaximumStreams;
        for (var index = 0; index < itemCount; index++)
        {
            items[index] = ReadItem(playlist.Section16(), duration, ref features, ref streamBudget);
            duration = checked(duration + items[index].DurationTicks);
        }
        if (subPathCount != 0) features |= BluRayPlaylistFeatures.SubPaths;
        for (var index = 0; index < subPathCount; index++)
        {
            // Subpath navigation is not implemented. Bound the containing record,
            // retain the feature flag and prevent it from becoming a linear map.
            var subPath = playlist.Section32();
            subPath.Take(6);
        }
        CheckRemainder(playlist, ref features);

        var markCount = marks.UInt16();
        if (markCount > marks.Remaining / 14) throw new InvalidDataException("Truncated playlist marks.");
        var resultMarks = new BluRayMark[markCount];
        for (var index = 0; index < markCount; index++)
        {
            marks.Byte();
            var type = marks.Byte();
            var itemIndex = marks.UInt16();
            var time = marks.UInt32();
            var packetId = marks.UInt16();
            var markDuration = marks.UInt32();
            if (itemIndex >= items.Length) throw new InvalidDataException("A mark references a missing play item.");
            var item = items[itemIndex];
            if (time < item.InTime || time > item.OutTime)
                throw new InvalidDataException("A mark is outside its clip interval.");
            if (type is not (1 or 2)) features |= BluRayPlaylistFeatures.UnknownMarks;
            resultMarks[index] = new BluRayMark(type, itemIndex, time, packetId, markDuration,
                item.StartTime + time - item.InTime);
        }
        CheckRemainder(marks, ref features);
        return new BluRayPlaylist(version, playbackType, playbackType == 1 ? (ushort)0 : playbackCount,
            operations, applicationFlags, subPathCount, Array.AsReadOnly(items),
            Array.AsReadOnly(resultMarks), duration, features);
    }

    private static BluRayPlayItem ReadItem(
        Cursor item, long start, ref BluRayPlaylistFeatures features, ref int streamBudget)
    {
        var clip = ReadClip(ref item);
        var flags = item.UInt16();
        var connection = (byte)(flags & 15);
        if (connection is not (1 or 5 or 6)) features |= BluRayPlaylistFeatures.UnknownConnections;
        clip = clip with { StcId = item.Byte() };
        var inTime = item.UInt32();
        var outTime = item.UInt32();
        if (outTime < inTime) throw new InvalidDataException("A clip ends before its in point.");
        var operations = item.UInt64();
        var randomAccess = (item.Byte() & 0x80) != 0;
        var stillMode = item.Byte();
        var stillTime = item.UInt16();
        if (stillMode != 0) features |= BluRayPlaylistFeatures.StillFrames;
        var clips = new List<BluRayClip> { clip };
        byte angleFlags = 0;
        if ((flags & 0x10) != 0)
        {
            features |= BluRayPlaylistFeatures.MultiAngle;
            var angles = item.Byte();
            angleFlags = item.Byte();
            if (angles == 0) throw new InvalidDataException("A multi-angle item has no angles.");
            for (var angle = 1; angle < angles; angle++)
            {
                var alternate = ReadClip(ref item);
                clips.Add(alternate with { StcId = item.Byte() });
            }
        }
        if (clips.Any(value => value.Codec != "M2TS")) features |= BluRayPlaylistFeatures.UnknownCodecTypes;
        var streams = ReadStreams(item.Section16(), ref features, ref streamBudget);
        CheckRemainder(item, ref features);
        return new BluRayPlayItem(clips.AsReadOnly(), inTime, outTime, start, connection,
            operations, randomAccess, stillMode, stillTime, angleFlags, streams);
    }

    private static BluRayClip ReadClip(ref Cursor cursor)
    {
        var id = cursor.Take(5);
        foreach (var value in id)
            if (value is < (byte)'0' or > (byte)'9') throw new InvalidDataException("Invalid Blu-ray clip identifier.");
        var codec = cursor.Take(4);
        foreach (var value in codec)
            if (value is < 0x20 or > 0x7e) throw new InvalidDataException("Invalid Blu-ray clip codec identifier.");
        return new BluRayClip(Encoding.ASCII.GetString(id), Encoding.ASCII.GetString(codec), 0);
    }

    private static IReadOnlyList<BluRayStream> ReadStreams(
        Cursor table, ref BluRayPlaylistFeatures features, ref int streamBudget)
    {
        table.UInt16();
        var counts = table.Take(8);
        table.Take(4);
        var count = 0;
        foreach (var value in counts) count += value;
        if (count > streamBudget) throw new InvalidDataException("Too many playlist streams.");
        streamBudget -= count;
        var streams = new List<BluRayStream>(count);
        if (counts[3] != 0) features |= BluRayPlaylistFeatures.InteractiveGraphics;
        if (counts[4] + counts[5] + counts[6] + counts[7] != 0)
            features |= BluRayPlaylistFeatures.SecondaryStreams;
        for (var group = 0; group < 8; group++)
        {
            // PIP presentation graphics precede interactive graphics in the binary table.
            var field = group switch { 3 => 6, 4 => 3, 5 => 4, 6 => 5, _ => group };
            var kind = field switch
            {
                0 => "Video", 1 => "Audio", 2 => "PresentationGraphics", 3 => "InteractiveGraphics",
                4 => "SecondaryAudio", 5 => "SecondaryVideo", 6 => "PipGraphics", _ => "DolbyVision"
            };
            for (var index = 0; index < counts[field]; index++)
            {
                streams.Add(ReadStream(ref table, kind, ref features));
                if (field == 4) ReadReferences(ref table, counts[1]);
                if (field == 5)
                {
                    ReadReferences(ref table, counts[4]);
                    ReadReferences(ref table, counts[6]);
                }
            }
        }
        CheckRemainder(table, ref features);
        return streams.AsReadOnly();
    }

    private static void ReadReferences(ref Cursor table, int available)
    {
        var count = table.Byte();
        table.Byte();
        foreach (var reference in table.Take(count))
            if (reference >= available) throw new InvalidDataException("Invalid secondary stream reference.");
        if ((count & 1) != 0) table.Byte();
    }

    private static BluRayStream ReadStream(ref Cursor table, string kind, ref BluRayPlaylistFeatures features)
    {
        var entry = table.Section8();
        var type = entry.Byte();
        ushort? packetId = null;
        byte? subPath = null, subClip = null;
        switch (type)
        {
            case 1: packetId = entry.UInt16(); break;
            case 2: subPath = entry.Byte(); subClip = entry.Byte(); packetId = entry.UInt16(); break;
            case 3 or 4: subPath = entry.Byte(); packetId = entry.UInt16(); break;
            default: features |= BluRayPlaylistFeatures.UnknownStreamTypes; break;
        }
        CheckRemainder(entry, ref features);
        var attributes = table.Section8();
        var coding = attributes.Byte();
        byte? format = null, rate = null;
        string? language = null;
        switch (coding)
        {
            case 0x01 or 0x02 or 0xea or 0x1b or 0x24:
                var video = attributes.Byte();
                format = (byte)(video >> 4); rate = (byte)(video & 15);
                if (coding == 0x24) attributes.Take(2); // HDR/color information remains metadata only.
                break;
            case 0x03 or 0x04 or 0x80 or 0x81 or 0x82 or 0x83 or 0x84 or 0x85 or 0x86 or 0xa1 or 0xa2:
                var audio = attributes.Byte();
                format = (byte)(audio >> 4); rate = (byte)(audio & 15);
                language = ReadLanguage(ref attributes);
                break;
            case 0x90 or 0x91: language = ReadLanguage(ref attributes); break;
            case 0x92: attributes.Byte(); language = ReadLanguage(ref attributes); break;
            default: features |= BluRayPlaylistFeatures.UnknownCodecTypes; break;
        }
        CheckRemainder(attributes, ref features);
        return new BluRayStream(kind, type, packetId, subPath, subClip, coding, language, format, rate);
    }

    private static string? ReadLanguage(ref Cursor attributes)
    {
        var language = attributes.Take(3);
        if (language.IndexOfAnyExcept((byte)0) < 0) return null;
        foreach (var value in language)
            if (value is < (byte)'a' or > (byte)'z') throw new InvalidDataException("Invalid stream language code.");
        return Encoding.ASCII.GetString(language);
    }

    private static Cursor SectionAt(ReadOnlySpan<byte> bytes, uint offset, int minimum, out int end)
    {
        if (offset < minimum || offset > bytes.Length - 4)
            throw new InvalidDataException("Invalid or overlapping MPLS section offsets.");
        var cursor = new Cursor(bytes[(int)offset..]);
        var section = cursor.Section32();
        end = checked((int)offset + cursor.Position);
        return section;
    }

    private static void CheckRemainder(Cursor cursor, ref BluRayPlaylistFeatures features)
    {
        if (cursor.Take(cursor.Remaining).IndexOfAnyExcept((byte)0) >= 0)
            features |= BluRayPlaylistFeatures.UnparsedData;
    }

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        internal int Position { get; private set; }
        internal int Remaining => _bytes.Length - Position;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > Remaining) throw new InvalidDataException("Truncated MPLS record.");
            var result = _bytes.Slice(Position, count);
            Position += count;
            return result;
        }
        internal byte Byte() => Take(1)[0];
        internal ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        internal uint UInt32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
        internal ulong UInt64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));
        internal Cursor Section8() => new(Take(Byte()));
        internal Cursor Section16() => new(Take(UInt16()));
        internal Cursor Section32()
        {
            var length = UInt32();
            if (length > Remaining) throw new InvalidDataException("Truncated MPLS section.");
            return new Cursor(Take((int)length));
        }
    }
}
