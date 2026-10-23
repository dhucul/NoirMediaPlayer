using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NoirMediaPlayer.Services;

// A bounded, clear cell for the diagnostic native renderer. Preparation follows
// the same NAV traversal, but never acknowledges presentation or runs a command.
internal sealed class DvdCellMedia
{
    public IReadOnlyList<DvdStreamRead> Units { get; }
    public long ByteCount { get; }
    public int AudioStreams { get; }
    internal DvdSubpictureStream Subpictures { get; }
    internal IReadOnlyList<uint> Palette { get; }
    internal DvdSubpictureTiming? MovingSubpictures { get; }
    private readonly ReadOnlyMemory<byte>[] decoderBytes;
    private DvdCellMedia(List<DvdStreamRead> units, long bytes, int audio, List<ReadOnlyMemory<byte>> decoderBytes,
        DvdSubpictureStream subpictures, IReadOnlyList<uint> palette, DvdSubpictureTiming? movingSubpictures)
    { Units = units.AsReadOnly(); ByteCount = bytes; AudioStreams = audio; this.decoderBytes = decoderBytes.ToArray(); Subpictures = subpictures; Palette = palette; MovingSubpictures = movingSubpictures; }

    internal static DvdCellMedia Prepare(DvdNavigationCatalog catalog, DvdDomainState initial,
        int byteLimit = 32 * 1024 * 1024, int unitLimit = 1024, CancellationToken cancellationToken = default)
    {
        if (byteLimit is < 2048 or > 32 * 1024 * 1024 || unitLimit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var chain = new DvdDomainResolver(catalog).StreamChain(initial);
        var cell = chain.Cells[initial.Position.Cell - 1];
        if (initial.Position.Sector != cell.FirstSector) throw new NotSupportedException("Native cell decoding currently requires authored cell entry.");
        var traversal = new DvdStreamTraversal(catalog);
        var units = new List<DvdStreamRead>(); var state = initial; long bytes = 0;
        var audio = new HashSet<byte>(); var video = new HashSet<byte>();
        var subpictures = new DvdSubpictureStream(); var decoderBytes = new List<ReadOnlyMemory<byte>>();
        var videoPts = new List<long>(); var videoStarted = false; var unanchoredVideo = false;
        var heldMenu = chain.Key.Domain is DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.TitleSetMenu &&
            (cell.StillSeconds != 0 || initial.Position.Cell == chain.Cells.Count && chain.StillSeconds != 0);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native DVD cell preparation deadline exceeded.");
            if (units.Count == unitLimit) throw new NotSupportedException("Native DVD cell exceeds the VOBU preparation budget.");
            var read = traversal.Read(state, cancellationToken);
            if (read.Status != DvdStreamStatus.Ready) throw new NotSupportedException(read.Reason);
            var unit = read.Unit!;
            if (bytes > byteLimit - unit.ByteCount) throw new NotSupportedException("Native DVD cell exceeds the byte preparation budget.");
            // Private audio still requires compatibility. Replace admitted SPU
            // with equal-length padding in the decoder view only, retaining the
            // original traversal/NAV bytes.
            byte[]? filtered = null;
            for (var offset = 2048; offset < unit.ByteCount; offset += 2048)
            {
                var sector = unit.Bytes.Span.Slice(offset, 2048);
                for (var packet = 14 + (sector[13] & 7); packet < sector.Length;)
                {
                    var id = sector[packet + 3];
                    var length = 6 + BinaryPrimitives.ReadUInt16BigEndian(sector[(packet + 4)..]);
                    if (id == 0xbd)
                    {
                        subpictures.AddPes(sector.Slice(packet, length), cancellationToken);
                        filtered ??= unit.Bytes.ToArray();
                        filtered[offset + packet + 3] = 0xbe;
                        filtered.AsSpan(offset + packet + 6, length - 6).Fill(0xff);
                    }
                    if (id is >= 0xc0 and <= 0xdf) audio.Add(id);
                    if (id is >= 0xe0 and <= 0xef)
                    {
                        video.Add(id);
                        var pts = DvdPesTimestamp.Read(sector.Slice(packet, length));
                        if (!videoStarted && pts is null) unanchoredVideo = true;
                        videoStarted = true;
                        if (pts.HasValue)
                        {
                            if (videoPts.Count == 65536) throw new NotSupportedException("DVD video timestamp budget exceeded.");
                            videoPts.Add(pts.Value);
                        }
                    }
                    packet += length;
                }
            }
            if (audio.Count > 1 || video.Count > 1) throw new NotSupportedException("Native DVD alternate stream selection is not ready.");
            units.Add(read); bytes += unit.ByteCount;
            decoderBytes.Add(filtered is null ? unit.Bytes : filtered);
            if (read.Next is null) break;
            state = read.Next;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native DVD cell preparation deadline exceeded.");
        if (video.Count != 1) throw new NotSupportedException("The DVD cell has no supported video stream.");
        subpictures.Complete();
        if (subpictures.HasPictures && (chain.Palette.Count != 16 || chain.Palette.Any(value => (value & 0xff000000) != 0)))
            throw new NotSupportedException("DVD subpictures require the authored PGC palette.");
        DvdSubpictureTiming? moving = null;
        // Legacy still-only cells can lack a video clock. A timestamped menu
        // keeps the same timed navigation through motion and the authored hold.
        var clocked = !unanchoredVideo && videoPts.Count != 0;
        var movingMenu = (!heldMenu || clocked) && (chain.Key.Domain is DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.TitleSetMenu) &&
            units.Any(read => read.Unit!.Navigation.Highlight.Update != DvdHighlightUpdate.None);
        if (movingMenu && (unanchoredVideo || videoPts.Count == 0))
            throw new NotSupportedException("Moving DVD menu needs a timestamp on the first video PES.");
        if (subpictures.HasPictures && (!heldMenu || clocked) || movingMenu)
        {
            if (unanchoredVideo) throw new NotSupportedException("Moving DVD subpictures need a timestamp on the first video PES.");
            var start = units[0].Unit!.Navigation.Start90Khz; var end = units[^1].Unit!.Navigation.End90Khz;
            long previousEnd = 0; var duration = unchecked(end - start);
            foreach (var read in units)
            {
                var nav = read.Unit!.Navigation;
                var begin = (long)unchecked(nav.Start90Khz - start); var finish = (long)unchecked(nav.End90Khz - start);
                if (begin < previousEnd || finish < begin || finish > duration) throw new NotSupportedException("Discontinuous DVD NAV clocks require compatibility playback.");
                previousEnd = finish;
            }
            var context = new DvdMenuContext(chain.Key.Domain == DvdPlaybackDomain.ManagerMenu ? DvdMenuDomain.Manager : DvdMenuDomain.TitleSetMenu,
                chain.Key.TitleSet, chain.Key.Number, initial.Epoch);
            moving = new(subpictures, chain.Palette, new DvdVideoClock(videoPts, start, end),
                movingMenu ? new DvdMovingMenu(units, context, initial.Registers, chain.ProhibitedOperations) : null);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native DVD cell preparation deadline exceeded.");
        return new(units, bytes, audio.Count, decoderBytes, subpictures, chain.Palette, moving);
    }

    internal DvdCellComStream OpenStream() => new(decoderBytes);
}

// Media Foundation owns its IMFByteStream wrapper; the session retains this CCW
// until the engine has shut down. Seek/read never touch the disc or navigation VM.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class DvdCellComStream : IStream, IDisposable
{
    private readonly object sync = new();
    private ReadOnlyMemory<byte>[] segments;
    private readonly long[] starts;
    private readonly long length;
    private long position;
    private bool disposed;
    internal DvdCellComStream(ReadOnlyMemory<byte>[] source)
    {
        segments = source.Where(segment => !segment.IsEmpty).ToArray(); starts = new long[segments.Length];
        for (var i = 0; i < segments.Length; i++) { starts[i] = length; length = checked(length + segments[i].Length); }
    }
    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        if (cb < 0 || cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var count = (int)Math.Min(Math.Min(cb, 1024 * 1024), Math.Max(0, length - position));
            var copied = 0;
            while (copied < count)
            {
                var index = Array.BinarySearch(starts, position);
                if (index < 0) index = ~index - 1;
                var offset = (int)(position - starts[index]);
                var bytes = Math.Min(count - copied, segments[index].Length - offset);
                segments[index].Span.Slice(offset, bytes).CopyTo(pv.AsSpan(copied));
                copied += bytes; position += bytes;
            }
            if (pcbRead != 0) Marshal.WriteInt32(pcbRead, count);
        }
    }
    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var origin = dwOrigin switch { 0 => 0, 1 => position, 2 => length, _ => throw new ArgumentOutOfRangeException(nameof(dwOrigin)) };
            var target = checked(origin + dlibMove);
            if (target < 0) throw new IOException("Cannot seek before the DVD cell start.");
            position = target;
            if (plibNewPosition != 0) Marshal.WriteInt64(plibNewPosition, position);
        }
    }
    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); pstatstg = new() { type = 2, cbSize = length, grfMode = 0 }; }
    }
    public void Dispose() { lock (sync) { disposed = true; segments = []; } }
    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new NotSupportedException();
    public void SetSize(long libNewSize) => throw new NotSupportedException();
    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotSupportedException();
    public void Commit(int grfCommitFlags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}
