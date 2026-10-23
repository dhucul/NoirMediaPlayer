using System.IO;

namespace NoirMediaPlayer.Services;

// Read-only Windows filesystem access for a validated linear title. Cell sectors
// address the concatenation of VTS_nn_1.VOB through VTS_nn_9.VOB, not disc LBAs.
// This preserves the stored NAV/PES bytes; it neither decrypts nor normalizes PTS.
internal sealed class DvdTitleStream : Stream
{
    private sealed record Part(FileStream File, long Start);
    private sealed record Extent(long Start, long Physical, long Length);
    private readonly object _sync = new();
    private readonly List<Part> _parts = [];
    private readonly List<Extent> _extents = [];
    private readonly DvdLinearTitleMap _map;
    private readonly long _length;
    private long _position;
    private bool _disposed;

    internal DvdTitleStream(string videoTsDirectory, int titleSet, DvdLinearTitleMap map)
    {
        if (titleSet is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(titleSet));
        if (!Path.IsPathFullyQualified(videoTsDirectory) || videoTsDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an explicit local VIDEO_TS directory.", nameof(videoTsDirectory));
        _map = map;
        try
        {
            long physicalLength = 0, logicalLength = 0;
            var gap = false;
            for (var number = 1; number <= 9; number++)
            {
                var path = Path.Combine(videoTsDirectory, $"VTS_{titleSet:00}_{number}.VOB");
                if (!File.Exists(path)) { gap = true; continue; }
                if (gap) throw new InvalidDataException("The DVD title VOB sequence contains a missing part.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked VOB files are not opened.");
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
                _parts.Add(new(file, physicalLength));
                if (file.Length == 0 || file.Length % 2048 != 0 || file.Length > 1024L * 1024 * 1024)
                    throw new InvalidDataException("A DVD VOB part has an invalid sector-aligned size.");
                physicalLength = checked(physicalLength + file.Length);
            }
            if (_parts.Count == 0) throw new FileNotFoundException("No DVD title VOB files were found.");
            foreach (var cell in map.Cells)
            {
                var first = checked((long)cell.FirstSector * 2048);
                var length = checked(((long)cell.LastSector - cell.FirstSector + 1) * 2048);
                if (length <= 0 || first > physicalLength || length > physicalLength - first)
                    throw new InvalidDataException("A DVD cell points outside its title VOB files.");
                _extents.Add(new(logicalLength, first, length));
                logicalLength = checked(logicalLength + length);
            }
            if (_extents.Count == 0) throw new InvalidDataException("The DVD title has no physical cells.");
            _length = logicalLength;
        }
        catch { Dispose(); throw; }
    }

    internal DvdChapterTarget SelectChapter(int number)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = _map.SelectChapter(number);
            _position = _extents[target.Cell - 1].Start;
            return target;
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length { get { lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); return _length; } } }
    public override long Position
    {
        get { lock (_sync) { ObjectDisposedException.ThrowIf(_disposed, this); return _position; } }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Bound each synchronous request even if a future decoder asks for a huge buffer.
            var count = (int)Math.Min(Math.Min(buffer.Length, 1024 * 1024), _length - _position);
            var remaining = buffer[..count];
            while (!remaining.IsEmpty)
            {
                var extent = _extents.Last(value => value.Start <= _position);
                var inCell = _position - extent.Start;
                var physical = extent.Physical + inCell;
                var part = _parts.Last(value => value.Start <= physical);
                var inFile = physical - part.Start;
                var bytes = (int)Math.Min(remaining.Length, Math.Min(extent.Length - inCell, part.File.Length - inFile));
                if (bytes <= 0) throw new EndOfStreamException("A DVD VOB file changed while reading the title.");
                part.File.Position = inFile;
                part.File.ReadExactly(remaining[..bytes]);
                _position += bytes;
                remaining = remaining[bytes..];
            }
            return count;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (position < 0 || position > _length) throw new ArgumentOutOfRangeException(nameof(offset));
            return _position = position;
        }
    }

    public override void Flush() { ObjectDisposedException.ThrowIf(_disposed, this); }
    public override void SetLength(long value) => throw new NotSupportedException("DVD title streams are read-only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("DVD title streams are read-only.");
    protected override void Dispose(bool disposing)
    {
        lock (_sync)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                foreach (var part in _parts) part.File.Dispose();
                _parts.Clear();
            }
        }
        base.Dispose(disposing);
    }
}
