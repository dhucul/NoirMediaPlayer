namespace NoirMediaPlayer.Services;

/// <summary>Simple chapter markers on the file's playback timeline, in milliseconds.</summary>
internal sealed class NativeChapterMap(long[] starts, bool firstChapterBeforeStart = false)
{
    internal IReadOnlyList<long> Starts { get; } = Array.AsReadOnly((long[])starts.Clone());

    internal long? Navigate(long time, bool next)
    {
        // MP4's first chapter is current before its first marker; Matroska can
        // have an unchaptered lead-in. Keep each container's navigation behavior.
        var current = firstChapterBeforeStart && Starts.Count > 0 ? 0 : -1;
        for (var index = 0; index < Starts.Count && Starts[index] <= time; index++) current = index;
        var target = current + (next ? 1 : -1);
        return target >= 0 && target < Starts.Count ? Starts[target] : null;
    }
}
