namespace NoirMediaPlayer.Services;

/// <summary>Windows paths ignore case; normalized network paths and credentials do not.</summary>
public sealed class MediaSourceComparer : IEqualityComparer<string>
{
    public static MediaSourceComparer Instance { get; } = new();

    public bool Equals(string? x, string? y)
    {
        if (x is null || y is null) return x == y;
        var xNetwork = MediaSourceService.TryNormalizeNetworkLocation(x, out var normalizedX);
        var yNetwork = MediaSourceService.TryNormalizeNetworkLocation(y, out var normalizedY);
        return xNetwork || yNetwork
            ? xNetwork && yNetwork && StringComparer.Ordinal.Equals(normalizedX, normalizedY)
            : StringComparer.OrdinalIgnoreCase.Equals(x, y);
    }

    public int GetHashCode(string source) =>
        MediaSourceService.TryNormalizeNetworkLocation(source, out var normalized)
            ? StringComparer.Ordinal.GetHashCode(normalized)
            : StringComparer.OrdinalIgnoreCase.GetHashCode(source);
}
