using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NoirMediaPlayer.Tests;

public enum LoopbackResponse
{
    Normal, IgnoreRanges, NotFound, WrongRange, Truncated, Compressed, ChangedEntity,
    ChangedLength, UnknownLength, ExcessChunked, StallBody, StallHeaders, Redirect
}

// First-party test fixture: deterministic HTTP media and byte ranges, with no external service.
internal sealed class LoopbackMediaServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly byte[] _media;
    private readonly string _contentType;
    private readonly List<Task> _clients = [];
    private int _requests;
    private int _rangeRequests;
    private long _bytesServed;
    private int _response;
    internal int Requests => Volatile.Read(ref _requests);
    internal int RangeRequests => Volatile.Read(ref _rangeRequests);
    internal long BytesServed => Interlocked.Read(ref _bytesServed);
    internal LoopbackResponse Response { get => (LoopbackResponse)Volatile.Read(ref _response); set => Volatile.Write(ref _response, (int)value); }
    internal bool ResetResponseAfterRequest { get; set; }
    internal string? LastIfRange { get; private set; }
    internal string Url { get; }

    internal LoopbackMediaServer(byte[] media, string extension, string contentType)
    {
        _media = media;
        _contentType = contentType;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/media{extension}";
        _serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(ServeClientAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        // Stop can dispose the listener between the loop condition and Accept.
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var request = await reader.ReadLineAsync(_stop.Token);
                if (request is null) return;
                Interlocked.Increment(ref _requests);
                var response = Response;
                if (ResetResponseAfterRequest) Response = LoopbackResponse.Normal;
                long start = 0, end = _media.Length - 1;
                var partial = false;
                for (var count = 0; count < 100; count++)
                {
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (string.IsNullOrEmpty(line)) break;
                    if (line.StartsWith("If-Range: ", StringComparison.OrdinalIgnoreCase)) LastIfRange = line[10..];
                    if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                    var range = line[13..].Split('-');
                    partial = long.TryParse(range[0], out start);
                    if (range.Length > 1 && long.TryParse(range[1], out var requestedEnd)) end = Math.Min(end, requestedEnd);
                }
                if (partial) Interlocked.Increment(ref _rangeRequests);
                if (response == LoopbackResponse.StallHeaders)
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                if (response == LoopbackResponse.NotFound)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    return;
                }
                if (response == LoopbackResponse.Redirect && !request.Contains("/redirected", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /redirected.mp4\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stop.Token);
                    return;
                }
                if (response == LoopbackResponse.IgnoreRanges) { partial = false; start = 0; end = _media.Length - 1; }
                start = Math.Clamp(start, 0, _media.Length - 1);
                end = Math.Clamp(end, start, _media.Length - 1);
                var length = (int)(end - start + 1);
                var headers = $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {_contentType}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n";
                headers += response == LoopbackResponse.ExcessChunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {length}\r\n";
                headers += $"ETag: \"{(response == LoopbackResponse.ChangedEntity ? "changed" : "noir-test")}\"\r\n";
                if (response == LoopbackResponse.Compressed) headers += "Content-Encoding: gzip\r\n";
                if (partial)
                {
                    var total = response == LoopbackResponse.UnknownLength ? "*" : (_media.Length + (response == LoopbackResponse.ChangedLength ? 1 : 0)).ToString();
                    headers += $"Content-Range: bytes {start + (response == LoopbackResponse.WrongRange ? 1 : 0)}-{end}/{total}\r\n";
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "\r\n"), _stop.Token);
                if (response == LoopbackResponse.StallBody) await Task.Delay(Timeout.Infinite, _stop.Token);
                if (!request.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    if (response == LoopbackResponse.ExcessChunked)
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{length + 1:X}\r\n"), _stop.Token);
                    var sent = response == LoopbackResponse.Truncated ? length - 1 : length;
                    await stream.WriteAsync(_media.AsMemory((int)start, sent), _stop.Token);
                    Interlocked.Add(ref _bytesServed, sent);
                    if (response == LoopbackResponse.ExcessChunked)
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("X\r\n0\r\n\r\n"), _stop.Token);
                }
            }
            catch (IOException) { } // Media engines may abandon a prefetch connection.
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _serving.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(_clients).WaitAsync(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }
}
