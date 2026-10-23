using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NoirMediaPlayer.Services;

[Flags]
internal enum SubtitleStyle { None = 0, Bold = 1, Italic = 2, Underline = 4 }

internal readonly record struct SubtitleStyleRange(int Start, int Length, SubtitleStyle Style, uint? Foreground = null,
    uint? Background = null, string? Language = null);

internal enum SubtitleAlignment
{
    BottomLeft = 1, BottomCenter, BottomRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    TopLeft, TopCenter, TopRight
}

internal sealed record SubtitleCue(long Start, long End, string Text)
{
    internal IReadOnlyList<SubtitleStyleRange> Styles { get; } = Array.Empty<SubtitleStyleRange>();
    internal SubtitleAlignment Alignment { get; init; } = SubtitleAlignment.BottomCenter;

    internal SubtitleCue(long start, long end, string text, SubtitleStyleRange[] styles,
        SubtitleAlignment alignment = SubtitleAlignment.BottomCenter) : this(start, end, text)
    {
        Styles = Array.AsReadOnly((SubtitleStyleRange[])styles.Clone());
        Alignment = alignment;
    }
}

/// <summary>SubRip/WebVTT text, alignment, styles, colors, voice annotations and supported languages. Unsupported layouts retain VLC.</summary>
internal sealed partial class PlainSubtitleTrack
{
    internal string Path { get; }
    internal string Name => System.IO.Path.GetFileName(Path);
    internal IReadOnlyList<SubtitleCue> Cues { get; }
    private (long Length, DateTime Modified)? _sourceVersion;

    // A compatibility renderer loads the original file independently. If it has
    // changed since parsing, its cached captions must stay with that renderer.
    internal bool HasUnchangedSource
    {
        get
        {
            if (_sourceVersion is not { } version) return false;
            try
            {
                var file = new FileInfo(Path);
                return file.Exists && file.Length == version.Length && file.LastWriteTimeUtc == version.Modified;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }
    }

    private PlainSubtitleTrack(string path, SubtitleCue[] cues) { Path = path; Cues = cues; }

    internal static PlainSubtitleTrack? TryLoad(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        var webVtt = IsWebVtt(path);
        if (!webVtt && !extension.Equals(".srt", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) return null;
            // WebVTT requires UTF-8. SubRip also supports BOM-marked UTF-16 files.
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: !webVtt);
            var text = reader.ReadToEnd();
            var track = Parse(System.IO.Path.GetFullPath(path), text);
            if (track is not null) track._sourceVersion = (stream.Length, File.GetLastWriteTimeUtc(path));
            return track;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    internal static PlainSubtitleTrack? Parse(string path, string text)
    {
        if (text.Length > 4 * 1024 * 1024) return null;
        return IsWebVtt(path)
            ? ParseWebVtt(path, text) : ParseSubRip(path, text);
    }

    private static bool IsWebVtt(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".vtt" or ".webvtt";

    private static PlainSubtitleTrack? ParseSubRip(string path, string text)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<SubtitleCue>();
        var index = 0;
        while (index < lines.Length)
        {
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index == lines.Length) break;
            if (int.TryParse(lines[index], out var sequence) && sequence >= 0) index++;
            if (index == lines.Length) return null;
            var match = Timing().Match(lines[index++]);
            if (!match.Success) return null;
            long Time(int group) => int.Parse(match.Groups[group].Value) * 3_600_000L +
                int.Parse(match.Groups[group + 1].Value) * 60_000L + int.Parse(match.Groups[group + 2].Value) * 1000L + int.Parse(match.Groups[group + 3].Value);
            var start = Time(1);
            var end = Time(5);
            if (end <= start || (cues.Count > 0 && start < cues[^1].End)) return null;
            var content = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) content.Add(lines[index++]);
            var caption = string.Join('\n', content);
            if (caption.Length > 2048) return null;
            var alignment = SubtitleAlignment.BottomCenter;
            // The common SubRip alignment extension applies to the whole cue.
            // Admit one exact leading tag; other overrides still reach VLC intact.
            if (caption.Length >= 6 && caption.StartsWith("{\\an", StringComparison.Ordinal) &&
                caption[4] is >= '1' and <= '9' && caption[5] == '}')
            {
                alignment = (SubtitleAlignment)(caption[4] - '0');
                caption = caption[6..];
            }
            if (content.Count is < 1 or > 4 || !TryParseInline(caption, webVtt: false, out var textValue, out var styles)) return null;
            cues.Add(new SubtitleCue(start, end, textValue, styles, alignment));
            if (cues.Count > 100_000) return null;
        }
        return cues.Count == 0 ? null : new PlainSubtitleTrack(path, cues.ToArray());
    }

    // Strictly accept cues whose meaning the shared plain-text renderer preserves.
    // CSS, regions, positioning, richer tags and karaoke timestamps retain VLC.
    private static PlainSubtitleTrack? ParseWebVtt(string path, string text)
    {
        var lines = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length < 2 || !(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal) ||
            lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)) || lines[0].Contains("-->") || lines[1].Length != 0) return null;
        var cues = new List<SubtitleCue>();
        var index = 2;
        while (index < lines.Length)
        {
            while (index < lines.Length && lines[index].Length == 0) index++;
            if (index == lines.Length) break;
            if (lines[index] == "NOTE" || lines[index].StartsWith("NOTE ", StringComparison.Ordinal) || lines[index].StartsWith("NOTE\t", StringComparison.Ordinal))
            {
                while (index < lines.Length && lines[index].Length != 0) index++;
                continue;
            }
            if (lines[index] is "STYLE" or "REGION") return null;
            if (!lines[index].Contains("-->")) index++; // Optional cue identifier, including numeric identifiers.
            if (index == lines.Length) return null;
            var match = WebVttTiming().Match(lines[index++]);
            if (!match.Success) return null;
            long Time(int group) => (match.Groups[group].Success ? int.Parse(match.Groups[group].Value) * 3_600_000L : 0) +
                int.Parse(match.Groups[group + 1].Value) * 60_000L + int.Parse(match.Groups[group + 2].Value) * 1000L + int.Parse(match.Groups[group + 3].Value);
            var start = Time(1);
            var end = Time(5);
            if (end <= start || (cues.Count > 0 && start < cues[^1].End)) return null;
            var content = new List<string>();
            while (index < lines.Length && lines[index].Length != 0) content.Add(lines[index++]);
            var caption = string.Join('\n', content);
            if (content.Count is < 1 or > 4 || !TryParseInline(caption, webVtt: true, out var textValue, out var styles)) return null;
            cues.Add(new SubtitleCue(start, end, textValue, styles));
            if (cues.Count > 100_000) return null;
        }
        return cues.Count == 0 ? null : new PlainSubtitleTrack(path, cues.ToArray());
    }

    internal string TextAt(long time) => CueAt(time)?.Text ?? string.Empty;

    internal SubtitleCue? CueAt(long time)
    {
        var low = 0;
        var high = Cues.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var cue = Cues[middle];
            if (time < cue.Start) high = middle - 1;
            else if (time >= cue.End) low = middle + 1;
            else return cue;
        }
        return null;
    }

    [GeneratedRegex(@"^\s*([0-9]{2,3}):([0-5][0-9]):([0-5][0-9]),([0-9]{3})\s+-->\s+([0-9]{2,3}):([0-5][0-9]):([0-5][0-9]),([0-9]{3})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Timing();

    [GeneratedRegex(@"^(?:([0-9]{2,3}):)?([0-5][0-9]):([0-5][0-9])\.([0-9]{3})[ \t]+-->[ \t]+(?:([0-9]{2,3}):)?([0-5][0-9]):([0-5][0-9])\.([0-9]{3})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex WebVttTiming();

}
