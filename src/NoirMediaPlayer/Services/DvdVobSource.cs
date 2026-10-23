using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

// Sector addresses are relative to one domain's VOB files, never disc LBAs.
// A source owns read-only handles for exactly one such address space.
internal sealed class DvdVobSource : IDisposable
{
    internal const int SectorSize = 2048;
    internal const int MaximumReadSectors = 1024;
    private sealed record Part(FileStream File, long Start, long Length);
    private readonly List<Part> parts = [];
    private bool disposed;
    internal long SectorCount { get; }

    internal DvdVobSource(string directory, DvdPgcKey key, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an explicit local VIDEO_TS directory.", nameof(directory));
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked VIDEO_TS directories are not opened.");
        var title = key.Domain == DvdPlaybackDomain.Title;
        var manager = key.Domain is DvdPlaybackDomain.ManagerMenu or DvdPlaybackDomain.FirstPlay;
        if ((!title && !manager && key.Domain != DvdPlaybackDomain.TitleSetMenu) ||
            (manager ? key.TitleSet != 0 : key.TitleSet is < 1 or > 99))
            throw new InvalidDataException("Invalid DVD stream domain/title set.");
        try
        {
            long length = 0;
            var gap = false;
            for (var number = title ? 1 : 0; number <= (title ? 9 : 0); number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = manager ? "VIDEO_TS.VOB" : $"VTS_{key.TitleSet:00}_{number}.VOB";
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) { gap = true; continue; }
                if (gap) throw new InvalidDataException("The DVD title VOB sequence contains a missing part.");
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked VOB files are not opened.");
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
                parts.Add(new(file, length, file.Length));
                if (file.Length == 0 || file.Length % SectorSize != 0 || file.Length > 1024L * 1024 * 1024)
                    throw new InvalidDataException("A DVD VOB part has an invalid sector-aligned size.");
                length = checked(length + file.Length);
            }
            if (parts.Count == 0) throw new FileNotFoundException("No VOB files were found for the resolved DVD domain.");
            SectorCount = length / SectorSize;
        }
        catch { Dispose(); throw; }
    }

    internal void Read(uint sector, Span<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (destination.Length == 0 || destination.Length % SectorSize != 0 || destination.Length > MaximumReadSectors * SectorSize)
            throw new ArgumentOutOfRangeException(nameof(destination));
        var offset = (long)sector * SectorSize;
        if (offset + destination.Length > SectorCount * SectorSize)
            throw new InvalidDataException("The DVD read points outside its domain's VOB files.");
        var started = Stopwatch.GetTimestamp();
        while (!destination.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) throw new TimeoutException("DVD VOB read deadline exceeded.");
            var part = parts.Last(value => value.Start <= offset);
            if (part.File.Length != part.Length) throw new InvalidDataException("A DVD VOB file changed while reading.");
            var position = offset - part.Start;
            var count = (int)Math.Min(Math.Min(destination.Length, part.Length - position), 65536);
            part.File.Position = position;
            part.File.ReadExactly(destination[..count]);
            offset += count; destination = destination[count..];
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) throw new TimeoutException("DVD VOB read deadline exceeded.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var part in parts) part.File.Dispose();
        parts.Clear();
    }
}
