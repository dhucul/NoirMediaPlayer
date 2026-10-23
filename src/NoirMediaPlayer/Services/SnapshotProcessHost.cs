using System.IO;

namespace NoirMediaPlayer.Services;

internal static class SnapshotProcessHost
{
    internal static int Run(Action<SourceSnapshotRequest, string> decode)
    {
        // Last resort if the parent disappears or cannot kill us. This also
        // bounds a stalled handshake, native teardown, and uncooperative codecs.
        using var deadline = new Timer(_ => Environment.Exit(3), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        try
        {
            // Leave the pipe open until process exit. Disposing it on success
            // would race the EOF watchdog and turn a valid capture into a failure.
            var input = Console.OpenStandardInput();
            var message = SnapshotProtocol.Read(input);
            _ = Task.Run(() =>
            {
                try { input.ReadByte(); }
                catch (Exception) { }
                // EOF, broken pipe or unexpected extra input: parent is gone or
                // the one-request protocol is violated. Terminate even in native code.
                Environment.Exit(3);
            });
            decode(message.ToRequest(), message.Destination);
            return 0;
        }
        catch (Exception) { return 2; }
    }
}
