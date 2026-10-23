namespace NoirMediaPlayer.Services;

internal static class PlaybackRestorationPolicy
{
    internal static bool Failed(Task? audio, Task? subtitles) =>
        audio is { IsFaulted: true } or { IsCanceled: true } ||
        subtitles is { IsFaulted: true } or { IsCanceled: true };

    internal static bool IsReady(PlaybackState state, bool pausePending, bool positionPending,
        bool audioPending, bool subtitlesPending, Task? audio, Task? subtitles) =>
        state is PlaybackState.Playing or PlaybackState.Paused &&
        !pausePending && !positionPending && !audioPending && !subtitlesPending &&
        !Failed(audio, subtitles) && (audio?.IsCompleted ?? true) && (subtitles?.IsCompleted ?? true);
}

internal sealed class PlaybackRestorationDeadline(TimeSpan timeout, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly long _started = (timeProvider ?? TimeProvider.System).GetTimestamp();

    internal TimeSpan Remaining
    {
        get
        {
            var remaining = timeout - _clock.GetElapsedTime(_started);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    internal void ThrowIfExpired(string message)
    {
        if (Remaining == TimeSpan.Zero) throw new TimeoutException(message);
    }
}
