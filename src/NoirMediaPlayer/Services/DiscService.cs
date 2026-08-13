using System.IO;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

public sealed record DiscInfo(string Root, string Label, string Source, string Format)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{Format} · {Root}" : $"{Label} · {Root}";
}

public static class DiscService
{
    public static IReadOnlyList<DiscInfo> FindVideoDiscs(CancellationToken cancellationToken = default)
    {
        var result = new List<DiscInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.CDRom || !drive.IsReady)
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                if (Directory.Exists(Path.Combine(root, "BDMV")))
                {
                    result.Add(new DiscInfo(root, drive.VolumeLabel, CreateDiscSource("bluray", root), "BLU-RAY"));
                }
                else if (Directory.Exists(Path.Combine(root, "VIDEO_TS")))
                {
                    result.Add(new DiscInfo(root, drive.VolumeLabel, CreateDiscSource("dvd", root), "DVD"));
                }
            }
            catch
            {
                // Ignore inaccessible or transitioning optical drives.
            }
        }

        return result;
    }

    public static PlaylistItem CreateItem(DiscInfo disc) => new()
    {
        Source = disc.Source,
        Title = string.IsNullOrWhiteSpace(disc.Label) ? $"{disc.Format} video" : disc.Label,
        Detail = disc.DisplayName,
        IsDisc = true
    };

    public static PlaylistItem? CreateFromFolder(string folder)
    {
        if (Directory.Exists(Path.Combine(folder, "VIDEO_TS")) &&
            !Directory.Exists(Path.Combine(folder, "BDMV")))
        {
            return new PlaylistItem
            {
                Source = CreateDiscSource("dvd", folder),
                Title = new DirectoryInfo(folder).Name,
                Detail = "DVD folder",
                IsDisc = true
            };
        }

        if (Directory.Exists(Path.Combine(folder, "BDMV")))
        {
            return new PlaylistItem
            {
                Source = CreateDiscSource("bluray", folder),
                Title = new DirectoryInfo(folder).Name,
                Detail = "Blu-ray folder",
                IsDisc = true
            };
        }

        return null;
    }

    public static bool IsAacsProtectedSource(string source)
    {
        if (!TryGetDiscFolder(source, "bluray", out var folder))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.Combine(folder, "AACS"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool TryGetDiscFolder(string source, string expectedScheme, out string folder)
    {
        folder = string.Empty;
        try
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                !uri.Scheme.Equals(expectedScheme, StringComparison.OrdinalIgnoreCase) ||
                uri.IsUnc)
            {
                return false;
            }

            folder = Path.GetFullPath(uri.LocalPath);
            return Path.IsPathFullyQualified(folder);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            folder = string.Empty;
            return false;
        }
    }

    private static string CreateDiscSource(string scheme, string folder)
    {
        var path = Path.GetFullPath(folder);
        var fileUri = new Uri(path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar);
        return $"{scheme}:///{fileUri.AbsolutePath.TrimStart('/')}";
    }
}
