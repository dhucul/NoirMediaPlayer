using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NoirMediaPlayer.Tests;

internal static class Mp4Fixture
{
    internal static byte[] UInt(uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        return data;
    }

    internal static byte[] Atom(string type, params byte[][] children)
    {
        var payload = children.SelectMany(child => child).ToArray();
        return UInt((uint)(8 + payload.Length)).Concat(Encoding.ASCII.GetBytes(type)).Concat(payload).ToArray();
    }

    internal static byte[] Nero(params long[] starts)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, (byte)starts.Length });
        foreach (var start in starts)
        {
            var ticks = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(ticks, (ulong)start * 10_000);
            stream.Write(ticks);
            var title = Encoding.UTF8.GetBytes(start == 4000 ? "日本語 café" : "Chapter");
            stream.WriteByte((byte)title.Length);
            stream.Write(title);
        }
        return Atom("chpl", stream.ToArray());
    }

    internal static byte[] Highlights(params uint[] starts) =>
        Atom("HMMT", UInt((uint)starts.Length), starts.SelectMany(value => UInt(value)).ToArray());

    internal static byte[] MovieHeader(bool versionOne = false, uint scale = 1000, ulong duration = 20_000)
    {
        var data = new byte[versionOne ? 112 : 100];
        data[0] = versionOne ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(versionOne ? 20 : 12), scale);
        if (versionOne) BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(24), duration);
        else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)duration);
        return Atom("mvhd", data);
    }

    internal static byte[] MetadataMovie(byte[]? chapters = null, byte[]? trackExtras = null, byte[]? header = null) =>
        Atom("moov", header ?? MovieHeader(), Atom("trak", trackExtras ?? []), Atom("udta", chapters ?? []));

    // Append metadata to the final moov atom. Media offsets remain unchanged, so
    // tests exercise a genuine Windows-encoded MP4 rather than a renamed AVI.
    private static void AddChapters(string path, byte[] chapters)
    {
        var data = File.ReadAllBytes(path);
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
            if (size < 8 || offset + size > data.Length) throw new InvalidDataException();
            if (data.AsSpan(offset + 4, 4).SequenceEqual("moov"u8))
            {
                if (offset + size != data.Length) throw new InvalidDataException("Fixture requires a final moov atom.");
                var payload = data[(offset + 8)..];
                var userData = 0;
                while (userData + 8 <= payload.Length)
                {
                    var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(userData)));
                    if (payload.AsSpan(userData + 4, 4).SequenceEqual("udta"u8))
                    {
                        payload = payload[..userData].Concat(Atom("udta", payload[(userData + 8)..(userData + length)], chapters))
                            .Concat(payload[(userData + length)..]).ToArray();
                        File.WriteAllBytes(path, data[..offset].Concat(Atom("moov", payload)).ToArray());
                        return;
                    }
                    userData += length;
                }
                File.WriteAllBytes(path, data[..offset].Concat(Atom("moov", payload, Atom("udta", chapters))).ToArray());
                return;
            }
            offset += size;
        }
        throw new InvalidDataException("No movie atom in the encoded fixture.");
    }

    internal static void Write(string path, byte[]? chapters = null)
    {
        File.WriteAllBytes(path, Encoded.Value);
        if (chapters is not null) AddChapters(path, chapters);
    }

    internal static void WriteAudio(string path, byte[]? chapters = null)
    {
        // The final moov follows the media data. Removing its video track leaves
        // a valid AAC-only movie without changing any audio sample offsets.
        File.WriteAllBytes(path, Rewrite(Encoded.Value, ["moov", "trak"], _ => [], owner: true));
        if (chapters is not null) AddChapters(path, chapters);
    }

    internal static void WriteVideo(string path) =>
        File.WriteAllBytes(path, Rewrite(Encoded.Value, ["moov", "trak"], _ => [], owner: false));

    internal static void WriteLongVideo(string path) => Encode(path, seconds: 30);

    internal static void WriteOriented(string path, uint rotation, uint aspectNumerator = 1, uint aspectDenominator = 1) =>
        Encode(path, rotation, aspectNumerator, aspectDenominator, quadrants: true);

    // QuickTime chapter lists are disabled text tracks referenced by an enabled
    // audio/video track. Each text sample's display time is a chapter marker.
    internal static void WriteQuickTime(string path, byte[]? extras = null, bool enabled = false, bool fixedSamples = false, byte[]? movieChapters = null, bool classicText = false)
    {
        var file = Encoded.Value;
        var movieOffset = 0;
        while (!file.AsSpan(movieOffset + 4, 4).SequenceEqual("moov"u8))
            movieOffset += checked((int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(movieOffset)));
        var movie = file[(movieOffset + 8)..];
        var scale = BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(8 + 12));
        var header = new byte[84];
        header[3] = enabled ? (byte)3 : (byte)2;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), scale * 20);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(40), 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(56), 0x10000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(72), 0x40000000);
        var mediaHeader = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(mediaHeader.AsSpan(12), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(mediaHeader.AsSpan(16), 20_000);
        BinaryPrimitives.WriteUInt16BigEndian(mediaHeader.AsSpan(20), 0x55c4);
        var samples = new[] { "Introduction", "日本語 café", "Finale" }.Select(title =>
        {
            var text = Encoding.UTF8.GetBytes(title);
            var sample = new byte[2 + text.Length];
            BinaryPrimitives.WriteUInt16BigEndian(sample, (ushort)text.Length);
            text.CopyTo(sample, 2);
            return fixedSamples ? sample.Concat(new byte[128 - sample.Length]).ToArray() : sample;
        }).ToArray();
        var entry = new byte[classicText ? 52 : 38];
        entry[7] = 1; // Self-contained data reference.
        if (classicText)
        {
            entry[39] = 1;
            Array.Fill(entry, (byte)0xff, 45, 6);
        }
        else
        {
            entry[24] = entry[25] = 0xff; // Automatic text box.
            entry[31] = 1; entry[33] = 18;
            Array.Fill(entry, (byte)0xff, 34, 4);
        }
        var table = Atom("stbl",
            Atom("stsd", UInt(0), UInt(1), Atom(classicText ? "text" : "tx3g", entry)),
            Atom("stts", UInt(0), UInt(3), UInt(1), UInt(4000), UInt(1), UInt(4000), UInt(1), UInt(12000)),
            Atom("stsc", UInt(0), UInt(1), UInt(1), UInt(3), UInt(1)),
            Atom("stsz", UInt(0), UInt(fixedSamples ? 128u : 0), UInt(3), fixedSamples ? [] : samples.SelectMany(sample => UInt((uint)sample.Length)).ToArray()),
            Atom("stco", UInt(0), UInt(1), UInt((uint)movieOffset + 8)));
        var chapterTrack = Atom("trak", Atom("tkhd", header), extras ?? [], Atom("mdia",
            Atom("mdhd", mediaHeader), Atom("hdlr", UInt(0), UInt(0), Encoding.ASCII.GetBytes("text"), new byte[12], Encoding.ASCII.GetBytes("Chapter titles\0")),
            Atom("minf", Atom("nmhd", UInt(0)), Atom("dinf", Atom("dref", UInt(0), UInt(1), Atom("url ", UInt(1)))), table)));
        var trackOffset = 0;
        while (!movie.AsSpan(trackOffset + 4, 4).SequenceEqual("trak"u8))
            trackOffset += checked((int)BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(trackOffset)));
        var trackSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(movie.AsSpan(trackOffset)));
        movie = movie[..trackOffset].Concat(Atom("trak", movie[(trackOffset + 8)..(trackOffset + trackSize)], Atom("tref", Atom("chap", UInt(3)))))
            .Concat(movie[(trackOffset + trackSize)..]).Concat(chapterTrack).ToArray();
        File.WriteAllBytes(path, file[..movieOffset].Concat(Atom("mdat", samples)).Concat(Atom("moov", movie)).ToArray());
        if (movieChapters is not null) AddChapters(path, movieChapters);
    }

    internal static void RewriteQuickTime(string path, string atomPath, Func<byte[], byte[]> transform, bool owner = false)
    {
        var atoms = new[] { "moov", "trak" }.Concat(atomPath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        File.WriteAllBytes(path, Rewrite(File.ReadAllBytes(path), atoms, transform, owner));
    }

    private static byte[] Rewrite(byte[] data, string[] path, Func<byte[], byte[]> transform, bool owner)
    {
        var matches = new List<(int Start, int Size)>();
        for (var offset = 0; offset < data.Length;)
        {
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
            if (size < 8 || offset + size > data.Length) throw new InvalidDataException();
            if (data.AsSpan(offset + 4, 4).SequenceEqual(Encoding.ASCII.GetBytes(path[0]))) matches.Add((offset, size));
            offset += size;
        }
        var selected = path[0] == "trak" ? (owner ? matches.First() : matches.Last()) : matches.Single();
        var original = data[selected.Start..(selected.Start + selected.Size)];
        var replacement = path.Length == 1 ? transform(original) : Atom(path[0], Rewrite(original[8..], path[1..], transform, owner));
        return data[..selected.Start].Concat(replacement).Concat(data[(selected.Start + selected.Size)..]).ToArray();
    }

    private static readonly Lazy<byte[]> Encoded = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "NoirMp4-" + Guid.NewGuid() + ".mp4");
        try { Encode(path); return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    });

    // Windows SDK mfreadwrite.h/mfobjects.h ABI. Generate H.264 through the OS
    // encoder, with no downloaded media or third-party encoding dependency.
    private static unsafe void Encode(string path, uint rotation = 0, uint aspectNumerator = 1, uint aspectDenominator = 1, bool quadrants = false, int seconds = 20)
    {
        var apartment = CoInitializeEx(0, 0);
        if (apartment < 0 && apartment != unchecked((int)0x80010106)) Check(apartment);
        nint writer = 0, input = 0, output = 0, audioInput = 0, audioOutput = 0;
        var started = false;
        try
        {
            Check(MFStartup(0x20070, 0));
            started = true;
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out writer));
            Check(MFCreateMediaType(out output));
            ConfigureVideo(output, new Guid("34363248-0000-0010-8000-00AA00389B71")); // H264
            Set32(output, new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1"), rotation);
            Set64(output, new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6"), ((ulong)aspectNumerator << 32) | aspectDenominator);
            Set32(output, new Guid("20332624-FB0D-4D9E-BD0D-CBF6786C102E"), 250_000);
            uint index = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, output, &index));
            Check(MFCreateMediaType(out input));
            ConfigureVideo(input, new Guid("00000016-0000-0010-8000-00AA00389B71")); // RGB32
            Set64(input, new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6"), ((ulong)aspectNumerator << 32) | aspectDenominator);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, index, input, 0));
            Check(MFCreateMediaType(out audioOutput));
            ConfigureAudio(audioOutput, aac: true);
            uint audioIndex = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Slot(writer, 3))(writer, audioOutput, &audioIndex));
            Check(MFCreateMediaType(out audioInput));
            ConfigureAudio(audioInput, aac: false);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Slot(writer, 4))(writer, audioIndex, audioInput, 0));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 5))(writer));
            for (var frame = 0; frame < seconds * 10; frame++)
            {
                SendSample(writer, index, frame * 1_000_000L, 320 * 240 * 4, frame < 80 ? 0x002032C8u : 0x00C83220u, quadrants);
                SendSample(writer, audioIndex, frame * 1_000_000L, 4410 * 4, 0);
            }
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(writer, 11))(writer));
        }
        finally
        {
            if (input != 0) Marshal.Release(input);
            if (output != 0) Marshal.Release(output);
            if (audioInput != 0) Marshal.Release(audioInput);
            if (audioOutput != 0) Marshal.Release(audioOutput);
            if (writer != 0) Marshal.Release(writer);
            if (started) Check(MFShutdown());
            if (apartment >= 0) CoUninitialize();
        }
    }

    private static unsafe void SendSample(nint writer, uint index, long time, int length, uint color, bool quadrants = false)
    {
        nint sample = 0, buffer = 0;
        try
        {
            Check(MFCreateMemoryBuffer(length, out buffer));
            nint pixels = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &pixels, null, null));
            try
            {
                var data = new Span<uint>((void*)pixels, length / 4);
                if (!quadrants) data.Fill(color);
                else
                {
                    // Distinct corners expose wrong rotation direction and double rotation.
                    uint[] colors = time < 80_000_000 ? [0x00C82020, 0x0020C820, 0x002020C8, 0x00C8C820]
                        : [0x002020C8, 0x00C8C820, 0x00C82020, 0x0020C820];
                    for (var y = 0; y < 240; y++)
                    for (var x = 0; x < 320; x++)
                        data[y * 320 + x] = colors[(y < 120 ? 0 : 2) + (x < 160 ? 0 : 1)];
                }
            }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)length));
            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, 1_000_000));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Slot(writer, 6))(writer, index, sample));
        }
        finally
        {
            if (sample != 0) Marshal.Release(sample);
            if (buffer != 0) Marshal.Release(buffer);
        }
    }

    private static void ConfigureAudio(nint type, bool aac)
    {
        SetGuid(type, new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), new Guid("73647561-0000-0010-8000-00AA00389B71"));
        SetGuid(type, new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), new Guid(aac ? "00001610-0000-0010-8000-00AA00389B71" : "00000001-0000-0010-8000-00AA00389B71"));
        Set32(type, new Guid("37E48BF5-645E-4C5B-89DE-ADA9E29B696A"), 2);
        Set32(type, new Guid("5FAEEAE7-0290-4C31-9E8A-C534F68D9DBA"), 44100);
        Set32(type, new Guid("F2DEB57F-40FA-4764-AA33-ED4F2D1FF669"), 16);
        Set32(type, new Guid("1AAB75C8-CFEF-451C-AB95-AC034B8E1731"), aac ? 16000u : 176400u);
        if (!aac) Set32(type, new Guid("322DE230-9EEB-43BD-AB7A-FF412251541D"), 4);
    }

    private static unsafe void ConfigureVideo(nint type, Guid subtype)
    {
        SetGuid(type, new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F"), new Guid("73646976-0000-0010-8000-00AA00389B71"));
        SetGuid(type, new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5"), subtype);
        Set32(type, new Guid("E2724BB8-E676-4806-B4B2-A8D6EFB44CCD"), 2);
        Set64(type, new Guid("1652C33D-D6B2-4012-B834-72030849A37D"), (320ul << 32) | 240);
        Set64(type, new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0"), (10ul << 32) | 1);
        Set64(type, new Guid("C6376A1E-8D0A-4027-BE45-6D9A0AD39BB6"), (1ul << 32) | 1);
    }

    private static unsafe nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static unsafe void Set32(nint target, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(target, 21))(target, &key, value));
    private static unsafe void Set64(nint target, Guid key, ulong value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(target, 22))(target, &key, value));
    private static unsafe void SetGuid(nint target, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(target, 24))(target, &key, &value));

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string path, nint sink, nint attributes, out nint writer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMemoryBuffer(int length, out nint buffer);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
