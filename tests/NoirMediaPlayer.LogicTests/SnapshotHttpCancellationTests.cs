using System.Net;
using System.Net.Sockets;
using System.Text;
using NoirMediaPlayer.Services;
using Xunit;

namespace NoirMediaPlayer.LogicTests;

public sealed class SnapshotHttpCancellationTests
{
    [Fact]
    public async Task CallerCancellation_InterruptsStalledHttpBodyBeforeNetworkDeadline()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource();
        var source = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/test.mp4";
        var opening = Task.Run(() => new WindowsSnapshotHttpStream(source, cancellationToken: cancellation.Token));
        try
        {
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            using var stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            // A valid range header plus one byte, then no more body. One tiny
            // loopback request exercises cancellation without decoding any media.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 206 Partial Content\r\nContent-Length: 262144\r\nContent-Range: bytes 0-262143/262144\r\nConnection: close\r\n\r\nx"), deadline.Token);
            await Task.Delay(50, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                using var input = await opening.WaitAsync(TimeSpan.FromSeconds(2));
            });
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { using var input = await opening.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
        }
    }
}
