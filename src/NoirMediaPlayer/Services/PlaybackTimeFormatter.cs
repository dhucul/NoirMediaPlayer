using System.Globalization;

namespace NoirMediaPlayer.Services;

internal static class PlaybackTimeFormatter
{
    internal static string Format(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds) / 1000;
        return seconds >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
