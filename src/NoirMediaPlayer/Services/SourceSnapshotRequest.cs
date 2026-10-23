namespace NoirMediaPlayer.Services;

// Managed values only; safe to decode after the live player has been disposed.
internal sealed record SourceSnapshotRequest(string Source, long Time, string? ContentType = null,
    PlainSubtitleTrack? Subtitles = null, int Rotation = 0, SubtitleCue? EmbeddedCue = null);
