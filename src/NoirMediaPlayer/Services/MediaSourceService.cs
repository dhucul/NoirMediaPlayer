using System.IO;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public static class MediaSourceService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".aac", ".ac3", ".aiff", ".asf", ".avi", ".divx", ".dts", ".flac", ".flv",
        ".m2ts", ".m4a", ".m4v", ".mka", ".mkv", ".mov", ".mp2", ".mp3", ".mp4", ".mpeg",
        ".mpg", ".mts", ".ogg", ".ogm", ".opus", ".rm", ".rmvb", ".ts", ".vob", ".wav",
        ".webm", ".wma", ".wmv"
    };

    private static readonly HashSet<string> PlaylistExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8"
    };

    public static string OpenFileFilter =>
        "Media files|*.3gp;*.aac;*.ac3;*.aiff;*.asf;*.avi;*.divx;*.dts;*.flac;*.flv;*.m2ts;*.m4a;*.m4v;*.mka;*.mkv;*.mov;*.mp2;*.mp3;*.mp4;*.mpeg;*.mpg;*.mts;*.ogg;*.ogm;*.opus;*.rm;*.rmvb;*.ts;*.vob;*.wav;*.webm;*.wma;*.wmv;*.m3u;*.m3u8|Video files|*.avi;*.divx;*.flv;*.m2ts;*.m4v;*.mkv;*.mov;*.mp4;*.mpeg;*.mpg;*.mts;*.rm;*.rmvb;*.ts;*.vob;*.webm;*.wmv|Audio files|*.aac;*.ac3;*.aiff;*.dts;*.flac;*.m4a;*.mka;*.mp2;*.mp3;*.ogg;*.opus;*.wav;*.wma|Playlists|*.m3u;*.m3u8|All files|*.*";

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public static IEnumerable<string> EnumerateFolder(string folder, CancellationToken cancellationToken = default)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            });
        }
        catch
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupported(current))
            {
                yield return current;
            }
        }
    }

    public static IEnumerable<string> ExpandFiles(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (var mediaPath in EnumerateFolder(path, cancellationToken))
                {
                    yield return mediaPath;
                }

                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            if (PlaylistExtensions.Contains(Path.GetExtension(path)))
            {
                foreach (var playlistPath in ReadM3u(path, cancellationToken))
                {
                    yield return playlistPath;
                }
            }
            else if (IsSupported(path))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    public static PlaylistItem CreateFileItem(string path) => new()
    {
        Source = Path.GetFullPath(path),
        Title = CleanTitle(Path.GetFileNameWithoutExtension(path)),
        Detail = Path.GetDirectoryName(path) ?? string.Empty
    };

    public static PlaylistItem CreateNetworkItem(string location) => new()
    {
        Source = location,
        Title = Uri.TryCreate(location, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "Network stream",
        Detail = location,
        IsNetwork = true
    };

    private static IEnumerable<string> ReadM3u(string playlistPath, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(playlistPath) ?? Environment.CurrentDirectory;
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(playlistPath);
        }
        catch
        {
            yield break;
        }

        using var enumerator = lines.GetEnumerator();
        while (true)
        {
            string rawLine;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                rawLine = enumerator.Current;
            }
            catch
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                yield return line;
                continue;
            }

            var resolved = Path.IsPathRooted(line) ? line : Path.Combine(parent, line);
            if (File.Exists(resolved) && IsSupported(resolved))
            {
                yield return Path.GetFullPath(resolved);
            }
        }
    }

    private static string CleanTitle(string value) =>
        value.Replace('.', ' ').Replace('_', ' ').Trim();
}
