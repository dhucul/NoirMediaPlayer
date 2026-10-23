using System.Diagnostics;
using System.IO;

namespace NoirMediaPlayer.Services;

// One process per capture. Keep the slot until the child has actually exited,
// including in the exceptional case where the OS cannot immediately terminate it.
internal sealed class SnapshotWorker(TimeSpan? timeout = null, TimeProvider? clock = null,
    Func<ProcessStartInfo>? startInfo = null)
{
    private readonly object _sync = new();
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Task _active = Task.CompletedTask;

    internal bool IsBusy { get { lock (_sync) return !_active.IsCompleted; } }

    internal async Task<bool> TryCaptureAsync(SourceSnapshotRequest request, string destination,
        CancellationToken cancellationToken = default)
    {
        Task work;
        CancellationToken token;
        lock (_sync)
        {
            if (!_active.IsCompleted) return false;
            cancellationToken.ThrowIfCancellationRequested();
            var deadline = new CancellationTokenSource(_timeout, _clock);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            token = cancellation.Token;
            work = _active = Task.Run(async () =>
            {
                string? staging = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(destination);
                    var folder = Path.GetDirectoryName(fullPath)!;
                    Directory.CreateDirectory(folder);
                    staging = Path.Combine(folder, $".noir-snapshot-{Guid.NewGuid():N}.tmp");
                    await SnapshotProcess.CaptureAsync(request, staging, token, startInfo).ConfigureAwait(false);
                    // Only a successful, reaped child can publish. Never overwrite
                    // an existing capture, or publish canceled/partial decoder output.
                    token.ThrowIfCancellationRequested();
                    File.Move(staging, fullPath);
                    if (token.IsCancellationRequested)
                    {
                        File.Delete(fullPath);
                        token.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    try { if (staging is not null) File.Delete(staging); }
                    catch (Exception exception) { Debug.WriteLine(exception); }
                    cancellation.Dispose();
                    deadline.Dispose();
                }
            });
        }
        _ = ObserveAsync(work);
        try
        {
            await work.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Allow a short, bounded reap before accepting another capture. If
            // termination fails, the cleanup task retains the slot until exit.
            try { await work.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) { Debug.WriteLine(exception); }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Snapshot capture timed out. Playback remains available.");
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }
}
