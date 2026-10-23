using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer;

public partial class MainWindow
{
    private string? SaveDvdFailureDiagnostic(PlaylistItem item, bool timedOut)
    {
        if (!item.Source.StartsWith("dvd:", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            string[] entries;
            lock (_engineLogSync) entries = _recentDiscEngineLogs.ToArray();
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NoirMediaPlayer", "DiscDiagnostics");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"dvd-compatibility-failure-{Guid.NewGuid():N}.json");
            var accessPlugins = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "plugins", "access");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, new
            {
                Utc = DateTimeOffset.UtcNow,
                Backend = "VLC DVD compatibility",
                item.Source,
                TimedOut = timedOut,
                DvdNavigationPluginPresent = File.Exists(Path.Combine(accessPlugins, "libdvdnav_plugin.dll")),
                DvdReadingPluginPresent = File.Exists(Path.Combine(accessPlugins, "libdvdread_plugin.dll")),
                RecentEngineMessages = entries
            });
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Reporting must never replace the original playback error.
            Debug.WriteLine(exception);
            return null;
        }
    }
}
