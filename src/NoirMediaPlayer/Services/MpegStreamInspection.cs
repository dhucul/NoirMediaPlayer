using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

internal sealed record MpegStreamInventory(int VideoCount, int AudioCount);

// Bounded, read-only container metadata inspection (ISO/IEC 13818-1). Windows
// performs all demuxing, decoding and presentation. Reject streams its inventory
// might omit, including disc subpictures/private audio, encryption and extra tracks.
internal static class MpegStreamInspection
{
    internal static bool SupportsExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".mpg" or ".mpeg" or ".vob";

    internal static MpegStreamInventory? TryRead(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            if (file.Length < 16) return null;
            var data = new byte[(int)Math.Min(file.Length, 1024 * 1024)];
            file.ReadExactly(data);
            return data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 1, 0xba }) ? Program(data, file.Length == data.Length) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private static MpegStreamInventory? Program(ReadOnlySpan<byte> data, bool complete)
    {
        MpegStreamInventory? inventory = null;
        byte[]? firstMap = null;
        HashSet<byte> streamIds = [];
        var clock = Stopwatch.StartNew();
        for (var offset = 0; offset + 6 <= data.Length;)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(1)) return null;
            var packet = data[offset..];
            if (!packet[..3].SequenceEqual(new byte[] { 0, 0, 1 })) return null;
            var id = packet[3];
            if (id == 0xb9) break;
            if (id == 0xba)
            {
                if (packet.Length < 14 || (packet[4] & 0xc4) != 0x44) return null;
                offset += 14 + (packet[13] & 7);
                if (offset > data.Length) return null;
                continue;
            }
            var length = 6 + BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            if (length == 6) return null; // Unbounded PES is not validated here.
            if (length > packet.Length)
            {
                if (complete) return null;
                break; // End of the bounded scan, not a media read.
            }
            packet = packet[..length];
            if (id == 0xbc)
            {
                if (firstMap is not null && !packet.SequenceEqual(firstMap)) return null;
                firstMap = packet.ToArray();
                inventory = ProgramMap(packet, out streamIds);
                if (inventory is null) return null;
            }
            else if (id is 0xbd or 0xbf) return null; // DVD/private audio, subpictures and navigation.
            else if (id >= 0xc0 && id <= 0xef)
            {
                if (!streamIds.Contains(id) || packet.Length < 9 || 9 + packet[8] > packet.Length ||
                    (packet[6] & 0xc0) != 0x80 || (packet[6] & 0x30) != 0) return null;
            }
            else if (id is not (0xbb or 0xbe)) return null;
            offset += length;
        }
        return inventory;
    }

    private static MpegStreamInventory? ProgramMap(ReadOnlySpan<byte> packet, out HashSet<byte> ids)
    {
        ids = [];
        if (packet.Length < 16 || (packet[6] & 0x80) == 0 || Crc(packet) != 0) return null;
        var infoLength = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
        var offset = 10 + infoLength;
        if (offset + 2 > packet.Length - 4 || !Descriptors(packet.Slice(10, infoLength))) return null;
        var end = offset + 2 + BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
        offset += 2;
        if (end != packet.Length - 4) return null;
        var streams = new List<byte>();
        while (offset < end)
        {
            if (end - offset < 4 || !ids.Add(packet[offset + 1])) return null;
            var type = packet[offset];
            var id = packet[offset + 1];
            if (IsVideo(type) ? id is < 0xe0 or > 0xef : id is < 0xc0 or > 0xdf) return null;
            var descriptorLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
            offset += 4;
            if (descriptorLength > end - offset || !Descriptors(packet.Slice(offset, descriptorLength))) return null;
            streams.Add(type);
            offset += descriptorLength;
        }
        return Inventory(streams);
    }

    private static bool IsVideo(byte type) => type is 1 or 2 or 0x1b;
    private static MpegStreamInventory? Inventory(List<byte> streams)
    {
        var video = streams.Count(IsVideo);
        var audio = streams.Count(type => type is 3 or 4 or 0x0f or 0x11 or 0x81 or 0x87);
        // Extra tracks retain their proven compatibility selection,
        // until Windows switching and seeking are verified for those layouts.
        return video == 1 && audio <= 1 && video + audio == streams.Count ? new(video, audio) : null;
    }

    private static bool Descriptors(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (data.Length < 2 || data[1] > data.Length - 2 || data[0] is 9 or 0x56 or 0x59 or 0x86) return false;
            data = data[(2 + data[1])..];
        }
        return true;
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc;
    }
}
