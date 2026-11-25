using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ClrVoyant.Core;

namespace ClrVoyant.Dap;

/// <summary>
/// Minimal Debug Adapter Protocol client over a pair of byte streams (in production the
/// stdio of <c>netcoredbg --interpreter=vscode</c>). Handles Content-Length framing,
/// request/response correlation, and surfaces protocol events as .NET events.
///
/// Lifecycle: the client is either open or CLOSED, and closing is terminal. It closes
/// exactly once, on whichever happens first: end of the adapter's output stream, a read
/// or framing failure, a failed write, or <see cref="Dispose"/>. Closing fails every
/// in-flight request and makes every later request fail immediately with the same
/// error, which names the cause and, for a spawned adapter, its exit code and the tail
/// of its stderr. No request can therefore wait on an adapter that is gone.
///
/// Every request is bounded (<see cref="DefaultRequestTimeout"/> unless overridden) and
/// cancellable; a timed-out or cancelled request is removed from the pending table.
/// </summary>
public sealed class DapClient : IDisposable
{
    /// <summary>
    /// Default bound for a request. Chosen so a legitimately slow answer survives — a
    /// first <c>stackTrace</c> (symbol loading) or an <c>evaluate</c> of a heavy
    /// expression can take several seconds — while a wedged adapter still turns into an
    /// error well inside an agent's own tool-call patience. Callers whose answer is
    /// bounded elsewhere (launch/attach, see <see cref="DapEngine"/>) or that need
    /// something tighter (disconnect) pass their own value.
    /// </summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    // Tail kept of the adapter's stderr (and of recoverable client-side problems), for
    // the "connection closed" message. Small: only the last lines explain a crash.
    const int DiagnosticsCapacity = 8 * 1024;

    // After the adapter's stdout ends, wait this long for its stderr to finish and for
    // the process to report its exit code, so the closed message can say why.
    static readonly TimeSpan CloseDiagnosticsGrace = TimeSpan.FromSeconds(1);

    readonly Stream _input;
    readonly Stream _output;
    readonly Stream? _stderr;
    readonly Process? _proc;
    readonly FrameReader _frames;
    readonly CancellationTokenSource _lifetime = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly BoundedTextTail _diagnostics = new(DiagnosticsCapacity);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task _stderrDrain = Task.CompletedTask;
    IOException? _closed;
    int _seq;
    int _started;
    int _disposed;

    public event Action<JsonElement>? Stopped;        // body of a 'stopped' event
    public event Action<JsonElement>? ExitedEvent;    // body of an 'exited' event ({ exitCode })
    public event Action<JsonElement>? TerminatedEvent; // body of a 'terminated' event
    public event Action<int>? ProcessStarted;         // systemProcessId
    public event Action<string, string>? Output;      // (category, text) of an 'output' event
    public event Action<JsonElement>? BreakpointChanged;
    /// <summary>Raised once when the connection is lost (the adapter ended or the stream
    /// broke) — not when this client is disposed. Carries the same error later requests fail with.</summary>
    public event Action<IOException>? ConnectionClosed;

    public int TargetPid { get; private set; }

    /// <summary>Requests sent and still awaiting a response (test seam: a finished request must not linger).</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>True once the connection is closed (terminal; see the type remarks).</summary>
    public bool IsClosed => Volatile.Read(ref _closed) is not null;

    /// <summary>
    /// Client over an already-connected pair of streams. The client takes ownership: it
    /// disposes them on <see cref="Dispose"/>. <paramref name="adapterStderr"/> is
    /// optional; when given it is drained into a bounded buffer whose tail appears in
    /// the closed-connection error. Call <see cref="Start"/> after subscribing to events.
    /// </summary>
    public DapClient(Stream fromAdapter, Stream toAdapter, Stream? adapterStderr = null)
    {
        _input = fromAdapter;
        _output = toAdapter;
        _stderr = adapterStderr;
        _frames = new FrameReader(fromAdapter);
        // 'initialized' never arriving because the connection died is reported through
        // the pending request; observe the fault so it is not left unobserved.
        _ = _initialized.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Spawn netcoredbg and connect to its stdio. The process starts here.</summary>
    public DapClient(string netcoredbgPath) : this(Spawn(netcoredbgPath)) { }

    DapClient(Process proc) : this(proc.StandardOutput.BaseStream, proc.StandardInput.BaseStream, proc.StandardError.BaseStream)
        => _proc = proc;

    static Process Spawn(string netcoredbgPath)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(netcoredbgPath, "--interpreter=vscode")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
        proc.Start();
        return proc;
    }

    /// <summary>Begin reading from the adapter. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        if (_stderr is not null) _stderrDrain = Task.Run(DrainStderrAsync);
        _ = Task.Run(ReadLoopAsync);
    }

    public Task WaitInitializedAsync() => _initialized.Task;

    /// <summary>
    /// Send a request and await its response body. Fails with <see cref="DapException"/>
    /// if the adapter rejects it, <see cref="TimeoutException"/> if no response arrives
    /// within <paramref name="timeout"/> (default <see cref="DefaultRequestTimeout"/>;
    /// <see cref="Timeout.InfiniteTimeSpan"/> for none), <see cref="OperationCanceledException"/>
    /// if <paramref name="ct"/> fires, and <see cref="IOException"/> if the connection is
    /// or becomes closed.
    /// </summary>
    public async Task<JsonElement> RequestAsync(string command, object? arguments = null,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfClosed();

        var limit = timeout ?? DefaultRequestTimeout;
        int seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        // Close() publishes the closed state and THEN drains _pending; registering and
        // then re-checking guarantees we are either drained or see the closed state.
        if (IsClosed) { _pending.TryRemove(seq, out _); ThrowIfClosed(); }

        byte[] frame = Frame(JsonSerializer.SerializeToUtf8Bytes(
            new { seq, type = "request", command, arguments = arguments ?? new { } }));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (limit != Timeout.InfiniteTimeSpan) cts.CancelAfter(limit);

        Task write = WriteFrameAsync(frame);
        // If we stop waiting for the write, its eventual failure must not go unobserved.
        _ = write.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        bool written = false;
        try
        {
            await write.WaitAsync(cts.Token).ConfigureAwait(false);
            written = true;
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(seq, out _);
            await AbortIfWriteStuckAsync(write, command).ConfigureAwait(false);
            throw new TimeoutException(
                $"DAP request '{command}' got no response from netcoredbg within " +
                $"{limit.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s " +
                "(the debuggee may be busy, or the adapter is wedged). A late answer is discarded.");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(seq, out _);
            await AbortIfWriteStuckAsync(write, command).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (!written)
        {
            // The write itself failed (broken pipe...): the stream is unusable.
            _pending.TryRemove(seq, out _);
            await ConnectionLostAsync($"writing '{command}' failed: {ex.Message}", ex).ConfigureAwait(false);
            ThrowIfClosed();
            throw;
        }
        catch
        {
            _pending.TryRemove(seq, out _);
            throw;
        }
    }

    // A frame that was only partly written leaves the stream out of sync, so an
    // unfinished write (timeout/cancel while blocked on a full pipe) is fatal.
    async Task AbortIfWriteStuckAsync(Task write, string command)
    {
        if (!write.IsCompleted)
            await ConnectionLostAsync($"writing '{command}' did not complete; the adapter is not reading its input", null)
                .ConfigureAwait(false);
    }

    static byte[] Frame(byte[] body)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        return frame;
    }

    async Task WriteFrameAsync(byte[] frame)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(frame).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    void ThrowIfClosed()
    {
        if (Volatile.Read(ref _closed) is { } c) throw new IOException(c.Message, c);
    }

    // ---- read side ----

    async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                byte[]? frame = await _frames.ReadFrameAsync(_lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    await ConnectionLostAsync("the adapter closed its output stream", null).ConfigureAwait(false);
                    return;
                }
                ProcessFrame(frame);
            }
        }
        catch (Exception ex)
        {
            // Read failure or an unrecoverable framing error (no usable Content-Length,
            // so message boundaries are lost). Either way this connection is finished.
            await ConnectionLostAsync($"reading from the adapter failed: {ex.Message}", ex).ConfigureAwait(false);
        }
    }

    // A frame is well delimited here, so a bad payload only costs that one message:
    // skip it, note it, keep the loop alive. (A bad FRAME is fatal; see FrameReader.)
    void ProcessFrame(byte[] frame)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(frame);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Note($"dropped an unparseable DAP message: {ex.Message}");
            return;
        }

        try { Dispatch(root); }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Note($"dropped a malformed DAP message: {ex.Message}");
        }
    }

    void Dispatch(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("type", out var typeEl)) return;
        switch (typeEl.GetString())
        {
            case "response":
                int rseq = m.GetProperty("request_seq").GetInt32();
                if (_pending.TryRemove(rseq, out var tcs))
                {
                    bool ok = m.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                    if (ok)
                        tcs.TrySetResult(m);
                    else
                        tcs.TrySetException(new DapException(
                            m.TryGetProperty("command", out var c) ? c.GetString() ?? "?" : "?",
                            m.TryGetProperty("message", out var msg) ? msg.GetString() : null));
                }
                break;

            case "event":
                DispatchEvent(m.GetProperty("event").GetString() ?? "", m.TryGetProperty("body", out var b) ? b : default);
                break;
        }
    }

    // Subscribers run on the read loop, so each call is isolated: a throwing handler is
    // noted and must not stop later messages (or later events) from being processed.
    void DispatchEvent(string ev, JsonElement body)
    {
        switch (ev)
        {
            case "initialized": _initialized.TrySetResult(true); break;
            case "stopped": Isolate(ev, () => Stopped?.Invoke(body)); break;
            case "exited": Isolate(ev, () => ExitedEvent?.Invoke(body)); break;
            case "terminated": Isolate(ev, () => TerminatedEvent?.Invoke(body)); break;
            case "breakpoint": Isolate(ev, () => BreakpointChanged?.Invoke(body)); break;
            case "process":
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("systemProcessId", out var pid))
                {
                    TargetPid = pid.GetInt32();
                    Isolate(ev, () => ProcessStarted?.Invoke(TargetPid));
                }
                break;
            case "output":
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("output", out var o))
                {
                    string category = body.TryGetProperty("category", out var c) ? c.GetString() ?? "console" : "console";
                    string text = o.GetString() ?? "";
                    Isolate(ev, () => Output?.Invoke(category, text));
                }
                break;
        }
    }

    void Isolate(string ev, Action call)
    {
        try { call(); }
        catch (Exception ex) { Note($"a subscriber of the '{ev}' event threw {ex.GetType().Name}: {ex.Message}"); }
    }

    // Recoverable problems have no logger in this layer; they go into the same bounded
    // tail as the adapter's stderr, so they show up if the connection later closes.
    void Note(string message) => _diagnostics.Append($"[clrvoyant] {message}\n");

    async Task DrainStderrAsync()
    {
        try
        {
            using var reader = new StreamReader(_stderr!, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var buf = new char[1024];
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
                _diagnostics.Append(new string(buf, 0, n));
        }
        catch { /* stream closed on dispose / process exit */ }
    }

    // ---- terminal state ----

    async Task ConnectionLostAsync(string reason, Exception? error)
    {
        if (IsClosed) return;

        // Give stderr a moment to finish and the process a moment to report its exit
        // code, so the error says WHY the adapter went away.
        string? exitInfo = null;
        using (var grace = new CancellationTokenSource(CloseDiagnosticsGrace))
        {
            try { await _stderrDrain.WaitAsync(grace.Token).ConfigureAwait(false); } catch { /* best effort */ }
            if (_proc is not null)
            {
                try
                {
                    await _proc.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    exitInfo = $" (adapter exit code {_proc.ExitCode})";
                }
                catch { /* still running or gone: no exit code to report */ }
            }
        }

        var sb = new StringBuilder("DAP connection to netcoredbg closed: ").Append(reason).Append(exitInfo).Append('.');
        string tail = _diagnostics.GetTail(DiagnosticsCapacity).Trim();
        if (tail.Length > 0) sb.Append(" Adapter stderr / client notes (tail): ").Append(tail);
        var cause = new IOException(sb.ToString(), error);
        if (Close(cause)) Isolate("connection-closed", () => ConnectionClosed?.Invoke(cause));
    }

    bool Close(IOException cause)
    {
        if (Interlocked.CompareExchange(ref _closed, cause, null) is not null) return false;
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(cause);
        _initialized.TrySetException(cause);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Close(new IOException("DAP connection to netcoredbg closed: the client was disposed."));
        _lifetime.Cancel();

        // The process may exit between HasExited and Kill — that race is expected,
        // so swallow only that. Don't mask other failures (e.g. access denied).
        try { if (_proc is not null && !_proc.HasExited) _proc.Kill(true); }
        catch (InvalidOperationException) { /* already exited */ }
        finally
        {
            foreach (var s in new Stream?[] { _input, _output, _stderr })
            {
                try { s?.Dispose(); } catch { /* best effort */ }
            }
            _proc?.Dispose();
        }
    }

    /// <summary>
    /// Buffered reader for Content-Length framed messages. Correct for any split of the
    /// byte stream across reads (a header or body spanning several reads, or several
    /// frames in one read). A clean end of stream between frames yields null; a stream
    /// that ends mid-frame, or a header without a usable Content-Length, throws —
    /// without a length the next message boundary is unknowable, so it is unrecoverable.
    /// </summary>
    sealed class FrameReader(Stream stream)
    {
        const int MaxHeaderBytes = 8 * 1024;
        const int MaxBodyBytes = 64 * 1024 * 1024;
        static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;

        readonly byte[] _buf = new byte[MaxHeaderBytes];
        int _start, _end;

        public async ValueTask<byte[]?> ReadFrameAsync(CancellationToken ct)
        {
            int headerLen;
            while ((headerLen = _buf.AsSpan(_start, _end - _start).IndexOf(HeaderEnd)) < 0)
            {
                if (_end - _start >= MaxHeaderBytes)
                    throw new InvalidDataException($"DAP header exceeds {MaxHeaderBytes} bytes without a terminator.");
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    if (_end == _start) return null; // clean end between frames
                    throw new EndOfStreamException("The stream ended in the middle of a DAP header.");
                }
            }

            int length = ParseContentLength(Encoding.ASCII.GetString(_buf, _start, headerLen));
            _start += headerLen + HeaderEnd.Length;

            var body = new byte[length];
            int have = Math.Min(length, _end - _start);
            Array.Copy(_buf, _start, body, 0, have);
            _start += have;
            if (_start == _end) _start = _end = 0;
            while (have < length)
            {
                int n = await stream.ReadAsync(body.AsMemory(have), ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("The stream ended in the middle of a DAP message body.");
                have += n;
            }
            return body;
        }

        // Read more bytes after the buffered ones; false at end of stream.
        async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            int n = await stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            _end += n;
            return n > 0;
        }

        static int ParseContentLength(string header)
        {
            foreach (var line in header.Split("\r\n"))
            {
                const string name = "Content-Length:";
                if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(line.AsSpan(name.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int len)
                    || len > MaxBodyBytes)
                    throw new InvalidDataException($"Unusable DAP Content-Length: '{line}'.");
                return len;
            }
            throw new InvalidDataException("DAP header has no Content-Length.");
        }
    }
}

/// <summary>A failed DAP request response.</summary>
public sealed class DapException(string command, string? message)
    : Exception($"DAP '{command}' failed: {message}")
{
    public string Command { get; } = command;
}
