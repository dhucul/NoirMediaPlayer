using System.Buffers.Binary;
using System.IO;

namespace NoirMediaPlayer.Services;

internal enum DvdStreamStatus { Ready, CompatibilityRequired }
internal sealed record DvdCellBoundary(byte CellStillSeconds, byte ProgramChainStillSeconds,
    byte CellCommandNumber, bool HasHighlight);

// Bytes include the NAV sector and all original packs through vobu_ea inclusive.
// A boundary is pending until the consumer has presented/drained this VOBU. It is
// never permission to skip a still, execute a command or enter another cell.
internal sealed class DvdStreamUnit(DvdNavigationPacket navigation, byte[] bytes, DvdCell cell, DvdCellBoundary? boundary)
{
    public DvdNavigationPacket Navigation { get; } = navigation;
    public DvdCell Cell { get; } = cell;
    public int ByteCount => bytes.Length;
    public DvdCellBoundary? Boundary { get; } = boundary;
    internal ReadOnlyMemory<byte> Bytes => bytes;
}

internal sealed record DvdStreamRead(DvdStreamStatus Status, DvdDomainState State,
    DvdStreamUnit? Unit = null, DvdDomainState? Next = null, string? Reason = null)
{
    public bool PlaybackVerified => false;
}

// Stateless preparation: one bounded VOBU or no bytes/continuation at all. The
// caller retains its current state until a decoder/coordinator accepts the unit.
// No current VLC state is imported, and live playback is never changed here.
internal sealed class DvdStreamTraversal(DvdNavigationCatalog catalog)
{
    private readonly DvdDomainResolver resolver = new(catalog);
    private const uint EndOfCell = 0x3fffffff;

    internal DvdStreamRead Read(DvdDomainState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var chain = resolver.StreamChain(state);
            var cell = chain.Cells[state.Position.Cell - 1];
            using var source = new DvdVobSource(catalog.VideoTsDirectory, chain.Key, cancellationToken);
            Require(cell.FirstSector <= cell.LastSector && cell.LastSector < source.SectorCount,
                "The authored DVD cell extends outside its domain's VOB files.");
            var navBytes = new byte[DvdVobSource.SectorSize];
            source.Read(state.Position.Sector, navBytes, cancellationToken);
            var nav = DvdNavigationPacket.Read(navBytes);
            Require(nav.Sector == state.Position.Sector && nav.VobId == cell.VobId && nav.CellId == cell.CellId,
                "The NAV sector does not match the resolved DVD sector/VOB/cell.");
            Require(nav.CompatibilityReason is null, nav.CompatibilityReason ?? "Unsupported DVD NAV data.");
            Require(nav.TraversalCompatibilityReason is null, nav.TraversalCompatibilityReason ?? "Unsupported DVD traversal data.");
            Require(nav.VobuEndRelativeSector < DvdVobSource.MaximumReadSectors,
                "The DVD VOBU exceeds the bounded sector-read budget.");
            var last = (ulong)nav.Sector + nav.VobuEndRelativeSector;
            Require(last <= cell.LastSector, "The DVD VOBU crosses its authored cell boundary.");

            var distance = nav.NextVobuRaw & EndOfCell;
            Require((nav.NextVobuRaw & 0x40000000) == 0, "Reserved DVD next-VOBU link flag.");
            DvdDomainState? next = null;
            DvdCellBoundary? boundary = null;
            if (distance == EndOfCell)
            {
                Require(last == cell.LastSector, "The DVD end-of-cell marker precedes the authored cell end.");
                boundary = new(cell.StillSeconds, state.Position.Cell == chain.Cells.Count ? chain.StillSeconds : (byte)0,
                    cell.CommandNumber, nav.Highlight.Update != DvdHighlightUpdate.None);
            }
            else
            {
                // Bit 31 is the forward-link validity flag, not a negative sign.
                // Bit 30 and unavailable/zero/overlapping links are not guessed.
                Require((nav.NextVobuRaw & 0xc0000000) == 0x80000000 && distance > nav.VobuEndRelativeSector,
                    "The DVD next-VOBU link is unavailable, overlapping or unsupported.");
                var sector = (ulong)nav.Sector + distance;
                Require(sector <= cell.LastSector && sector <= uint.MaxValue,
                    "The DVD next-VOBU link leaves its authored cell.");
                // Ordinary forward traversal preserves the navigation epoch so
                // highlight continuations remain in the same presentation run.
                next = state with { Position = state.Position with { Sector = (uint)sector } };
            }

            var bytes = new byte[checked(((int)nav.VobuEndRelativeSector + 1) * DvdVobSource.SectorSize)];
            navBytes.CopyTo(bytes, 0);
            if (bytes.Length > navBytes.Length)
                source.Read(nav.Sector + 1, bytes.AsSpan(navBytes.Length), cancellationToken);
            for (var offset = navBytes.Length; offset < bytes.Length; offset += DvdVobSource.SectorSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateMediaSector(bytes.AsSpan(offset, DvdVobSource.SectorSize));
            }
            return new(DvdStreamStatus.Ready, state, new(nav, bytes, cell, boundary), next);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or TimeoutException)
        {
            return new(DvdStreamStatus.CompatibilityRequired, state, Reason: exception.Message);
        }
    }

    // Parse top-level PS framing by lengths, never scan elementary payloads for
    // start codes. This also checks PES_scrambling_control before exposing bytes
    // to a future native decoder. It does not remove or decrypt protected data.
    private static void ValidateMediaSector(ReadOnlySpan<byte> sector)
    {
        Require(Prefix(sector, 0xba) && (sector[4] & 0xc4) == 0x44 && (sector[6] & 4) != 0 &&
            (sector[8] & 4) != 0 && (sector[9] & 1) != 0 && (sector[12] & 3) == 3 && (sector[13] & 0xf8) == 0xf8,
            "Expected a clear MPEG-2 DVD pack in the VOBU.");
        var offset = 14 + (sector[13] & 7);
        for (var i = 14; i < offset; i++) Require(sector[i] == 0xff, "Invalid DVD pack stuffing.");
        while (offset < sector.Length)
        {
            var packet = sector[offset..];
            Require(packet.Length >= 6 && packet[0] == 0 && packet[1] == 0 && packet[2] == 1,
                "Invalid DVD media packet framing.");
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            Require(length != 0 && length <= packet.Length - 6, "Unbounded or truncated DVD media packet.");
            var code = packet[3];
            if (code is 0xbd or >= 0xc0 and <= 0xef)
            {
                Require(length >= 3 && (packet[6] & 0xc0) == 0x80 && packet[8] <= length - 3,
                    "Invalid MPEG-2 DVD PES header.");
                Require((packet[6] & 0x30) == 0, "Protected/scrambled DVD sectors require the compatibility backend.");
            }
            else Require(code is 0xbb or 0xbe, "Unsupported packet or unexpected nested NAV sector in a DVD VOBU.");
            offset += 6 + length;
        }
    }

    private static bool Prefix(ReadOnlySpan<byte> bytes, byte code) => bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == code;
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
}
