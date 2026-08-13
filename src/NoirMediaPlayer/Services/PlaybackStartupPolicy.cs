namespace NoirMediaPlayer.Services;

public sealed record PlaybackStartupFailure(
    string EngineStatus,
    string StatusText,
    string Notice,
    string DialogTitle,
    string DialogMessage);

public static class PlaybackStartupPolicy
{
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DiscTimeout = TimeSpan.FromSeconds(35);

    public static TimeSpan? GetTimeout(bool isNetwork, bool isDisc)
    {
        if (isNetwork)
        {
            return NetworkTimeout;
        }

        return isDisc ? DiscTimeout : null;
    }

    public static PlaybackStartupFailure DescribeDiscFailure(
        bool isAacsProtected,
        bool keyDatabaseFound,
        bool timedOut)
    {
        if (isAacsProtected)
        {
            var reason = timedOut
                ? $"Playback remained at 0% for {DiscTimeout.TotalSeconds:0} seconds."
                : "The playback engine rejected the protected disc.";
            var credentials = keyDatabaseFound
                ? "KEYDB.cfg was found, but it may not contain a matching key for this disc."
                : "No playback credentials were found.";

            return new PlaybackStartupFailure(
                "AACS UNLOCK FAILED",
                "This Blu-ray could not be unlocked",
                "AACS could not unlock this Blu-ray",
                "Protected Blu-ray could not be played",
                $"{reason}\n\n{credentials}\n\nUpdate your lawfully obtained KEYDB.cfg or play the disc with licensed Blu-ray playback software.");
        }

        return new PlaybackStartupFailure(
            timedOut ? "DISC TIMEOUT" : "DISC ERROR",
            timedOut ? "The disc did not start in time" : "The disc could not be played",
            timedOut ? "Disc startup timed out" : "Disc playback failed",
            "Disc playback failed",
            timedOut
                ? $"The disc did not start within {DiscTimeout.TotalSeconds:0} seconds. Check the disc and optical drive, then try again."
                : "The playback engine could not open this disc. Check the disc and optical drive, then try again.");
    }
}
