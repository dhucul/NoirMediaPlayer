using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using NoirMediaPlayer.Services;

namespace NoirMediaPlayer.Tests;

// Synthetic Windows-encoded control at DVD canvas height, with original encoded
// payloads. Clock shifts change only PES timestamps and MPEG pack SCR fields.
internal static class DvdMovingControlFixture
{
    private static readonly Lazy<byte[]> Program = new(Encode);
    private static byte[] Encode()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoirDvdTiming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mp4"); var output = Path.Combine(root, "control.mpg"); Mp4Fixture.Write(source);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Generate-WindowsMpegFixture.ps1"),
                "-Source", source, "-Destination", output, "-Height", "480" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot encode Windows DVD timing control.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Windows timing control encoder timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(stdout.Result + stderr.Result);
            return File.ReadAllBytes(output);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(resolved).StartsWith("NoirDvdTiming-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected timing fixture cleanup path.");
            Directory.Delete(resolved, true);
        }
    }

    internal static (byte[] Program, long[] Video) Create(long shift)
    {
        var program = Program.Value.ToArray(); var video = new List<long>();
        for (var offset = 0; offset + 6 <= program.Length;)
        {
            var id = program[offset + 3]; if (id == 0xb9) break;
            if (id == 0xba)
            {
                var b = program.AsSpan(offset);
                long scr = (long)(b[4] & 0x38) << 27 | (long)(b[4] & 3) << 28 | (long)b[5] << 20 |
                    (long)(b[6] & 0xf8) << 12 | (long)(b[6] & 3) << 13 | (long)b[7] << 5 | (uint)b[8] >> 3;
                scr = (scr + shift) & DvdSubpicture.ClockMask;
                b[4] = (byte)((b[4] & 0xc4u) | (scr >> 27 & 0x38) | (scr >> 28 & 3)); b[5] = (byte)(scr >> 20);
                b[6] = (byte)((b[6] & 4u) | (scr >> 12 & 0xf8) | (scr >> 13 & 3)); b[7] = (byte)(scr >> 5);
                b[8] = (byte)((b[8] & 7u) | (scr << 3 & 0xf8)); offset += 14 + (b[13] & 7); continue;
            }
            var size = 6 + BinaryPrimitives.ReadUInt16BigEndian(program.AsSpan(offset + 4));
            if (id is >= 0xc0 and <= 0xef)
            {
                var packet = program.AsSpan(offset, size); var pts = DvdPesTimestamp.Read(packet);
                if (pts.HasValue)
                {
                    var shifted = (pts.Value + shift) & DvdSubpicture.ClockMask;
                    WritePts(packet.Slice(9, 5), shifted);
                    if (id >= 0xe0) video.Add(shifted);
                    if (packet[7] >> 6 == 3)
                    {
                        var d = packet.Slice(14, 5); long dts = (long)(d[0] & 14) << 29 | (long)d[1] << 22 | (long)(d[2] & 254) << 14 | (long)d[3] << 7 | (uint)d[4] >> 1;
                        WritePts(d, (dts + shift) & DvdSubpicture.ClockMask);
                    }
                }
            }
            offset += size;
        }
        return (program, video.ToArray());
    }
    private static void WritePts(Span<byte> bytes, long pts)
    {
        bytes[0] = (byte)((bytes[0] & 0xf1u) | (pts >> 29 & 14)); bytes[1] = (byte)(pts >> 22);
        bytes[2] = (byte)((pts >> 14 & 254) | 1); bytes[3] = (byte)(pts >> 7); bytes[4] = (byte)((pts << 1 & 254) | 1);
    }
}
