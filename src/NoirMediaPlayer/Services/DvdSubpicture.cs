using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Services;

// First-party DVD SPU reader. The complete control chain is validated before any
// pixels are exposed; unsupported colour-change tables cannot be half-rendered.
internal sealed class DvdSubpicture
{
    internal const long ClockMask = (1L << 33) - 1;
    private sealed record Change(long Delay, DvdSubpictureFrame? Frame);
    private readonly Change[] changes;
    internal long Pts { get; }
    internal int PixelStorage { get; }
    private DvdSubpicture(long pts, Change[] changes, int storage)
    { Pts = pts; this.changes = changes; PixelStorage = storage; }

    internal DvdSubpictureFrame? At(long time90Khz)
    {
        if (time90Khz is < 0 or > ClockMask) throw new ArgumentOutOfRangeException(nameof(time90Khz));
        var elapsed = (time90Khz - Pts) & ClockMask;
        if (elapsed >= (1L << 32)) return null;
        DvdSubpictureFrame? frame = null;
        foreach (var change in changes) { if (change.Delay > elapsed) break; frame = change.Frame; }
        return frame;
    }

    internal static DvdSubpicture Decode(ReadOnlySpan<byte> packet, long pts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pts is < 0 or > ClockMask) throw new ArgumentOutOfRangeException(nameof(pts));
        if (packet.Length is < 10 or > ushort.MaxValue || U16(packet, 0) != packet.Length) throw Bad("SPU packet size");
        var control = U16(packet, 2);
        if (control < 4 || control > packet.Length - 6) throw Bad("SPU control offset");
        var states = new List<Change>();
        var images = new Dictionary<(int, int, int, int), byte[]>();
        var left = -1; var top = -1; var width = 0; var height = 0; var even = -1; var odd = -1;
        ushort colors = 0, alpha = 0; var hasColors = false; var hasAlpha = false;
        var visible = false; var forced = false; var storage = 0; long previousDelay = -1;
        var offset = control;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (states.Count == 64) throw new NotSupportedException("DVD SPU control sequence budget exceeded.");
            var delay = (long)U16(packet, offset) * 1024;
            var next = U16(packet, offset + 2);
            if (delay < previousDelay || next != offset && (next <= offset || next > packet.Length - 6)) throw Bad("SPU control chain");
            previousDelay = delay;
            var cursor = offset + 4; var limit = next == offset ? packet.Length : next;
            bool ended = false;
            while (cursor < limit)
            {
                var command = packet[cursor++];
                var size = command switch { 0 or 1 or 2 or 255 => 0, 3 or 4 => 2, 5 => 6, 6 => 4,
                    _ => throw new NotSupportedException($"DVD SPU control 0x{command:X2} requires compatibility rendering.") };
                if (cursor > limit - size) throw Bad("Truncated SPU command");
                switch (command)
                {
                    case 0: visible = forced = true; break;
                    case 1: visible = true; forced = false; break;
                    case 2: visible = false; break;
                    case 3: colors = U16(packet, cursor); hasColors = true; break;
                    case 4: alpha = U16(packet, cursor); hasAlpha = true; break;
                    case 5:
                        left = packet[cursor] << 4 | packet[cursor + 1] >> 4;
                        var right = (packet[cursor + 1] & 15) << 8 | packet[cursor + 2];
                        top = packet[cursor + 3] << 4 | packet[cursor + 4] >> 4;
                        var bottom = (packet[cursor + 4] & 15) << 8 | packet[cursor + 5];
                        if (left > right || top > bottom || right >= 720 || bottom >= 576) throw Bad("SPU display rectangle");
                        width = right - left + 1; height = bottom - top + 1; break;
                    case 6: even = U16(packet, cursor); odd = U16(packet, cursor + 2); break;
                    case 255: ended = true; break;
                }
                cursor += size;
                if (ended) break;
            }
            if (!ended) throw Bad("Unterminated SPU controls");
            DvdSubpictureFrame? frame = null;
            if (visible)
            {
                if (left < 0 || top < 0 || !hasColors || !hasAlpha || even < 4 || odd < 4 || even >= control || odd >= control)
                    throw Bad("Incomplete SPU display state");
                var key = (width, height, even, odd);
                if (!images.TryGetValue(key, out var pixels))
                {
                    storage = checked(storage + width * height);
                    if (storage > 8 * 1024 * 1024) throw new NotSupportedException("DVD SPU decoded pixel budget exceeded.");
                    pixels = new byte[width * height];
                    DecodeField(packet, even, odd > even ? odd : control, pixels, width, height, 0, cancellationToken);
                    DecodeField(packet, odd, even > odd ? even : control, pixels, width, height, 1, cancellationToken);
                    images.Add(key, pixels);
                }
                frame = new(left, top, width, height, pixels, colors, alpha, forced);
            }
            states.Add(new(delay, frame));
            if (next == offset) break;
            offset = next;
        }
        return new(pts, states.ToArray(), storage);
    }

    private static void DecodeField(ReadOnlySpan<byte> packet, int start, int end, byte[] pixels,
        int width, int height, int field, CancellationToken token)
    {
        var nibble = start * 2; var limit = end * 2;
        for (var y = field; y < height; y += 2)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < width;)
            {
                var code = Nibble(packet, ref nibble, limit);
                if (code < 4)
                {
                    code = code << 4 | Nibble(packet, ref nibble, limit);
                    if (code < 16)
                    {
                        code = code << 4 | Nibble(packet, ref nibble, limit);
                        if (code < 64) code = code << 4 | Nibble(packet, ref nibble, limit);
                    }
                }
                var run = code >> 2;
                if (run == 0) run = width - x;
                if (run > width - x) throw Bad("SPU run crosses its scanline");
                pixels.AsSpan(y * width + x, run).Fill((byte)(code & 3)); x += run;
            }
            // Each interlaced scanline begins on a byte boundary.
            if ((nibble & 1) != 0) { Nibble(packet, ref nibble, limit); }
        }
    }
    private static int Nibble(ReadOnlySpan<byte> bytes, ref int position, int end)
    {
        if (position >= end) throw Bad("Truncated SPU run data");
        var result = (bytes[position / 2] >> ((position & 1) == 0 ? 4 : 0)) & 15; position++; return result;
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset)
    { if (offset < 0 || offset > data.Length - 2) throw Bad("Truncated SPU data"); return BinaryPrimitives.ReadUInt16BigEndian(data[offset..]); }
    private static InvalidDataException Bad(string reason) => new(reason + ".");
}

internal sealed class DvdSubpictureFrame(int left, int top, int width, int height, byte[] pixels, ushort colors, ushort alpha, bool forced)
{
    internal int Left => left;
    internal int Top => top;
    internal int Width => width;
    internal int Height => height;
    internal bool Forced => forced;
    internal ReadOnlyMemory<byte> Indices => pixels;

    internal DvdSubpictureOverlay Colorize(IReadOnlyList<uint> palette, int canvasHeight, DvdButtonHighlight? highlight = null)
    {
        if (palette.Count != 16 || palette.Any(color => (color & 0xff000000) != 0)) throw new NotSupportedException("DVD subpictures need the authored 16-entry Y/Cr/Cb palette.");
        if (canvasHeight is not (480 or 576) || top + height > canvasHeight) throw new NotSupportedException("SPU coordinates exceed the DVD video canvas.");
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var index = pixels[y * width + x];
            var inside = highlight?.Bounds.Contains(left + x, top + y) == true;
            var map = inside ? highlight!.ColorAndAlpha >> 16 : colors;
            var opacity = inside ? highlight!.ColorAndAlpha & 0xffff : alpha;
            var value = palette[(int)(map >> (index * 4) & 15)];
            var c = (int)(value >> 16 & 255) - 16; var d = (int)(value & 255) - 128; var e = (int)(value >> 8 & 255) - 128;
            var offset = (y * width + x) * 4;
            data[offset] = Clamp((298 * c + 516 * d + 128) >> 8);
            data[offset + 1] = Clamp((298 * c - 100 * d - 208 * e + 128) >> 8);
            data[offset + 2] = Clamp((298 * c + 409 * e + 128) >> 8);
            data[offset + 3] = (byte)((opacity >> (index * 4) & 15) * 17);
        }
        return new(left, top, width, height, canvasHeight, data);
    }
    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}

// Compact straight-alpha BGRA rectangle in the 720 x 480/576 DVD canvas. The
// presenter scales it with the picture before its existing aspect/rotation map.
internal sealed record DvdSubpictureOverlay(int Left, int Top, int Width, int Height, int CanvasHeight, ReadOnlyMemory<byte> Bgra)
{
    internal void Composite(Span<byte> destination, int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height * 4 != destination.Length ||
            CanvasHeight is not (480 or 576) || Width < 1 || Height < 1 || Left < 0 || Top < 0 ||
            Left + (long)Width > 720 || Top + (long)Height > CanvasHeight || (long)Width * Height * 4 != Bgra.Length)
            throw new ArgumentException("Invalid DVD subpicture surface.");
        var source = Bgra.Span;
        var x1 = (int)(((long)Left * width + 719) / 720);
        var x2 = (int)(((long)(Left + Width) * width + 719) / 720);
        var y1 = (int)(((long)Top * height + CanvasHeight - 1) / CanvasHeight);
        var y2 = (int)(((long)(Top + Height) * height + CanvasHeight - 1) / CanvasHeight);
        for (var y = y1; y < y2; y++) for (var x = x1; x < x2; x++)
        {
            var src = ((y * (long)CanvasHeight / height - Top) * Width + (x * (long)720 / width - Left)) * 4;
            var dst = (y * width + x) * 4; var a = source[(int)src + 3];
            for (var c = 0; c < 3; c++) destination[dst + c] = (byte)((source[(int)src + c] * a + destination[dst + c] * (255 - a) + 127) / 255);
        }
    }
}
