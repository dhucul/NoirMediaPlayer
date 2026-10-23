using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace NoirMediaPlayer.Services;

internal static class SnapshotProcess
{
    internal const string WorkerArgument = "--noir-snapshot-worker";

    private static ProcessStartInfo CreateStartInfo()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No application executable.");
        var start = new ProcessStartInfo(executable);
        // Also support launching the framework-dependent DLL with dotnet.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        start.ArgumentList.Add(WorkerArgument);
        return start;
    }

    internal static async Task CaptureAsync(SourceSnapshotRequest request, string staging,
        CancellationToken cancellationToken, Func<ProcessStartInfo>? startInfo = null)
    {
        var payload = SnapshotProtocol.Encode(request, staging);
        cancellationToken.ThrowIfCancellationRequested();
        var start = (startInfo ?? CreateStartInfo)();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardInput = true;
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start snapshot decoder.");
        Task? exchange = null;
        try
        {
            // No paths, URLs or captions in command-line arguments. Keep stdin
            // open: its EOF is the child's parent-lifetime signal.
            // Anonymous-pipe writes may not honor cancellation after starting.
            // Bound the entire exchange so even a child that never reads stdin
            // reaches the independent kill/reap path below.
            exchange = SendAndWaitAsync(process, payload, cancellationToken);
            await exchange.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new IOException($"Snapshot decoder exited with code {process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) { Debug.WriteLine(exception); }
                // EOF gives the child's independent watchdog another way to exit.
                try { process.StandardInput.Close(); }
                catch (Exception exception) { Debug.WriteLine(exception); }
                // Do not dispose the handle or release the single slot early.
                // SnapshotWorker bounds the caller's wait independently.
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            // Killing closes the child's pipe end and unblocks any pending write.
            // Observe it before disposing the process/pipe handles.
            if (exchange is not null)
            {
                try { await exchange.ConfigureAwait(false); }
                catch (Exception exception) { Debug.WriteLine(exception); }
            }
        }
    }

    private static async Task SendAndWaitAsync(Process process, byte[] payload, CancellationToken cancellationToken)
    {
        await process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }
}
