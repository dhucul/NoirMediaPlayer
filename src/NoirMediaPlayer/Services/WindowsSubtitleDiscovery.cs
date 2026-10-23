using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

/// <summary>Discover supported sidecars without changing the compatibility player's default selection.</summary>
internal static class WindowsSubtitleDiscovery
{
    // Include compatibility-only formats: ignoring one could hide a selectable track.
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".smi", ".sami", ".scc", ".sbv", ".ttml", ".dfxp",
        ".aqt", ".cvd", ".dks", ".jss", ".mpsub", ".mpl", ".mpl2", ".pjs", ".psb", ".rt", ".stl",
        ".ttxt", ".txt", ".usf", ".cdg", ".tt", ".webvtt"
    };

    internal static WindowsPlaybackPlan? Inspect(string source)
    {
        try
        {
            var directory = Path.GetDirectoryName(source);
            if (string.IsNullOrEmpty(directory)) return null;
            var stem = Path.GetFileNameWithoutExtension(source);
            var normalizedStem = Normalize(stem);
            if (normalizedStem.Length == 0) return null;
            var clock = Stopwatch.StartNew();
            var inspected = 0;
            var candidates = new List<(string Path, bool Exact)>();
            // These are VLC's default Windows search folders. The historical "sub"
            // guard remains conservative until non-default search paths migrate.
            foreach (var folder in new[] { directory, Path.Combine(directory, "subs"), Path.Combine(directory, "subtitles"), Path.Combine(directory, "sub") })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in Directory.EnumerateFiles(folder))
                {
                    if (++inspected > 4096 || clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
                    var name = Path.GetFileName(path);
                    if (name.StartsWith('.') || !Extensions.Contains(Path.GetExtension(path))) continue;
                    if (folder.Equals(Path.Combine(directory, "sub"), StringComparison.OrdinalIgnoreCase)) return null;
                    var candidateStem = Path.GetFileNameWithoutExtension(path);
                    var exact = candidateStem.Equals(stem, StringComparison.OrdinalIgnoreCase);
                    // The compatibility matcher is byte/locale based. For non-ASCII
                    // source names, only a lone exact match is demonstrably safe.
                    if (!exact && stem.Any(character => character > 127)) return null;
                    // A deliberately broad guard catches punctuation/fuzzy matches
                    // that we do not yet reproduce, rather than dropping their tracks.
                    if (!Normalize(candidateStem).Contains(normalizedStem, StringComparison.Ordinal)) continue;
                    if (!exact && !IsQualifiedSidecar(candidateStem, stem)) return null;
                    candidates.Add((path, exact));
                    if (candidates.Count > 32) return null;
                }
            }
            if (candidates.Count == 0) return new WindowsPlaybackPlan([]);
            // Exact names take precedence over descriptive suffixes. Equal top-priority
            // candidates retain VLC's ordering/language selection instead of guessing.
            var preferred = candidates.Where(candidate => candidate.Exact).ToArray();
            if (preferred.Length == 0) preferred = candidates.ToArray();
            if (preferred.Length != 1) return null;
            var tracks = new List<PlainSubtitleTrack>();
            long bytes = 0;
            var cues = 0;
            foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Exact)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
            {
                bytes += new FileInfo(candidate.Path).Length;
                if (bytes > 16 * 1024 * 1024 || clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
                var track = PlainSubtitleTrack.TryLoad(candidate.Path);
                if (track is null || (cues += track.Cues.Count) > 200_000) return null;
                tracks.Add(track);
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
            return new WindowsPlaybackPlan(Array.AsReadOnly(tracks.ToArray()), Path.GetFullPath(preferred[0].Path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsQualifiedSidecar(string candidate, string stem)
    {
        if (!candidate.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = candidate.AsSpan(stem.Length);
        if (suffix.IsEmpty || suffix[0] is not ('.' or '_' or '-' or ' ' or '[' or '(')) return false;
        // ASCII descriptions after a separator have the same discovery priority,
        // whether they describe a language, region, forced track, or accessibility.
        // Require actual text so punctuation-only names cannot compete as exact
        // matches. Locale-dependent and other fuzzy names retain compatibility.
        var hasDescription = false;
        foreach (var character in suffix)
        {
            if (char.IsAsciiLetterOrDigit(character)) hasDescription = true;
            else if (character is not ('.' or '_' or '-' or ' ' or '[' or ']' or '(' or ')')) return false;
        }
        return hasDescription;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
