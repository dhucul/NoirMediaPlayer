using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Tests;

internal static class MpegFixture
{
    private static readonly Lazy<byte[]> Program = new(() => Encode(videoOnly: false));
    private static readonly Lazy<byte[]> VideoProgram = new(() => Encode(videoOnly: true));
    private static readonly Lazy<byte[]> LongProgram = new(() => Encode(videoOnly: false, longer: true));

    private static byte[] Encode(bool videoOnly, bool longer = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirMpegFixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4");
            var destination = Path.Combine(root, "generated.mpg");
            if (longer) Mp4Fixture.WriteLongVideo(source);
            else Mp4Fixture.Write(source);
            var start = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(AppContext.BaseDirectory, "Generate-WindowsMpegFixture.ps1"), "-Source", source, "-Destination", destination })
                start.ArgumentList.Add(argument);
            if (videoOnly) start.ArgumentList.Add("-VideoOnly");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the Windows fixture encoder.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("Windows MPEG fixture generation timed out.");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException(output.Result + error.Result);
            return File.ReadAllBytes(destination);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith("NoirMpegFixtures-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(root, recursive: true);
        }
    }

    internal static void Write(string path, bool videoOnly = false, int audioTracks = 1, bool unsupportedStream = false, bool scrambled = false, bool longer = false)
    {
        var program = longer ? LongProgram.Value : videoOnly ? VideoProgram.Value : Program.Value;
        if (Path.GetExtension(path).ToLowerInvariant() is ".mpg" or ".mpeg" or ".vob")
        {
            File.WriteAllBytes(path, program);
            return;
        }
        File.WriteAllBytes(path, Transport(program, Path.GetExtension(path).ToLowerInvariant() is ".m2ts" or ".mts",
            videoOnly ? 0 : audioTracks, unsupportedStream, scrambled));
    }

    // First-party test packetizer. Windows supplies every encoded A/V sample;
    // these PAT/PMT, PES and 188/192-byte packet headers only change the container.
    private static byte[] Transport(byte[] program, bool m2ts, int audioTracks, bool unsupportedStream, bool scrambled)
    {
        using var output = new MemoryStream();
        var counters = new Dictionary<int, int>();
        uint arrival = 0;
        void Packet(int pid, ReadOnlySpan<byte> payload, bool start, long? pcr = null)
        {
            Span<byte> packet = stackalloc byte[188];
            packet.Fill(0xff);
            packet[0] = 0x47; packet[1] = (byte)((pid >> 8) | (start ? 0x40 : 0)); packet[2] = (byte)pid;
            var counter = counters.GetValueOrDefault(pid);
            counters[pid] = (counter + 1) & 15;
            var adaptation = payload.Length < 184 || pcr is not null;
            packet[3] = (byte)((adaptation ? 0x30 : 0x10) | counter | (scrambled && pid >= 0x101 && pid < 0x110 ? 0x80 : 0));
            var offset = 4;
            if (adaptation)
            {
                var length = 183 - payload.Length;
                packet[offset++] = (byte)length;
                if (length > 0) packet[offset] = pcr is null ? (byte)0 : (byte)0x10;
                if (pcr is { } clock)
                {
                    packet[offset + 1] = (byte)(clock >> 25); packet[offset + 2] = (byte)(clock >> 17);
                    packet[offset + 3] = (byte)(clock >> 9); packet[offset + 4] = (byte)(clock >> 1);
                    packet[offset + 5] = (byte)(((clock & 1) << 7) | 0x7e); packet[offset + 6] = 0;
                }
                offset += length;
            }
            payload.CopyTo(packet[offset..]);
            if (m2ts)
            {
                Span<byte> timestamp = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(timestamp, arrival & 0x3fffffff);
                output.Write(timestamp); arrival += 27_000;
            }
            output.Write(packet);
        }
        static byte[] Section(params byte[] data)
        {
            var crc = Crc(data);
            return new byte[] { 0 }.Concat(data).Concat(Mp4Fixture.UInt(crc)).ToArray();
        }
        var pat = Section(0, 0xb0, 13, 0, 1, 0xc1, 0, 0, 0, 1, 0xf0, 0);
        var pmtEntries = new List<byte> { 0x1b, 0xe1, 1, 0xf0, 0 };
        for (var index = 0; index < audioTracks; index++)
        {
            pmtEntries.AddRange(new byte[] { unsupportedStream ? (byte)0x06 : (byte)0x0f, 0xe1, (byte)(2 + index), 0xf0, 6,
                0x0a, 4, (byte)(index == 0 ? 'e' : 'f'), (byte)(index == 0 ? 'n' : 'r'), (byte)(index == 0 ? 'g' : 'a'), 0 });
        }
        var pmt = Section(new byte[] { 2, 0xb0, (byte)(13 + pmtEntries.Count), 0, 1, 0xc1, 0, 0, 0xe1, 1, 0xf0, 0 }.Concat(pmtEntries).ToArray());
        var samples = new List<(byte Id, byte[] Data, long Clock)>();
        for (var offset = 0; offset + 6 <= program.Length;)
        {
            if (!program.AsSpan(offset, 3).SequenceEqual(new byte[] { 0, 0, 1 })) throw new InvalidDataException("Invalid Windows MPEG fixture.");
            var id = program[offset + 3];
            if (id == 0xb9) break;
            if (id == 0xba) { offset += 14 + (program[offset + 13] & 7); continue; }
            var length = 6 + BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(offset + 4));
            if (offset + length > program.Length || length == 6) throw new InvalidDataException();
            if (id == 0xe0 || id == 0xc0 && audioTracks > 0)
            {
                var pes = program.AsSpan(offset, length);
                var time = pes.Slice((pes[7] & 0xc0) == 0xc0 ? 14 : 9, 5);
                var clock = ((long)(time[0] & 14) << 29) | ((long)time[1] << 22) | ((long)(time[2] & 254) << 14) |
                    ((long)time[3] << 7) | ((long)time[4] >> 1);
                samples.Add((id, pes.ToArray(), clock));
            }
            offset += length;
        }
        Packet(0, pat, true); Packet(0x1000, pmt, true);
        // Transport packets must interleave by decoding time. A PS sink can write
        // bursts from one stream that would deliver TS audio after its PCR deadline.
        foreach (var sample in samples.OrderBy(sample => sample.Clock).ThenBy(sample => sample.Id))
        {
            var id = sample.Id;
            if (id == 0xe0) { Packet(0, pat, true); Packet(0x1000, pmt, true); }
            var copies = id == 0xe0 ? 1 : audioTracks;
            for (var copy = 0; copy < copies; copy++)
            {
                var pes = sample.Data.AsSpan();
                long? clock = id == 0xe0 ? sample.Clock : null;
                for (var consumed = 0; consumed < pes.Length;)
                {
                    var count = Math.Min(pes.Length - consumed, consumed == 0 && clock is not null ? 176 : 184);
                    Packet(id == 0xe0 ? 0x101 : 0x102 + copy, pes.Slice(consumed, count), consumed == 0, consumed == 0 ? clock : null);
                    consumed += count;
                }
            }
        }
        return output.ToArray();
    }

    internal static uint Crc(ReadOnlySpan<byte> data)
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
