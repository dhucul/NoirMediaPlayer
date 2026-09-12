namespace NoirMediaPlayer.Services;

internal sealed class ShuffleCycle
{
    private readonly HashSet<string> _played = new(MediaSourceComparer.Instance);

    public void Reset() => _played.Clear();
    public void MarkPlayed(string source) => _played.Add(source);

    public int Next(IReadOnlyList<string> sources, int currentIndex, bool restartCycle, Random random)
    {
        var remaining = Enumerable.Range(0, sources.Count)
            .Where(index => !_played.Contains(sources[index])).ToArray();
        if (remaining.Length == 0 && restartCycle)
        {
            Reset();
            if (currentIndex >= 0 && currentIndex < sources.Count && sources.Count > 1)
                MarkPlayed(sources[currentIndex]);
            remaining = Enumerable.Range(0, sources.Count)
                .Where(index => !_played.Contains(sources[index])).ToArray();
        }
        return remaining.Length == 0 ? -1 : remaining[random.Next(remaining.Length)];
    }
}
