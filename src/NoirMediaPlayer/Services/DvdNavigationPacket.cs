using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdHighlightUpdate { None, New, Continue, CommandsOnly }
internal enum DvdMenuDisplay { Normal, Wide, Letterbox, PanScan }
internal sealed record DvdButtonBounds(int Left, int Top, int Right, int Bottom)
{
    internal bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
}

// An immutable copy of all eight authored bytes, passed intact to the bounded VM.
// These bytes are never guessed to be playable title/chapter numbers.
internal sealed record DvdButtonCommand(ulong Value)
{
    public string Hex => Value.ToString("X16");
}
internal sealed record DvdMenuButton(int Number, DvdButtonBounds Bounds, int ColorNumber,
    bool AutoActivate, int Up, int Down, int Left, int Right, DvdButtonCommand Command);
internal sealed record DvdButtonColor(uint Selected, uint Activated);
internal sealed record DvdButtonGroup(int DisplayMask, IReadOnlyList<DvdMenuButton> Buttons);
internal sealed record DvdMenuHighlight(DvdHighlightUpdate Update, uint Start90Khz, uint End90Khz,
    uint SelectionEnd90Khz, int ButtonOffset, int NumericSelectionCount, int ForcedSelection,
    int ForcedActivation, IReadOnlyList<DvdButtonGroup> Groups, IReadOnlyList<DvdButtonColor> Colors,
    IReadOnlyList<DvdButtonCommand> Commands);

// One clear 2048-byte NAV sector. Search words are retained intact; traversal is
// separately checked against the current domain, IFO cell and available VOB files.
internal sealed record DvdNavigationPacket(uint Sector, int VobId, int CellId, uint Start90Khz,
    uint End90Khz, uint ProhibitedOperations, uint VobuEndRelativeSector, uint NextVobuRaw,
    uint PreviousVobuRaw, DvdMenuHighlight Highlight, string? CompatibilityReason)
{
    public string? TraversalCompatibilityReason { get; private init; }

    internal static DvdNavigationPacket Read(ReadOnlySpan<byte> sector)
    {
        if (sector.Length != 2048 || !Prefix(sector, 0xba) ||
            (sector[4] & 0xc4) != 0x44 || (sector[6] & 4) == 0 || (sector[8] & 4) == 0 ||
            (sector[9] & 1) == 0 || (sector[12] & 3) != 3 || (sector[13] & 0xf8) != 0xf8)
            throw new InvalidDataException("Expected one complete MPEG-2 DVD navigation sector.");
        var offset = 14 + (sector[13] & 7);
        for (var index = 14; index < offset; index++)
            if (sector[index] != 0xff) throw new InvalidDataException("Invalid pack stuffing.");
        ReadOnlySpan<byte> pci = default, dsi = default;
        while (offset < sector.Length)
        {
            var remaining = sector[offset..];
            if (remaining.Length < 6 || remaining[0] != 0 || remaining[1] != 0 || remaining[2] != 1)
                throw new InvalidDataException("Invalid navigation packet framing.");
            var size = BinaryPrimitives.ReadUInt16BigEndian(remaining[4..]);
            if (size > remaining.Length - 6) throw new InvalidDataException("Truncated navigation packet.");
            var payload = remaining.Slice(6, size);
            switch (remaining[3])
            {
                case 0xbf when size == 980 && payload[0] == 0 && pci.IsEmpty:
                    pci = payload[1..]; break;
                case 0xbf when size == 1018 && payload[0] == 1 && dsi.IsEmpty:
                    dsi = payload[1..]; break;
                case 0xbb: // Bounded system header; not searched for nested start codes.
                case 0xbe: // Padding packet.
                    break;
                default: throw new InvalidDataException("Expected exactly one PCI and one DSI packet.");
            }
            offset += size + 6;
        }
        if (pci.IsEmpty || dsi.IsEmpty || U32(pci, 0) != U32(dsi, 4) || U16(dsi, 24) == 0 || dsi[27] == 0 ||
            (ulong)U32(dsi, 4) + U32(dsi, 8) > uint.MaxValue)
            throw new InvalidDataException("Missing or inconsistent navigation identity/range.");

        var highlight = ReadHighlight(pci.Slice(96, 694), out var reason);
        if (U16(pci, 6) != 0 || dsi[26] != 0 || (U32(pci, 8) & 0xfe000000) != 0)
            reason ??= "Reserved navigation fields require compatibility handling.";
        // Angle destinations and ILVU preparation/block flags cannot be treated as
        // ordinary forward links. Keep this independent of highlight rendering.
        var traversalReason = U16(dsi, 32) != 0 || !AllZero(pci.Slice(60, 36)) ||
            !AllZero(dsi.Slice(34, 10)) || !AllZero(dsi.Slice(180, 54))
            ? "Interleaved, angle or unknown seamless traversal data requires compatibility." : null;
        return new(U32(pci, 0), U16(dsi, 24), dsi[27], U32(pci, 12), U32(pci, 16),
            U32(pci, 8), U32(dsi, 8), U32(dsi, 314), U32(dsi, 318), highlight, reason)
            { TraversalCompatibilityReason = traversalReason };
    }

    private static DvdMenuHighlight ReadHighlight(ReadOnlySpan<byte> data, out string? reason)
    {
        reason = null;
        var status = U16(data, 0);
        var update = (DvdHighlightUpdate)(status & 3);
        var commands = Enumerable.Range(0, 36).Select(_ => new DvdButtonCommand(0)).ToArray();
        for (var i = 0; i < commands.Length; i++)
            commands[i] = new(BinaryPrimitives.ReadUInt64BigEndian(data[(46 + i * 18 + 10)..]));
        var colors = new DvdButtonColor[3];
        for (var i = 0; i < colors.Length; i++) colors[i] = new(U32(data, 22 + i * 8), U32(data, 26 + i * 8));
        var groups = new List<DvdButtonGroup>();
        if ((status & ~3) != 0) reason = "Unknown highlight status flags.";
        if (update == DvdHighlightUpdate.New)
        {
            var count = (data[14] >> 4) & 3;
            var buttons = data[17] & 63;
            if (count == 0 || buttons == 0 || buttons > 36 / count || (data[18] & 63) > buttons ||
                (data[20] & 63) > buttons || (data[21] & 63) > buttons)
                throw new InvalidDataException("Invalid DVD button counts or forced button reference.");
            if ((data[14] & 0xc8) != 0 || (data[15] & 0x88) != 0 || data[19] != 0 ||
                (data[17] & 0xc0) != 0 || (data[18] & 0xc0) != 0 ||
                (data[20] & 0xc0) != 0 || (data[21] & 0xc0) != 0)
                reason ??= "Reserved highlight fields require compatibility handling.";
            if (data[16] != 0) reason ??= "Button offset numbering is not implemented.";
            for (var group = 0; group < count; group++)
            {
                var entries = new DvdMenuButton[buttons];
                for (var index = 0; index < buttons; index++)
                {
                    var slot = group * (36 / count) + index;
                    var b = data.Slice(46 + slot * 18, 18);
                    var x = b[0] << 16 | b[1] << 8 | b[2];
                    var y = b[3] << 16 | b[4] << 8 | b[5];
                    var bounds = new DvdButtonBounds((x >> 12) & 1023, (y >> 12) & 1023, x & 1023, y & 1023);
                    if (bounds.Left > bounds.Right || bounds.Top > bounds.Bottom ||
                        (b[6] & 63) > buttons || (b[7] & 63) > buttons ||
                        (b[8] & 63) > buttons || (b[9] & 63) > buttons)
                        throw new InvalidDataException("Invalid button geometry or neighbor reference.");
                    if ((x & 0xc00) != 0 || (y & 0xc00) != 0 || (y >> 22) > 1 ||
                        (b[6] & 0xc0) != 0 || (b[7] & 0xc0) != 0 || (b[8] & 0xc0) != 0 || (b[9] & 0xc0) != 0)
                        reason ??= "Unknown button flags require compatibility handling.";
                    entries[index] = new(index + 1, bounds, x >> 22, (y >> 22) == 1,
                        b[6] & 63, b[7] & 63, b[8] & 63, b[9] & 63, commands[slot]);
                }
                var mask = group == 0 ? data[14] & 7 : group == 1 ? (data[15] >> 4) & 7 : data[15] & 7;
                groups.Add(new(mask, Array.AsReadOnly(entries)));
            }
        }
        return new(update, U32(data, 2), U32(data, 6), U32(data, 10), data[16], data[18] & 63,
            data[20] & 63, data[21] & 63, groups.AsReadOnly(), Array.AsReadOnly(colors), Array.AsReadOnly(commands));
    }

    private static bool Prefix(ReadOnlySpan<byte> data, byte code) => data[0] == 0 && data[1] == 0 && data[2] == 1 && data[3] == code;
    private static bool AllZero(ReadOnlySpan<byte> data)
    {
        foreach (var value in data) if (value != 0) return false;
        return true;
    }
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
}
