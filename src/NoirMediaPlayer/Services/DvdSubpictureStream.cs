using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Services;

// One explicitly unambiguous SPU channel per prepared cell. A new preparation
// creates a new assembler, so neither fragments nor timestamps survive a branch.
internal sealed class DvdSubpictureStream
{
    private readonly List<DvdSubpicture> pictures = new();
    private readonly byte[] pending = new byte[ushort.MaxValue];
    private int channel = -1, count, size, storage;
    private long pts;
    internal bool HasPictures => pictures.Count != 0;

    // A complete, already-framed MPEG-2 PES, including its six-byte prefix.
    internal void AddPes(ReadOnlySpan<byte> packet, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (packet.Length < 10 || packet[0] != 0 || packet[1] != 0 || packet[2] != 1 || packet[3] != 0xbd ||
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != packet.Length - 6 || (packet[6] & 0xc0) != 0x80 || (packet[6] & 0x30) != 0)
            throw new InvalidDataException("Invalid or protected DVD subpicture PES.");
        var payload = 9 + packet[8];
        if (payload >= packet.Length) throw new InvalidDataException("Empty DVD private PES.");
        var id = packet[payload++];
        if (id is < 0x20 or > 0x3f) throw new NotSupportedException("DVD private audio requires compatibility playback.");
        if (channel >= 0 && id != channel) throw new NotSupportedException("DVD subpicture channel selection is not integrated yet.");
        channel = id;
        var timestamp = DvdPesTimestamp.Read(packet);
        if (count == 0)
        {
            if (timestamp is null) throw new NotSupportedException("An SPU unit must begin with an authored presentation timestamp.");
            if (pictures.Count == 256) throw new NotSupportedException("DVD subpicture packet budget exceeded.");
            pts = timestamp.Value; size = 0;
        }
        else if (timestamp.HasValue && timestamp != pts) throw new InvalidDataException("DVD SPU timestamp changed inside a fragment.");
        var bytes = packet[payload..];
        if (bytes.IsEmpty || count > pending.Length - bytes.Length) throw new InvalidDataException("DVD SPU fragment exceeds its packet bounds.");
        bytes.CopyTo(pending.AsSpan(count)); count += bytes.Length;
        if (count >= 2 && size == 0)
        {
            size = BinaryPrimitives.ReadUInt16BigEndian(pending);
            if (size < 10) throw new InvalidDataException("Invalid DVD SPU size.");
        }
        if (size != 0 && count > size) throw new NotSupportedException("Multiple SPU units in one PES require compatibility playback.");
        if (size != 0 && count == size)
        {
            if (pictures.Count > 0 && ((pts - pictures[^1].Pts) & DvdSubpicture.ClockMask) >= (1L << 32))
                throw new NotSupportedException("Out-of-order DVD subpicture timestamps require compatibility playback.");
            var decoded = DvdSubpicture.Decode(pending.AsSpan(0, size), pts, token);
            storage = checked(storage + decoded.PixelStorage);
            if (storage > 16 * 1024 * 1024) throw new NotSupportedException("DVD cell subpicture pixel budget exceeded.");
            pictures.Add(decoded); count = size = 0;
        }
    }

    internal void Complete()
    { if (count != 0) throw new NotSupportedException("DVD cell ends inside an SPU packet; continuous subpicture assembly is not integrated yet."); }

    internal DvdSubpictureFrame? At(long time90Khz)
    {
        if (time90Khz is < 0 or > DvdSubpicture.ClockMask) throw new ArgumentOutOfRangeException(nameof(time90Khz));
        for (var i = pictures.Count - 1; i >= 0; i--)
            if (((time90Khz - pictures[i].Pts) & DvdSubpicture.ClockMask) < (1L << 32)) return pictures[i].At(time90Khz);
        return null;
    }

    internal DvdSubpictureFrame? AtNav(uint time90Khz)
    {
        if (pictures.Count == 0) return null;
        // NAV timestamps carry the low 32 bits. Resolve the nearest value around
        // this bounded cell's first full 33-bit PES timestamp, including wrap.
        var anchor = pictures[0].Pts;
        var delta = unchecked((int)(time90Khz - (uint)anchor));
        return At((anchor + delta) & DvdSubpicture.ClockMask);
    }

}
