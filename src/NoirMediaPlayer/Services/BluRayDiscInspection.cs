using System.IO;

namespace NoirMediaPlayer.Services;

internal sealed record BluRayPlaylistInspection(string File, BluRayPlaylist? Playlist, string? Error);

internal sealed record BluRayDiscInspection(
    bool PlaybackVerified, bool MainTitleSelected, string Note, IReadOnlyList<BluRayPlaylistInspection> Playlists)
{
    internal static BluRayDiscInspection Read(string source, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(source);
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use a local disc, disc folder or metadata export.", nameof(source));
        string[] files;
        if (Path.GetExtension(path).Equals(".mpls", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            files = [path];
        }
        else
        {
            var directory = new DirectoryInfo(path);
            var bdmv = directory.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                ? directory.FullName : Path.Combine(path, "BDMV");
            var playlistFolder = Path.Combine(bdmv, "PLAYLIST");
            var discovered = new List<string>();
            foreach (var file in Directory.EnumerateFiles(playlistFolder, "*.mpls", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (discovered.Count >= 4096) throw new InvalidDataException("Too many MPLS files for inspection.");
                discovered.Add(file);
            }
            files = discovered.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (files.Length == 0) throw new InvalidDataException("No Blu-ray playlists were found.");

        // Preflight the whole directory before reading individual files. A bad playlist
        // still gets a diagnostic entry; large inventories never become an unbounded scan.
        long bytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked playlist files are not inspected.");
            bytes = checked(bytes + info.Length);
            if (bytes > 32 * 1024 * 1024) throw new InvalidDataException("Playlist metadata exceeds the 32 MiB inspection limit.");
        }
        var results = new List<BluRayPlaylistInspection>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var playlist = BluRayPlaylistReader.Read(file);
                results.Add(new BluRayPlaylistInspection(Path.GetFileName(file), playlist, null));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                results.Add(new BluRayPlaylistInspection(Path.GetFileName(file), null, exception.Message));
            }
        }
        return new BluRayDiscInspection(false, false,
            "Metadata inspection only. Sequential durations exclude still holds and random/shuffle behavior. " +
            "No main movie was inferred, no media or AACS data was read, and playback, menus, codecs and protection were not verified.",
            results.AsReadOnly());
    }
}
