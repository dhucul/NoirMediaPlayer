using System.IO;
using NoirMediaPlayer.Models;

namespace NoirMediaPlayer.Services;

internal sealed record WindowsPlaybackPlan(IReadOnlyList<PlainSubtitleTrack> AutomaticSubtitles,
    string? SelectedSubtitlePath = null, NativeChapterMap? Chapters = null, string? DetectedContentType = null,
    bool UsesAsfMarkers = false);

/// <summary>Only migrate sources whose basic playback can be retried safely in VLC.</summary>
internal static class WindowsPlaybackPolicy
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mp3", ".m4a", ".aac", ".wav", ".wma",
        ".3gp", ".asf", ".flac", ".mkv"
    };
    // Download/API endpoints are candidates only. Startup must identify supported
    // content through bounded byte ranges before Windows receives the URL.
    internal static bool IsWebEndpoint(string extension) => extension.ToLowerInvariant() is
        "" or ".php" or ".asp" or ".aspx" or ".ashx" or ".axd" or ".cgi" or ".bin" or ".download";
    internal static bool CanTry(PlaylistItem item, PlayerSettings settings, int rotation, string? aspectRatio) =>
        CreatePlan(item, settings, rotation, aspectRatio) is not null;

    internal static WindowsPlaybackPlan? CreatePlan(PlaylistItem item, PlayerSettings settings, int rotation, string? aspectRatio)
    {
        if (!settings.PreferWindowsPlayback || item.IsDisc ||
            rotation is not (0 or 90 or 180 or 270) || !WindowsMediaEngine.SupportsAspectRatio(aspectRatio))
            return null;

        if (item.IsNetwork)
        {
            if (!Uri.TryCreate(item.Source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) return null;
            var extension = Path.GetExtension(uri.AbsolutePath);
            // This is only a candidate. Actual startup completes bounded HTTP
            // inspection before admitting Matroska's physical playback timeline.
            return Extensions.Contains(extension) || IsWebEndpoint(extension) ? new WindowsPlaybackPlan([]) : null;
        }
        var mpeg = MpegStreamInspection.SupportsExtension(item.Source);
        if (!Extensions.Contains(Path.GetExtension(item.Source)) && !mpeg) return null;
        var plan = WindowsSubtitleDiscovery.Inspect(item.Source);
        if (plan is null) return null;
        if (mpeg)
        {
            // MPEG caption handoff can omit a paused first-frame cue. Keep its
            // complete caption session on VLC until composition is verified.
            return plan.AutomaticSubtitles.Count > 0 || MpegStreamInspection.TryRead(item.Source) is null
                ? null : plan with { Chapters = new NativeChapterMap([]) };
        }
        var localExtension = Path.GetExtension(item.Source);
        if (WindowsAsfChapters.SupportsExtension(item.Source))
            return plan with { Chapters = WindowsAsfChapters.TryRead(item.Source), UsesAsfMarkers = true };
        if (localExtension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            localExtension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
            localExtension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
            localExtension.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
            localExtension.Equals(".3gp", StringComparison.OrdinalIgnoreCase))
        {
            // MP4 chapter markers do not create a virtual playback timeline. Unknown
            // layouts can still play natively, then hand chapter navigation to VLC.
            var mp4Chapters = Mp4Chapters.TryRead(item.Source, out var requiresCompatibility);
            return requiresCompatibility ? null : plan with { Chapters = mp4Chapters };
        }
        if (!localExtension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)) return plan;
        // An ordered or linked edition changes the playback timeline itself. Detect
        // it before starting Windows playback, rather than only at a chapter click.
        var chapters = MatroskaChapters.TryRead(item.Source);
        return chapters is null ? null : plan with { Chapters = chapters };
    }
}
