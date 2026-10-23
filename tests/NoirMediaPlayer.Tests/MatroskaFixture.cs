using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NoirMediaPlayer.Tests;

internal static class MatroskaFixture
{
    internal static byte[] Number(ulong value)
    {
        var bytes = BitConverter.GetBytes(value).Reverse().ToArray();
        var start = 0;
        while (start < bytes.Length - 1 && bytes[start] == 0) start++;
        return bytes[start..];
    }
    internal static byte[] Element(uint id, byte[] data)
    {
        var sizeBytes = 1;
        while ((ulong)data.Length >= (1ul << (sizeBytes * 7)) - 1) sizeBytes++;
        var size = Number((1ul << (sizeBytes * 7)) | (uint)data.Length);
        return Number(id).Concat(size).Concat(data).ToArray();
    }
    internal static byte[] UInt(uint id, ulong value) => Element(id, Number(value));
    internal static byte[] Text(uint id, string value) => Element(id, Encoding.UTF8.GetBytes(value));
    private static byte[] Float(uint id, double value) => Element(id, BitConverter.GetBytes(value).Reverse().ToArray());
    internal static byte[] Group(uint id, params byte[][] children) => Element(id, children.SelectMany(child => child).ToArray());

    internal static byte[] SimpleChapters() => Group(0x1043A770, Group(0x45B9,
        Group(0xB6, UInt(0x73C4, 1), UInt(0x91, 0), Group(0x80, Text(0x85, "Introduction"))),
        Group(0xB6, UInt(0x73C4, 2), UInt(0x91, 4_000_000_000), Group(0x80, Text(0x85, "日本語 café"))),
        Group(0xB6, UInt(0x73C4, 3), UInt(0x91, 8_000_000_000), Group(0x80, Text(0x85, "Finale")))));

    // Complete first-party Matroska with PCM audio, optional captions and chapters.
    internal static void Write(string path, bool subtitles, byte[]? chapters = null, byte[]? infoExtras = null, bool video = false, bool chaptersAtEnd = false, int audioTracks = 1,
        string subtitleText = "Embedded captions", int subtitleTracks = 1, string? laterSubtitleText = null,
        int laterSubtitleSecond = 5, bool subtitleDefault = true, bool subtitleForced = false)
    {
        if (audioTracks is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(audioTracks));
        if (subtitleTracks is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(subtitleTracks));
        var header = Group(0x1A45DFA3, UInt(0x4286, 1), UInt(0x42F7, 1), UInt(0x42F2, 4), UInt(0x42F3, 8),
            Text(0x4282, "matroska"), UInt(0x4287, 4), UInt(0x4285, 2));
        var info = Group(0x1549A966, UInt(0x2AD7B1, 1_000_000), Float(0x4489, 20_000), Text(0x4D80, "NOIR tests"), Text(0x5741, "NOIR tests"), infoExtras ?? []);
        var audio = Group(0xAE, UInt(0xD7, 1), UInt(0x73C5, 1), UInt(0x83, 2), Text(0x86, "A_PCM/INT/LIT"),
            Group(0xE1, Float(0xB5, 8000), UInt(0x9F, 1), UInt(0x6264, 16)));
        var tracks = new List<byte[]> { audio };
        if (audioTracks == 2)
            tracks.Add(Group(0xAE, UInt(0xD7, 2), UInt(0x73C5, 2), UInt(0x83, 2), Text(0x86, "A_PCM/INT/LIT"),
                Text(0x536E, "Alternate audio"), Text(0x22B59C, "fra"),
                Group(0xE1, Float(0xB5, 8000), UInt(0x9F, 1), UInt(0x6264, 16))));
        var videoTrack = (uint)audioTracks + 1;
        var subtitleTrack = (uint)audioTracks + (video ? 2u : 1u);
        byte[] jpeg = [];
        if (video)
        {
            tracks.Add(Group(0xAE, UInt(0xD7, videoTrack), UInt(0x73C5, videoTrack), UInt(0x83, 1), Text(0x86, "V_MJPEG"),
                UInt(0x23E383, 500_000_000), Group(0xE0, UInt(0xB0, 320), UInt(0xBA, 240))));
            var pixels = new byte[320 * 240 * 3];
            for (var index = 0; index < pixels.Length; index += 3) { pixels[index] = 200; pixels[index + 1] = 50; pixels[index + 2] = 20; }
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 240, 96, 96, PixelFormats.Bgr24, null, pixels, 320 * 3)));
            using var encoded = new MemoryStream();
            encoder.Save(encoded);
            jpeg = encoded.ToArray();
        }
        if (subtitles)
        {
            tracks.Add(Group(0xAE, UInt(0xD7, subtitleTrack), UInt(0x73C5, audioTracks == 1 ? 3u : subtitleTrack), UInt(0x83, 17), Text(0x86, "S_TEXT/UTF8"), Text(0x536E, "English captions"), Text(0x22B59C, "eng"),
                subtitleDefault ? [] : UInt(0x88, 0), subtitleForced ? UInt(0x55AA, 1) : []));
            if (subtitleTracks == 2)
                tracks.Add(Group(0xAE, UInt(0xD7, subtitleTrack + 1), UInt(0x73C5, subtitleTrack + 2), UInt(0x83, 17), Text(0x86, "S_TEXT/UTF8"), Text(0x536E, "Alternate captions"), Text(0x22B59C, "fra")));
        }
        var clusters = new List<byte[]>();
        for (var second = 0; second < 20; second++)
        {
            var blocks = new List<byte[]> { UInt(0xE7, (ulong)second * 1000), Element(0xA3, new byte[] { 0x81, 0, 0, 0x80 }.Concat(new byte[16000]).ToArray()) };
            if (audioTracks == 2)
                blocks.Add(Element(0xA3, new byte[] { 0x82, 0, 0, 0x80 }.Concat(new byte[16000]).ToArray()));
            if (video)
            {
                blocks.Add(Element(0xA3, new byte[] { (byte)(0x80 | videoTrack), 0, 0, 0x80 }.Concat(jpeg).ToArray()));
                blocks.Add(Element(0xA3, new byte[] { (byte)(0x80 | videoTrack), 1, 244, 0x80 }.Concat(jpeg).ToArray()));
            }
            if (subtitles && second == 0)
            {
                blocks.Add(Group(0xA0, Element(0xA1, new byte[] { (byte)(0x80 | subtitleTrack), 0, 0, 0 }.Concat(Encoding.UTF8.GetBytes(subtitleText)).ToArray()), UInt(0x9B, 3000)));
                if (subtitleTracks == 2)
                    blocks.Add(Group(0xA0, Element(0xA1, new byte[] { (byte)(0x80 | (subtitleTrack + 1)), 0, 0, 0 }.Concat(Encoding.UTF8.GetBytes("Alternate embedded captions")).ToArray()), UInt(0x9B, 3000)));
            }
            if (subtitles && second == laterSubtitleSecond && laterSubtitleText is not null)
                blocks.Add(Group(0xA0, Element(0xA1, new byte[] { (byte)(0x80 | subtitleTrack), 0, 0, 0 }.Concat(Encoding.UTF8.GetBytes(laterSubtitleText)).ToArray()), UInt(0x9B, 3000)));
            clusters.Add(Group(0x1F43B675, blocks.ToArray()));
        }
        var trackData = Group(0x1654AE6B, tracks.ToArray());
        // Fixed-width seek offsets keep the index size stable while positions are computed.
        byte[] SeekHead(long cuesPosition) => Group(0x114D9B74, Group(0x4DBB,
            Element(0x53AB, Number(0x1C53BB6B)), Element(0x53AC, BitConverter.GetBytes((ulong)cuesPosition).Reverse().ToArray())));
        var seekHead = SeekHead(0);
        var offset = seekHead.Length + info.Length + trackData.Length + (chaptersAtEnd ? 0 : chapters?.Length ?? 0);
        var points = new List<byte[]>();
        for (var second = 0; second < clusters.Count; second++)
        {
            points.Add(Group(0xBB, UInt(0xB3, (ulong)second * 1000), Group(0xB7, UInt(0xF7, 1), UInt(0xF1, (ulong)offset))));
            offset += clusters[second].Length;
        }
        seekHead = SeekHead(offset);
        var segment = Group(0x18538067, seekHead, info, trackData, chaptersAtEnd ? [] : chapters ?? [],
            clusters.SelectMany(cluster => cluster).ToArray(), Group(0x1C53BB6B, points.ToArray()), chaptersAtEnd ? chapters ?? [] : []);
        File.WriteAllBytes(path, header.Concat(segment).ToArray());
    }
}
