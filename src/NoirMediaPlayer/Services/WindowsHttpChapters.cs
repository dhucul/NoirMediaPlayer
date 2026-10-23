using System.IO;
using System.Net.Http;

namespace NoirMediaPlayer.Services;

internal static class WindowsHttpChapters
{
    // This runs only for an actual playback attempt, never during URL routing.
    // Failed MP4 inspection leaves its existing chapter handoff available. Matroska
    // requires a validated physical timeline before playback itself can stay native.
    internal static WindowsPlaybackPlan? Inspect(string source, WindowsPlaybackPlan plan)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return plan;
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (WindowsPlaybackPolicy.IsWebEndpoint(extension)) return WindowsHttpMediaProbe.Inspect(source, plan);
        var matroska = extension == ".mkv";
        var asf = WindowsAsfChapters.SupportsExtension(uri.AbsolutePath);
        if (asf) return plan with { Chapters = WindowsAsfChapters.TryReadHttp(source), UsesAsfMarkers = true };
        if (!matroska && extension is not (".mp4" or ".m4v" or ".m4a" or ".mov" or ".3gp"))
            return plan;
        try
        {
            using var input = new WindowsSnapshotHttpStream(source, TimeSpan.FromSeconds(2), byteLimit: 8 * 1024 * 1024);
            using var stream = input.AsReadStream();
            if (matroska)
            {
                var markers = MatroskaChapters.TryRead(stream);
                return markers is null ? null : plan with { Chapters = markers };
            }
            var chapters = Mp4Chapters.TryRead(stream, out var requiresCompatibility);
            return requiresCompatibility ? null : plan with { Chapters = chapters };
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or OperationCanceledException or
            UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        {
            return matroska ? null : plan;
        }
    }
}
