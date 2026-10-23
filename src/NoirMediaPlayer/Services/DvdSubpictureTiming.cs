using System.IO;

namespace NoirMediaPlayer.Services;

// The source and Windows timelines can have different origins. Pair the first
// authored video PTS with the first decoded frame while the engine is paused at
// source entry. Never derive an origin from wall time or from the first SPU.
internal sealed class DvdVideoClock
{
    private readonly long origin, duration;
    private long? previous;
    private readonly long firstVideo;
    private long? nativeOrigin;
    internal long? Current90Khz { get; private set; }
    internal long End90Khz => (origin + duration) & DvdSubpicture.ClockMask;

    internal DvdVideoClock(IReadOnlyList<long> videoPts, uint start90Khz, uint end90Khz)
    {
        duration = unchecked(end90Khz - start90Khz);
        if (videoPts.Count is < 1 or > 65536 || duration is <= 0 or > 3600 * 90000)
            throw new NotSupportedException("Moving DVD subpictures need a bounded authored video timeline.");
        var first = videoPts[0];
        if (videoPts.Any(pts => pts is < 0 or > DvdSubpicture.ClockMask)) throw new InvalidDataException("Invalid DVD video PTS.");
        origin = (first + unchecked((int)(start90Khz - (uint)first))) & DvdSubpicture.ClockMask;
        var anchors = videoPts.Select(pts => (pts - origin) & DvdSubpicture.ClockMask).Distinct().Order().ToArray();
        if (anchors.Any(time => time > duration)) throw new NotSupportedException("DVD video timestamps leave the authored cell clock.");
        firstVideo = (first - origin) & DvdSubpicture.ClockMask;
        if (firstVideo != anchors[0]) throw new NotSupportedException("DVD video entry has ambiguous leading reordered pictures.");
    }

    internal void BindFirstFrame(long frame100ns)
    {
        if (nativeOrigin.HasValue) throw new InvalidOperationException("The DVD frame clock is already anchored.");
        if (frame100ns < 0) throw new NotSupportedException("Windows returned a negative DVD frame timestamp.");
        nativeOrigin = frame100ns;
    }

    internal long Accept(long frame100ns)
    {
        if (!nativeOrigin.HasValue) throw new InvalidOperationException("The first paused DVD frame must anchor the presentation clock.");
        if (frame100ns < nativeOrigin.Value) throw new NotSupportedException("Windows returned a DVD frame before the anchored entry timestamp.");
        // Integer conversion rounds to the nearest 90 kHz tick and cannot
        // overflow even for a large unwrapped Media Foundation timestamp.
        var delta = frame100ns - nativeOrigin.Value;
        var elapsed = firstVideo + delta / 1000 * 9 + (delta % 1000 * 9 + 500) / 1000;
        if (elapsed > duration || previous.HasValue && elapsed < previous.Value)
            throw new NotSupportedException("Windows DVD frame timestamps left or reversed the authored timeline.");
        previous = elapsed; Current90Khz = (origin + elapsed) & DvdSubpicture.ClockMask; return Current90Khz.Value;
    }
}

internal sealed class DvdSubpictureTiming(DvdSubpictureStream stream, IReadOnlyList<uint> palette, DvdVideoClock clock, DvdMovingMenu? menu = null)
{
    private long? heldTime;
    private DvdSubpictureFrame? heldFrame;
    internal DvdMovingMenu? Menu => menu;
    internal bool Held => heldTime.HasValue;
    internal long? Current90Khz => heldTime ?? clock.Current90Khz;
    internal IReadOnlyList<uint> Palette { get; } = Array.AsReadOnly(palette.ToArray());
    internal void BindFirstFrame(long frame100ns) => clock.BindFirstFrame(frame100ns);
    internal DvdSubpictureFrame? Sample(long frame100ns)
    {
        if (Held) throw new InvalidOperationException("A held DVD picture cannot advance its frame clock.");
        var now = clock.Accept(frame100ns); menu?.Advance(now); return stream.At(now);
    }
    internal DvdSubpictureFrame? EnterHold()
    {
        if (Held) return heldFrame;
        if (!clock.Current90Khz.HasValue) throw new InvalidOperationException("A decoded DVD picture is required before holding.");
        menu?.EnterHold(clock.End90Khz);
        heldTime = clock.End90Khz; heldFrame = stream.At(heldTime.Value); return heldFrame;
    }
}

internal static class DvdPesTimestamp
{
    internal static long? Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 9 || (packet[6] & 0xf0) != 0x80 || 9 + packet[8] > packet.Length)
            throw new InvalidDataException("Invalid clear DVD PES timestamp header.");
        var flags = packet[7] >> 6;
        if (flags == 1) throw new InvalidDataException("Invalid DVD PES timestamp flags.");
        if (flags == 0) return null;
        if (packet[8] < (flags == 3 ? 10 : 5)) throw new InvalidDataException("Truncated DVD PES timestamps.");
        var pts = Value(packet.Slice(9, 5), flags);
        if (flags == 3) Value(packet.Slice(14, 5), 1);
        return pts;
    }
    private static long Value(ReadOnlySpan<byte> bytes, int prefix)
    {
        if (bytes[0] >> 4 != prefix || (bytes[0] & 1) == 0 || (bytes[2] & 1) == 0 || (bytes[4] & 1) == 0)
            throw new InvalidDataException("Invalid DVD PES timestamp markers.");
        return (long)(bytes[0] & 14) << 29 | (long)bytes[1] << 22 | (long)(bytes[2] & 254) << 14 | (long)bytes[3] << 7 | (uint)bytes[4] >> 1;
    }
}
