using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ClrVoyant.Dap;

namespace ClrVoyant.Tests.Fakes;

/// <summary>
/// A scripted Debug Adapter on the far side of a <see cref="DapClient"/>, over
/// in-memory streams (no process, no pipes, nothing OS-specific). The test decides
/// exactly which bytes arrive and in which chunks, so framing edge cases are
/// deterministic. Every wait is bounded.
/// </summary>
public sealed class FakeAdapter : IDisposable
{
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    readonly ChunkStream _toClient = new();
    readonly ChunkStream _stderr = new();
    readonly RequestSink _fromClient = new();
    readonly CancellationTokenSource _stop = new();

    public DapClient Client { get; }

    /// <summary>Every request the client has sent, in order.</summary>
    public IReadOnlyList<JsonElement> Received => _fromClient.Received;

    public FakeAdapter(bool withStderr = false)
    {
        Client = new DapClient(_toClient, _fromClient, withStderr ? _stderr : null);
    }

    // ---- adapter -> client ----

    /// <summary>One complete frame for <paramref name="message"/>.</summary>
    public static byte[] Frame(object message) => FrameBytes(JsonSerializer.SerializeToUtf8Bytes(message));

    public static byte[] FrameBytes(byte[] body)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        return [.. header, .. body];
    }

    /// <summary>Deliver bytes to the client as a single read.</summary>
    public void PushRaw(byte[] bytes) => _toClient.Push(bytes);

    /// <summary>Deliver bytes one per read: every possible split point at once.</summary>
    public void PushByteByByte(byte[] bytes)
    {
        foreach (var b in bytes) _toClient.Push([b]);
    }

    public void Respond(JsonElement request, object? body = null)
        => PushRaw(Frame(new
        {
            seq = 0,
            type = "response",
            request_seq = request.GetProperty("seq").GetInt32(),
            success = true,
            command = request.GetProperty("command").GetString(),
            body = body ?? new { },
        }));

    public void Reject(JsonElement request, string message)
        => PushRaw(Frame(new
        {
            seq = 0,
            type = "response",
            request_seq = request.GetProperty("seq").GetInt32(),
            success = false,
            command = request.GetProperty("command").GetString(),
            message,
        }));

    public void Event(string name, object? body = null)
        => PushRaw(Frame(new { seq = 0, type = "event", @event = name, body = body ?? new { } }));

    public void WriteStderr(string text) => _stderr.Push(Encoding.UTF8.GetBytes(text));

    /// <summary>The adapter closes its stdout (and stderr): a clean end of stream.</summary>
    public void Eof()
    {
        _toClient.Complete();
        _stderr.Complete();
    }

    /// <summary>Make the next read from the adapter fail with <paramref name="error"/>.</summary>
    public void FailReads(Exception error) => _toClient.Fail(error);

    // ---- client -> adapter ----

    public Task<JsonElement> NextRequestAsync() => _fromClient.NextAsync().AsTask().WaitAsync(Bound);

    /// <summary>The first received request with this command, or null.</summary>
    public JsonElement? Find(string command)
    {
        foreach (var r in Received)
            if (r.GetProperty("command").GetString() == command) return r;
        return null;
    }

    /// <summary>Make client writes throw (a broken pipe).</summary>
    public void BreakWrites() => _fromClient.Break();

    /// <summary>Make client writes block forever (an adapter that stopped reading).</summary>
    public void StallWrites() => _fromClient.Stall();

    /// <summary>
    /// Answer requests as they arrive on a background loop until disposed. The handler
    /// runs once per request and replies through <see cref="Respond"/>/<see cref="Reject"/>
    /// /<see cref="Event"/> (or deliberately does not). Calling this again replaces the
    /// handler (there is only ever one loop reading requests).
    /// </summary>
    public void Script(Action<JsonElement, FakeAdapter> handler)
    {
        Volatile.Write(ref _handler, handler);
        if (Interlocked.Exchange(ref _scriptStarted, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var request = await _fromClient.NextAsync(_stop.Token);
                    Volatile.Read(ref _handler)!(request, this); // read AFTER the wait: the handler may have been replaced
                }
            }
            catch (OperationCanceledException) { /* disposed */ }
        });
    }

    Action<JsonElement, FakeAdapter>? _handler;
    int _scriptStarted;

    public void Dispose()
    {
        _stop.Cancel();
        Client.Dispose();
    }

    /// <summary>Serves a fixed set of chunks, one chunk per read, then waits for more.</summary>
    sealed class ChunkStream : Stream
    {
        readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        byte[]? _current;
        int _offset;

        public void Push(byte[] chunk) => _chunks.Writer.TryWrite(chunk);
        public void Complete() => _chunks.Writer.TryComplete();
        public void Fail(Exception error) => _chunks.Writer.TryComplete(error);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
                _chunks.Reader.TryRead(out _current);
                _offset = 0;
            }
            int n = Math.Min(buffer.Length, _current!.Length - _offset);
            _current.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _chunks.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Receives what the client writes, parses the frames and queues the requests.</summary>
    sealed class RequestSink : Stream
    {
        readonly Channel<JsonElement> _requests = Channel.CreateUnbounded<JsonElement>();
        readonly List<JsonElement> _received = [];
        readonly List<byte> _pending = [];
        readonly object _gate = new();
        volatile bool _broken;
        volatile TaskCompletionSource? _stall;

        public IReadOnlyList<JsonElement> Received { get { lock (_gate) return [.. _received]; } }

        public ValueTask<JsonElement> NextAsync(CancellationToken ct = default) => _requests.Reader.ReadAsync(ct);
        public void Break() => _broken = true;
        public void Stall() => _stall = new TaskCompletionSource();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_broken) throw new IOException("broken pipe (test)");
            if (_stall is { } stall) await stall.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                _pending.AddRange(buffer.ToArray());
                while (TryTakeFrame(out var body))
                {
                    var request = JsonDocument.Parse(body).RootElement.Clone();
                    _received.Add(request);
                    _requests.Writer.TryWrite(request);
                }
            }
        }

        bool TryTakeFrame(out byte[] body)
        {
            body = [];
            var text = Encoding.ASCII.GetString([.. _pending]);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) return false;
            int length = int.Parse(text["Content-Length: ".Length..end], System.Globalization.CultureInfo.InvariantCulture);
            if (_pending.Count < end + 4 + length) return false;
            body = [.. _pending.GetRange(end + 4, length)];
            _pending.RemoveRange(0, end + 4 + length);
            return true;
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
