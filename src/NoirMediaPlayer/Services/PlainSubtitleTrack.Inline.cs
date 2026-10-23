using System.Text;
using System.Globalization;

namespace NoirMediaPlayer.Services;

internal sealed partial class PlainSubtitleTrack
{
    // Balanced, bounded markup with UTF-16 ranges matching WPF's text APIs.
    // Unknown tags, attributes and color syntax are never silently stripped.
    private static bool TryParseInline(string source, bool webVtt, out string text, out SubtitleStyleRange[] ranges)
    {
        text = string.Empty;
        ranges = [];
        if (source.Length > 2048 || source.IndexOfAny(['{', '}', '\\']) >= 0 ||
            source.Any(character => char.IsControl(character) && character is not ('\n' or '\t'))) return false;
        var output = new StringBuilder(source.Length);
        var styles = new List<SubtitleStyleRange>();
        var stack = new Stack<(char Tag, SubtitleStyle Previous, uint? Foreground, uint? Background, string? Language, bool ImplicitVoiceEnd)>();
        var current = SubtitleStyle.None;
        uint? foreground = null;
        uint? background = null;
        string? language = null;
        var runStart = 0;
        void ChangeStyle(SubtitleStyle next, uint? color, uint? fill, string? culture)
        {
            if (next == current && foreground == color && background == fill && language == culture) return;
            if ((current != SubtitleStyle.None || foreground is not null || background is not null || language is not null) && output.Length > runStart)
                styles.Add(new SubtitleStyleRange(runStart, output.Length - runStart, current, foreground, background, language));
            current = next;
            foreground = color;
            background = fill;
            language = culture;
            runStart = output.Length;
        }
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '<')
            {
                var end = source.IndexOf('>', index + 1);
                if (end < 0) return false;
                var tag = source.AsSpan(index + 1, end - index - 1);
                if (tag.StartsWith("/"))
                {
                    var name = InlineTagName(tag[1..], webVtt);
                    if (stack.Count == 0 || stack.Peek().Tag != name) return false;
                    var previous = stack.Pop();
                    ChangeStyle(previous.Previous, previous.Foreground, previous.Background, previous.Language);
                }
                else
                {
                    if (stack.Count >= 16 || !TryOpenInlineTag(tag, webVtt, out var name, out var style, out var color, out var fill, out var culture)) return false;
                    // Only a voice spanning the complete cue may omit its end tag.
                    stack.Push((name, current, foreground, background, language, name == 'v' && index == 0));
                    ChangeStyle(current | style, color ?? foreground, fill ?? background, culture ?? language);
                }
                index = end;
            }
            else if (character == '>') return false;
            else if (character == '&')
            {
                var end = source.IndexOf(';', index + 1);
                var entity = end >= 0 && end - index <= 10 ? source[(index + 1)..end] : string.Empty;
                var decoded = entity switch
                {
                    "amp" => "&", "lt" => "<", "gt" => ">", "nbsp" => "\u00A0",
                    "lrm" when webVtt => "\u200E", "rlm" when webVtt => "\u200F",
                    "quot" when !webVtt => "\"", "apos" when !webVtt => "'", _ => null
                };
                if (decoded is not null)
                {
                    output.Append(decoded); // Decode once; escaped tags stay literal.
                    index = end;
                }
                else
                {
                    // Unescaped '&' is ordinary SubRip dialogue. An entity-shaped
                    // token or any unsupported WebVTT escape retains compatibility.
                    if (webVtt || (index + 1 < source.Length && source[index + 1] == '#') ||
                        (end > index && !source.AsSpan(index + 1, end - index - 1).ContainsAny(' ', '\n', '\t'))) return false;
                    output.Append(character);
                }
            }
            else output.Append(character);
            if (styles.Count > 256) return false;
        }
        if (stack.Count != 0 && !(stack.Count == 1 && stack.Peek() is { Tag: 'v', ImplicitVoiceEnd: true })) return false;
        ChangeStyle(SubtitleStyle.None, null, null, null);
        if (styles.Count > 256 || output.Length == 0) return false;
        text = output.ToString();
        foreach (var range in styles)
        {
            bool SplitsPair(int position) => position > 0 && position < output.Length &&
                char.IsHighSurrogate(output[position - 1]) && char.IsLowSurrogate(output[position]);
            if (SplitsPair(range.Start) || SplitsPair(range.Start + range.Length)) return false;
        }
        ranges = styles.ToArray();
        return true;
    }

    private static char InlineTagName(ReadOnlySpan<char> tag, bool webVtt) => tag switch
    {
        "b" => 'b', "i" => 'i', "u" => 'u', "font" when !webVtt => 'f',
        "c" when webVtt => 'c', "v" when webVtt => 'v', "lang" when webVtt => 'l', _ => '\0'
    };

    private static bool TryOpenInlineTag(ReadOnlySpan<char> tag, bool webVtt, out char name, out SubtitleStyle style,
        out uint? color, out uint? background, out string? language)
    {
        name = InlineTagName(tag, webVtt);
        style = name switch { 'b' => SubtitleStyle.Bold, 'i' => SubtitleStyle.Italic, 'u' => SubtitleStyle.Underline, _ => SubtitleStyle.None };
        color = null;
        background = null;
        language = null;
        if (webVtt)
        {
            var annotationStart = tag.IndexOfAny(' ', '\t');
            var component = annotationStart < 0 ? tag : tag[..annotationStart];
            var classes = component.IndexOf('.');
            name = InlineTagName(classes < 0 ? component : component[..classes], webVtt: true);
            if (name == '\0') return false;
            style = name switch { 'b' => SubtitleStyle.Bold, 'i' => SubtitleStyle.Italic, 'u' => SubtitleStyle.Underline, _ => SubtitleStyle.None };
            if (name == 'v')
            {
                // Speaker names are annotations, not visible dialogue. CSS that
                // could style them is rejected by the file parser before this call.
                if (annotationStart < 0 || !ValidVoiceAnnotation(tag[(annotationStart + 1)..])) return false;
            }
            else if (name == 'l')
            {
                if (annotationStart < 0 || !TrySubtitleLanguage(tag[(annotationStart + 1)..], out language)) return false;
            }
            else if (annotationStart >= 0) return false;
            return classes < 0 || TryWebVttClasses(component[(classes + 1)..], out color, out background);
        }
        if (style != SubtitleStyle.None) return true;
        if (!webVtt && tag.Length > 5 && tag.StartsWith("font") && tag[4] is ' ' or '\t')
        {
            var attribute = tag[4..].Trim(" \t");
            if (!attribute.StartsWith("color")) return false;
            attribute = attribute[5..].TrimStart(" \t");
            if (attribute.Length == 0 || attribute[0] != '=') return false;
            var value = attribute[1..].Trim(" \t");
            if (value.Length > 1 && value[0] is '\'' or '"')
            {
                if (value[^1] != value[0]) return false;
                value = value[1..^1];
            }
            if (!TrySubRipColor(value, out var subRipColor)) return false;
            name = 'f';
            color = subRipColor;
            return true;
        }
        return false;
    }

    private static bool ValidVoiceAnnotation(ReadOnlySpan<char> annotation)
    {
        annotation = annotation.Trim(" \t");
        if (annotation.Length is < 1 or > 256 || annotation.ContainsAny('<', '\n', '\r')) return false;
        for (var index = 0; index < annotation.Length; index++)
        {
            if (annotation[index] != '&') continue;
            var end = annotation[index..].IndexOf(';');
            if (end < 0 || annotation.Slice(index + 1, end - 1) is not ("amp" or "lt" or "gt" or "nbsp" or "lrm" or "rlm")) return false;
            index += end;
        }
        return true;
    }

    // Admit known Windows/.NET cultures, including neutral, script and region
    // names. Do not silently replace an unknown BCP 47 tag with another culture.
    private static readonly Lazy<Dictionary<string, string>> SubtitleLanguages = new(() =>
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
            if (culture.Name.Length > 0 && !culture.Name.Contains('_')) result.TryAdd(culture.Name, culture.Name);
        // ICU enumerates these documented Chinese aliases under their script
        // names, although existing captions commonly use the regional names.
        if (result.ContainsKey("zh-Hans-CN")) result.TryAdd("zh-CN", "zh-Hans-CN");
        if (result.ContainsKey("zh-Hant-TW")) result.TryAdd("zh-TW", "zh-Hant-TW");
        return result;
    });

    private static bool TrySubtitleLanguage(ReadOnlySpan<char> annotation, out string? language)
    {
        language = null;
        annotation = annotation.Trim(" \t");
        if (annotation.Length is < 2 or > 85 || annotation.ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-")) return false;
        var primaryEnd = annotation.IndexOf('-');
        var primary = primaryEnd < 0 ? annotation : annotation[..primaryEnd];
        if (primary.Length is < 2 or > 3 || primary.ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ")) return false;
        return SubtitleLanguages.Value.TryGetValue(annotation.ToString(), out language);
    }

    // Admit an unambiguous standard foreground/background pair in either order.
    // Custom classes and competing colors retain VLC's complete cascade handling.
    private static bool TryWebVttClasses(ReadOnlySpan<char> classes, out uint? foreground, out uint? background)
    {
        foreground = background = null;
        for (var count = 0; count < 2 && !classes.IsEmpty; count++)
        {
            var separator = classes.IndexOf('.');
            var component = separator < 0 ? classes : classes[..separator];
            if (component.StartsWith("bg_"))
            {
                if (background is not null || !TryWebVttColor(component[3..], out var color)) return false;
                background = color;
            }
            else
            {
                if (foreground is not null || !TryWebVttColor(component, out var color)) return false;
                foreground = color;
            }
            if (separator < 0) return true;
            classes = classes[(separator + 1)..];
        }
        return false;
    }

    // WebVTT's eight standard color names are case-sensitive.
    private static bool TryWebVttColor(ReadOnlySpan<char> name, out uint color)
    {
        uint? value = name switch
        {
            "white" => 0xFFFFFF, "lime" => 0x00FF00, "cyan" => 0x00FFFF, "red" => 0xFF0000,
            "yellow" => 0xFFFF00, "magenta" => 0xFF00FF, "blue" => 0x0000FF, "black" => 0x000000, _ => null
        };
        color = value ?? 0;
        return value.HasValue;
    }

    private static bool TrySubRipColor(ReadOnlySpan<char> name, out uint color)
    {
        color = 0;
        if (name.Length == 7 && name[0] == '#')
            return uint.TryParse(name[1..], System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out color);
        // HTML's basic opaque named colors, with common equivalent names.
        uint? value = name.ToString().ToLowerInvariant() switch
        {
            "black" => 0x000000, "silver" => 0xC0C0C0, "gray" or "grey" => 0x808080, "white" => 0xFFFFFF,
            "maroon" => 0x800000, "red" => 0xFF0000, "purple" => 0x800080, "fuchsia" or "magenta" => 0xFF00FF,
            "green" => 0x008000, "lime" => 0x00FF00, "olive" => 0x808000, "yellow" => 0xFFFF00,
            "navy" => 0x000080, "blue" => 0x0000FF, "teal" => 0x008080, "aqua" or "cyan" => 0x00FFFF, "orange" => 0xFFA500, _ => null
        };
        color = value ?? 0;
        return value.HasValue;
    }
}
