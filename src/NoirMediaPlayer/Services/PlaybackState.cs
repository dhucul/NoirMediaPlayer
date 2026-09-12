namespace NoirMediaPlayer.Services;

internal enum PlaybackState
{
    Stopped,
    Opening,
    Playing,
    Paused,
    Ended,
    Error,
    Closing
}

internal static class PlaybackResumePolicy
{
    public static long? Resolve(long position, long length, bool explicitPosition)
    {
        if (length <= 0 || position < 0) return null;
        if (explicitPosition) return Math.Clamp(position, 0, Math.Max(0, length - 1));
        return position >= 10_000 && position < length - 10_000 ? position : null;
    }
}
