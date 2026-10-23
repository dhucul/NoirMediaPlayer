using System.Buffers.Binary;
using System.Text;

namespace NoirMediaPlayer.Tests;

internal static class BluRayFixture
{
    internal static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
    internal static byte[] U16(ushort value)
    {
        var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return bytes;
    }
    internal static byte[] U32(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes;
    }
    internal static byte[] Sized16(byte[] payload) => Join(U16(checked((ushort)payload.Length)), payload);
    internal static byte[] Sized32(byte[] payload) => Join(U32((uint)payload.Length), payload);
    internal static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);

    internal static byte[] Stream(ushort pid, byte coding, byte[] attributes, byte type = 1)
    {
        var entry = type switch
        {
            1 => Join([1], U16(pid), new byte[6]),
            2 => Join([2, 3, 4], U16(pid), new byte[4]),
            3 or 4 => Join([type, 3], U16(pid), new byte[5]),
            _ => new byte[] { type }
        };
        var descriptor = Join([coding], attributes);
        return Join([(byte)entry.Length], entry, [(byte)descriptor.Length], descriptor);
    }

    internal static byte[] Table(
        byte[][]? video = null, byte[][]? audio = null, byte[][]? pg = null, byte[][]? ig = null,
        byte[][]? secondaryAudio = null, byte[][]? secondaryVideo = null, byte[][]? pip = null, byte[][]? dv = null)
    {
        video ??= []; audio ??= []; pg ??= []; ig ??= [];
        secondaryAudio ??= []; secondaryVideo ??= []; pip ??= []; dv ??= [];
        return Sized16(Join(new byte[2],
            [(byte)video.Length, (byte)audio.Length, (byte)pg.Length, (byte)ig.Length,
                (byte)secondaryAudio.Length, (byte)secondaryVideo.Length, (byte)pip.Length, (byte)dv.Length],
            new byte[4], Join(video), Join(audio), Join(pg), Join(pip), Join(ig),
            Join(secondaryAudio), Join(secondaryVideo), Join(dv)));
    }

    internal static byte[] Item(
        string clip = "00001", uint input = 45000, uint output = 495000,
        byte[]? table = null, byte connection = 5, string[]? angles = null,
        byte stillMode = 0, ushort stillTime = 0, string codec = "M2TS", ulong operations = 0)
    {
        var mask = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(mask, operations);
        var angleData = angles is null ? [] : Join([(byte)(angles.Length + 1), 3],
            Join(angles.Select(value => Join(Text(value + "M2TS"), [7])).ToArray()));
        return Sized16(Join(Text(clip + codec), U16((ushort)(connection | (angles is null ? 0 : 16))),
            [9], U32(input), U32(output), mask, [0x80, stillMode], U16(stillTime),
            angleData, table ?? Table()));
    }

    internal static byte[] Mark(ushort item = 0, uint time = 45000, byte type = 1, uint duration = 0) =>
        Join([0, type], U16(item), U32(time), U16(0x1011), U32(duration));

    internal static byte[] Playlist(
        byte[][]? items = null, byte[][]? marks = null, byte playbackType = 1,
        byte[][]? subPaths = null, byte[]? extension = null, string version = "0200")
    {
        items ??= [Item()]; marks ??= [Mark()]; subPaths ??= [];
        var application = Sized32(Join([0, playbackType], U16(3), new byte[8], [0xc0, 0]));
        var playlist = Sized32(Join(new byte[2], U16((ushort)items.Length), U16((ushort)subPaths.Length),
            Join(items), Join(subPaths.Select(Sized32).ToArray())));
        var markSection = Sized32(Join(U16((ushort)marks.Length), Join(marks)));
        var playlistOffset = 40 + application.Length;
        var markOffset = playlistOffset + playlist.Length;
        var extensionOffset = extension is null ? 0 : markOffset + markSection.Length;
        return Join(Text("MPLS" + version), U32((uint)playlistOffset), U32((uint)markOffset),
            U32((uint)extensionOffset), new byte[20], application, playlist, markSection,
            extension is null ? [] : Sized32(extension));
    }
}
