using System.Diagnostics;
using System.IO;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

/// <summary>
/// Collects non-fatal problems hit while walking folders and playlists, so a scan that was
/// truncated by an I/O error is not reported to the user as a complete one. Written from the
/// background import worker and read from the UI thread.
/// </summary>
public sealed class MediaScanDiagnostics
{
    private int _faulted;

    public bool Faulted => Volatile.Read(ref _faulted) != 0;

    public void MarkFaulted() => Volatile.Write(ref _faulted, 1);
}

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

    private static readonly HashSet<string> NetworkSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        "rtsp",
        "rtp",
        "udp"
    };

    public static string OpenFileFilter =>
        "Media files|*.3gp;*.aac;*.ac3;*.aiff;*.asf;*.avi;*.divx;*.dts;*.flac;*.flv;*.m2ts;*.m4a;*.m4v;*.mka;*.mkv;*.mov;*.mp2;*.mp3;*.mp4;*.mpeg;*.mpg;*.mts;*.ogg;*.ogm;*.opus;*.rm;*.rmvb;*.ts;*.vob;*.wav;*.webm;*.wma;*.wmv;*.m3u;*.m3u8|Video files|*.avi;*.divx;*.flv;*.m2ts;*.m4v;*.mkv;*.mov;*.mp4;*.mpeg;*.mpg;*.mts;*.rm;*.rmvb;*.ts;*.vob;*.webm;*.wmv|Audio files|*.aac;*.ac3;*.aiff;*.dts;*.flac;*.m4a;*.mka;*.mp2;*.mp3;*.ogg;*.opus;*.wav;*.wma|Playlists|*.m3u;*.m3u8|All files|*.*";

    public static bool IsSupported(string path) =>
        !string.IsNullOrWhiteSpace(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public static bool TryNormalizeNetworkLocation(string? location, out string normalizedLocation)
    {
        normalizedLocation = string.Empty;
        if (string.IsNullOrWhiteSpace(location) ||
            !Uri.TryCreate(location.Trim(), UriKind.Absolute, out var uri) ||
            uri.IsFile ||
            !NetworkSchemes.Contains(uri.Scheme) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        normalizedLocation = uri.AbsoluteUri;
        return true;
    }

    public static IEnumerable<string> EnumerateFolder(
        string folder,
        CancellationToken cancellationToken = default,
        MediaScanDiagnostics? diagnostics = null)
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
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            diagnostics?.MarkFaulted();
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
            catch (Exception exception)
            {
                // A mid-walk failure (removed drive, dropped share) truncates the scan; record
                // it so the caller does not present a partial result as a complete one.
                Debug.WriteLine(exception);
                diagnostics?.MarkFaulted();
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (IsSupported(current))
            {
                yield return current;
            }
        }
    }

    public static IEnumerable<string> ExpandFiles(
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default,
        MediaScanDiagnostics? diagnostics = null)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryNormalizeNetworkLocation(path, out var networkLocation))
            {
                yield return networkLocation;
                continue;
            }

            if (Directory.Exists(path))
            {
                foreach (var mediaPath in EnumerateFolder(path, cancellationToken, diagnostics))
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
                foreach (var playlistPath in ReadM3u(path, cancellationToken, diagnostics))
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

    public static PlaylistItem CreateNetworkItem(string location)
    {
        if (!TryNormalizeNetworkLocation(location, out var normalizedLocation))
        {
            throw new ArgumentException("The location is not a supported network media URI.", nameof(location));
        }

        var uri = new Uri(normalizedLocation, UriKind.Absolute);
        return new PlaylistItem
        {
            Source = normalizedLocation,
            Title = uri.Host,
            // Source keeps the credentials because playback needs them; Detail is rendered in
            // the queue and the inspector, so it must not put a password on screen.
            Detail = RedactCredentials(normalizedLocation),
            IsNetwork = true
        };
    }

    /// <summary>
    /// Removes any <c>user:password@</c> userinfo from a network URI while leaving the rest of
    /// it playable. Non-network sources are returned unchanged.
    /// </summary>
    public static string RedactCredentials(string source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.IsFile ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return source;
        }

        try
        {
            return new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty
            }.Uri.AbsoluteUri;
        }
        catch (Exception exception) when (exception is UriFormatException or ArgumentException)
        {
            // Never hand back the original on a formatting failure: that would leak the secret
            // this method exists to remove.
            return $"{uri.Scheme}://{uri.Host}";
        }
    }

    private static IEnumerable<string> ReadM3u(
        string playlistPath,
        CancellationToken cancellationToken,
        MediaScanDiagnostics? diagnostics)
    {
        var parent = Path.GetDirectoryName(playlistPath) ?? Environment.CurrentDirectory;
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(playlistPath);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            diagnostics?.MarkFaulted();
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
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
                diagnostics?.MarkFaulted();
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (TryNormalizeNetworkLocation(line, out var networkLocation))
            {
                yield return networkLocation;
                continue;
            }

            if (TryResolvePlaylistFile(parent, line, out var resolved))
            {
                yield return resolved;
            }
        }
    }

    private static bool TryResolvePlaylistFile(string parent, string entry, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            // File URIs and UNC paths in a playlist can silently trigger access to local
            // resources or outbound SMB authentication. Explicit file/folder selection
            // remains available for locations the user intentionally chose.
            if (entry.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || IsUncPath(entry))
            {
                return false;
            }

            var candidate = Path.IsPathRooted(entry) ? entry : Path.Combine(parent, entry);
            var fullPath = Path.GetFullPath(candidate);
            if (IsUncPath(fullPath) || !File.Exists(fullPath) || !IsSupported(fullPath))
            {
                return false;
            }

            resolvedPath = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsUnc;

    private static string CleanTitle(string value) =>
        value.Replace('.', ' ').Replace('_', ' ').Trim();
}
