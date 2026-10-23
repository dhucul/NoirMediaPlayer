using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

/// <summary>First-party MP4 chapter reader; skips encoded audio/video and reads only chapter metadata/text.</summary>
internal static partial class Mp4Chapters
{
    internal static NativeChapterMap? TryRead(string path)
        => TryRead(path, out _);

    internal static NativeChapterMap? TryRead(string path, out bool requiresCompatibility)
    {
        requiresCompatibility = false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
            return TryRead(file, out requiresCompatibility);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static NativeChapterMap? TryRead(Stream file, out bool requiresCompatibility)
    {
        requiresCompatibility = false;
        try
        {
            if (!file.CanRead || !file.CanSeek) return null;
            file.Position = 0;
            var reader = new Reader(file);
            long? duration = null;
            uint movieScale = 0;
            long[]? nero = null, highlights = null;
            var movieFound = false;
            var tracks = new List<ChapterTrack>();
            var media = new List<Box>();
            var userDataFound = false;
            foreach (var root in reader.Children(file.Length))
            {
                // Fragmented and compressed movies need the compatibility chapter reader.
                if (root.Type is "moof" or "cmov") return null;
                if (root.Type == "mdat") media.Add(root);
                if (root.Type != "moov") continue;
                if (movieFound) return null;
                movieFound = true;
                foreach (var box in reader.Children(root.End))
                {
                    switch (box.Type)
                    {
                        case "cmov":
                        case "mvex": return null;
                        case "mvhd":
                            if (duration is not null) return null;
                            duration = ReadDuration(reader.Payload(box, 112), out movieScale);
                            break;
                        case "trak":
                            if (tracks.Count >= 256) return null;
                            tracks.Add(ReadTrack(reader, box));
                            break;
                        case "udta":
                            if (userDataFound) return null;
                            userDataFound = true;
                            foreach (var data in reader.Children(box.End))
                            {
                                if (data.Type == "chpl")
                                {
                                    if (nero is not null) return null;
                                    nero = ReadNero(reader.Payload(data, 70_000));
                                }
                                else if (data.Type == "HMMT")
                                {
                                    if (highlights is not null) return null;
                                    highlights = ReadHighlights(reader.Payload(data, 404));
                                }
                            }
                            break;
                    }
                }
            }
            if (!movieFound || duration is null || tracks.Count == 0) return null;
            // Enabled chapter tracks may also present visible text or pictures.
            // Keep the full compatibility renderer from the start in that case.
            requiresCompatibility = tracks.SelectMany(track => track.References)
                .Any(id => tracks.Any(track => track.Id == id && (track.Flags & 1) != 0));
            if (requiresCompatibility) return null;
            var quickTime = ReadQuickTime(reader, tracks, media, movieScale);
            // The compatibility backend gives a nonempty Nero list precedence over
            // GoPro HiLight tags when both representations are present.
            var starts = nero is { Length: > 0 } ? nero : highlights is { Length: > 0 } ? highlights : quickTime;
            if (starts.Any(start => start >= duration)) return null;
            // MP4 exposes its first chapter as current even before that marker.
            return new NativeChapterMap(starts, firstChapterBeforeStart: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private static long ReadDuration(ReadOnlySpan<byte> data, out uint scale)
    {
        if (data.Length < 4 || data[1] != 0 || data[2] != 0 || data[3] != 0) throw new InvalidDataException();
        ulong duration;
        switch (data[0])
        {
            case 0 when data.Length == 100:
                scale = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
                duration = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
                if (duration == uint.MaxValue) throw new NotSupportedException();
                break;
            case 1 when data.Length == 112:
                scale = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
                duration = BinaryPrimitives.ReadUInt64BigEndian(data[24..]);
                if (duration == ulong.MaxValue) throw new NotSupportedException();
                break;
            default: throw new NotSupportedException();
        }
        if (scale == 0 || duration == 0) throw new InvalidDataException();
        // Divide before multiplying so long, high-timescale movies do not overflow.
        return checked((long)(duration / scale) * 1000 + (long)(duration % scale * 1000 / scale));
    }

    private static long[] ReadNero(ReadOnlySpan<byte> data)
    {
        // Version 1: full-box header, reserved word, byte count, then entries:
        // unsigned 64-bit start in 100 ns units, byte title length, title bytes.
        // Version 0 is not supported by the retained VLC 3 backend either.
        if (data.Length < 9 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0x01000000 ||
            BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != 0) throw new NotSupportedException();
        var starts = new long[data[8]];
        var offset = 9;
        for (var index = 0; index < starts.Length; index++)
        {
            if (data.Length - offset < 9) throw new InvalidDataException();
            var ticks = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]);
            if (ticks > long.MaxValue) throw new NotSupportedException();
            starts[index] = (long)(ticks / 10_000);
            offset += 9 + data[offset + 8];
            if (offset > data.Length) throw new InvalidDataException();
        }
        if (offset != data.Length) throw new InvalidDataException();
        // Nero lists are ordered by time in compatibility playback.
        Array.Sort(starts);
        ValidateOrder(starts);
        return starts;
    }

    private static long[] ReadHighlights(ReadOnlySpan<byte> data)
    {
        // GoPro HiLight tags: 32-bit count followed by 32-bit millisecond starts.
        if (data.Length < 4) throw new InvalidDataException();
        var count = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (count > 100 || data.Length != 4 + count * 4) throw new NotSupportedException();
        var starts = new long[count];
        for (var index = 0; index < starts.Length; index++)
            starts[index] = BinaryPrimitives.ReadUInt32BigEndian(data[(4 + index * 4)..]);
        ValidateOrder(starts);
        return starts;
    }

    private static void ValidateOrder(long[] starts)
    {
        for (var index = 1; index < starts.Length; index++)
            if (starts[index] <= starts[index - 1]) throw new NotSupportedException("Ambiguous chapter ordering or precision.");
    }

    private readonly record struct Box(string Type, long Start, long End);

    private sealed class Reader(Stream file)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private int _boxes;
        private int _metadataBytes;

        internal long Position { get => file.Position; set => file.Position = value; }

        private void CheckBudget()
        {
            if (_clock.Elapsed > TimeSpan.FromSeconds(2)) throw new NotSupportedException("Metadata time limit reached.");
        }

        internal IEnumerable<Box> Children(long end)
        {
            while (file.Position < end)
            {
                var box = ReadBox(end);
                yield return box;
                file.Position = box.End;
            }
        }

        private Box ReadBox(long parentEnd)
        {
            CheckBudget();
            if (++_boxes > 100_000) throw new NotSupportedException("Metadata limit reached.");
            var start = file.Position;
            if (parentEnd - start < 8) throw new InvalidDataException();
            Span<byte> header = stackalloc byte[8];
            file.ReadExactly(header);
            var size = (ulong)BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.Latin1.GetString(header[4..]);
            var headerSize = 8;
            if (size == 1)
            {
                if (parentEnd - file.Position < 8) throw new InvalidDataException();
                file.ReadExactly(header);
                size = BinaryPrimitives.ReadUInt64BigEndian(header);
                headerSize = 16;
            }
            else if (size == 0)
            {
                if (parentEnd != file.Length) throw new NotSupportedException();
                size = (ulong)(file.Length - start);
            }
            if (size < (ulong)headerSize || size > (ulong)(parentEnd - start)) throw new InvalidDataException();
            return new Box(type, file.Position, start + (long)size);
        }

        internal byte[] Payload(Box box, int maximum)
        {
            var size = box.End - file.Position;
            if (size > maximum) throw new NotSupportedException("Oversized chapter metadata.");
            CheckBudget();
            _metadataBytes = checked(_metadataBytes + (int)size);
            if (_metadataBytes > 4 * 1024 * 1024) throw new NotSupportedException("Metadata byte limit reached.");
            var data = new byte[(int)size];
            file.ReadExactly(data);
            return data;
        }

        internal byte[] PayloadAt(Box box, int maximum)
        {
            file.Position = box.Start;
            return Payload(box, maximum);
        }

        internal ushort TitleLength(long position)
        {
            CheckBudget();
            file.Position = position;
            Span<byte> length = stackalloc byte[2];
            file.ReadExactly(length);
            return BinaryPrimitives.ReadUInt16BigEndian(length);
        }
    }
}
