namespace NoirMediaPlayer.Services;

internal sealed record PlaybackSessionChoices<TSubtitles>(long Position, bool Paused, int? AudioOrdinal, TSubtitles? Subtitles)
    where TSubtitles : class
{
    internal static PlaybackSessionChoices<TSubtitles> Capture(long position, bool paused,
        IReadOnlyList<int> audioIds, int selectedAudioId, TSubtitles? subtitles) =>
        new(Math.Max(0, position), paused,
            audioIds.Count == 0 && selectedAudioId < 0 ? null : PlaybackTrackSelection.Ordinal(audioIds, selectedAudioId), subtitles);
}

internal static class PlaybackTrackSelection
{
    internal static int Ordinal(IReadOnlyList<int> ids, int selectedId)
    {
        if (selectedId < 0) return -1;
        for (var index = 0; index < ids.Count; index++)
            if (ids[index] == selectedId) return index;
        throw new InvalidOperationException("The selected track is not present in the current inventory.");
    }
}

// Keep the current item's replay choice independently of disposable decoder resources.
// A different item drops the single retained inventory rather than caching every source.
internal sealed class PlaybackReplayMemory<T> where T : class
{
    private object? _item;
    private T? _choice;

    internal void Remember(object item, T? choice) { _item = item; _choice = choice; }
    internal T? For(object item) => ReferenceEquals(item, _item) ? _choice : null;
    internal void ClearIfDifferent(object item) { if (!ReferenceEquals(item, _item)) Clear(); }
    internal void Clear() { _item = null; _choice = null; }
}
