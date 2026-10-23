using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NoirMediaPlayer.Services;

// A read-only, seekable input for Media Foundation snapshots and chapter metadata. HTTP
// reads have one shared deadline and bounded memory/traffic; no media file is saved.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class WindowsSnapshotHttpStream : IStream, IDisposable
{
    private const int BlockSize = 256 * 1024;
    private readonly object _sync = new();
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationToken _token;
    private readonly Uri _source;
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly Queue<long> _cacheOrder = [];
    private readonly long _byteLimit;
    private long _downloaded;
    private int _requests;
    private long _position;
    private int _disposed;
    private string? _entityTag;
    private DateTimeOffset? _lastModified;
    private string? _resolvedUrl;
    internal long Length { get; private set; }
    internal string ContentType { get; private set; } = "application/octet-stream";

    internal WindowsSnapshotHttpStream(string source, TimeSpan? timeout = null, long byteLimit = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new NotSupportedException("Snapshot input requires a direct HTTP/HTTPS URL without explicit credentials.");
        if (byteLimit < BlockSize) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        _source = uri;
        _byteLimit = byteLimit;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));
        _token = _deadline.Token;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxAutomaticRedirections = 5,
            Credentials = null,
            UseCookies = false
        }) { Timeout = Timeout.InfiniteTimeSpan };
        try { Cache(0, DownloadBlock(0, initial: true).GetAwaiter().GetResult()); }
        catch { Dispose(); throw; }
    }

    private async Task<byte[]> DownloadBlock(long start, bool initial = false)
    {
        _token.ThrowIfCancellationRequested();
        var requested = initial ? BlockSize : Math.Min(BlockSize, Length - start);
        if (_downloaded > _byteLimit - requested) throw new NotSupportedException("Snapshot HTTP byte limit exceeded.");
        if (++_requests > 256) throw new NotSupportedException("Snapshot HTTP request limit exceeded.");
        var end = initial ? BlockSize - 1L : start + Math.Min(BlockSize, Length - start) - 1;
        using var request = new HttpRequestMessage(HttpMethod.Get, _source);
        request.Headers.Range = new RangeHeaderValue(start, end);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (!initial)
        {
            if (_entityTag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_entityTag));
            else if (_lastModified is { } modified) request.Headers.IfRange = new RangeConditionHeaderValue(modified);
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _token).ConfigureAwait(false);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
            range.From != start || range.Length is not > 0 || range.To is null ||
            range.To != Math.Min(end, range.Length.Value - 1) ||
            response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Snapshot input requires consistent finite HTTP byte ranges.");
        var redirected = response.RequestMessage?.RequestUri;
        if (redirected is null || redirected.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(redirected.UserInfo))
            throw new NotSupportedException("The snapshot HTTP redirect is unsupported.");
        var tag = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
        var lastModified = response.Content.Headers.LastModified;
        if (initial)
        {
            Length = range.Length.Value;
            _entityTag = tag;
            _lastModified = lastModified;
            _resolvedUrl = redirected.AbsoluteUri;
            ContentType = response.Content.Headers.ContentType?.MediaType ?? ContentType;
        }
        else if (_resolvedUrl != redirected.AbsoluteUri || Length != range.Length || (_entityTag is not null && _entityTag != tag) ||
            (_entityTag is null && _lastModified is not null && _lastModified != lastModified))
            throw new IOException("The snapshot source changed during capture.");
        var length = checked((int)(range.To.Value - start + 1));
        if (response.Content.Headers.ContentLength is { } declared && declared != length)
            throw new IOException("The snapshot HTTP range length is inconsistent.");
        if (_downloaded > _byteLimit - length) throw new NotSupportedException("Snapshot HTTP byte limit exceeded.");
        _downloaded += length;
        var bytes = new byte[length];
        using var body = await response.Content.ReadAsStreamAsync(_token).ConfigureAwait(false);
        await body.ReadExactlyAsync(bytes, _token).ConfigureAwait(false);
        // Do not accept a chunked response that sends more than its declared range.
        var extra = new byte[1];
        if (await body.ReadAsync(extra, _token).ConfigureAwait(false) != 0)
            throw new IOException("The snapshot HTTP range contains excess data.");
        return bytes;
    }

    private void Cache(long start, byte[] bytes)
    {
        if (_cache.Count == 4) _cache.Remove(_cacheOrder.Dequeue());
        _cache.Add(start, bytes);
        _cacheOrder.Enqueue(start);
    }

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        if (pcbRead != 0) Marshal.WriteInt32(pcbRead, 0);
        if (cb < 0 || cb > pv.Length) throw new ArgumentOutOfRangeException(nameof(cb));
        var copied = Read(pv.AsSpan(0, cb));
        if (pcbRead != 0) Marshal.WriteInt32(pcbRead, copied);
    }

    private int Read(Span<byte> buffer)
    {
        lock (_sync)
        {
            EnsureAvailable();
            var copied = 0;
            while (copied < buffer.Length && _position < Length)
            {
                _token.ThrowIfCancellationRequested();
                var start = _position / BlockSize * BlockSize;
                if (!_cache.TryGetValue(start, out var bytes))
                {
                    bytes = DownloadBlock(start).GetAwaiter().GetResult();
                    Cache(start, bytes);
                }
                var offset = (int)(_position - start);
                var count = Math.Min(buffer.Length - copied, bytes.Length - offset);
                bytes.AsSpan(offset, count).CopyTo(buffer[copied..]);
                copied += count;
                _position += count;
            }
            return copied;
        }
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        var position = MovePosition(dlibMove, dwOrigin);
        if (plibNewPosition != 0) Marshal.WriteInt64(plibNewPosition, position);
    }

    private long MovePosition(long offset, int from)
    {
        lock (_sync)
        {
            EnsureAvailable();
            var origin = from switch { 0 => 0L, 1 => _position, 2 => Length, _ => throw new ArgumentOutOfRangeException(nameof(from)) };
            var target = checked(origin + offset);
            if (target < 0) throw new IOException("A snapshot stream cannot seek before its start.");
            _position = target;
            return _position;
        }
    }

    // The adapter shares this input's position, cache, deadline and byte budget.
    // Its owner retains responsibility for disposing the HTTP input.
    internal Stream AsReadStream() => new ReadStream(this);

    private sealed class ReadStream(WindowsSnapshotHttpStream input) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.MovePosition(0, 1); set => input.MovePosition(value, 0); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => input.Read(buffer);
        public override int ReadByte()
        {
            Span<byte> value = stackalloc byte[1];
            return input.Read(value) == 0 ? -1 : value[0];
        }
        public override long Seek(long offset, SeekOrigin origin) => input.MovePosition(offset, (int)origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        lock (_sync)
        {
            EnsureAvailable();
            pstatstg = new STATSTG { type = 2, cbSize = Length, grfMode = 0 };
        }
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _token.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _deadline.Cancel();
        lock (_sync)
        {
            _client.Dispose();
            _cache.Clear();
            _cacheOrder.Clear();
            _deadline.Dispose();
        }
    }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new NotSupportedException();
    public void SetSize(long libNewSize) => throw new NotSupportedException();
    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten) => throw new NotSupportedException();
    public void Commit(int grfCommitFlags) { }
    public void Revert() => throw new NotSupportedException();
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotSupportedException();
    public void Clone(out IStream ppstm) => throw new NotSupportedException();
}
