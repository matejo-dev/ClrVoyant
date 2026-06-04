using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClrVoyant.Dap;

/// <summary>
/// Minimal Debug Adapter Protocol client over a child process's stdio
/// (netcoredbg --interpreter=vscode). Handles Content-Length framing,
/// request/response correlation, and surfaces protocol events as .NET events.
/// Ported and hardened from the spike's DapDriver.
/// </summary>
public sealed class DapClient : IDisposable
{
    readonly Process _proc;
    int _seq;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event Action<JsonElement>? Stopped;     // body of a 'stopped' event
    public event Action<JsonElement>? ExitedEvent;  // body of 'exited'/'terminated'
    public event Action<int>? ProcessStarted;       // systemProcessId
    public event Action<string>? Output;            // target stdout/stderr text
    public event Action<JsonElement>? BreakpointChanged;

    public int TargetPid { get; private set; }

    public DapClient(string netcoredbgPath)
    {
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo(netcoredbgPath, "--interpreter=vscode")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        };
    }

    public void Start()
    {
        _proc.Start();
        _ = Task.Run(ReadLoopAsync);
    }

    public Task WaitInitializedAsync() => _initialized.Task;

    public Task<JsonElement> RequestAsync(string command, object? arguments = null)
    {
        int seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        var msg = new { seq, type = "request", command, arguments = arguments ?? new { } };
        WriteMessage(JsonSerializer.SerializeToUtf8Bytes(msg));
        return tcs.Task;
    }

    void WriteMessage(byte[] body)
    {
        var s = _proc.StandardInput.BaseStream;
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (s) { s.Write(header); s.Write(body); s.Flush(); }
    }

    async Task ReadLoopAsync()
    {
        var stream = _proc.StandardOutput.BaseStream;
        try
        {
            while (true)
            {
                int len = await ReadHeaderAsync(stream);
                if (len < 0) break;
                var buf = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(got, len - got));
                    if (n == 0) return;
                    got += n;
                }
                Dispatch(JsonDocument.Parse(buf).RootElement);
            }
        }
        catch
        {
            // Stream closed / process gone: fail any pending requests.
            foreach (var kv in _pending)
                kv.Value.TrySetException(new IOException("DAP connection closed."));
        }
    }

    static async Task<int> ReadHeaderAsync(Stream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int n = await s.ReadAsync(one.AsMemory(0, 1));
            if (n == 0) return -1;
            sb.Append((char)one[0]);
        }
        foreach (var line in sb.ToString().Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                return int.Parse(line.AsSpan("Content-Length:".Length).Trim());
        return -1;
    }

    void Dispatch(JsonElement m)
    {
        if (!m.TryGetProperty("type", out var typeEl)) return;
        switch (typeEl.GetString())
        {
            case "response":
                int rseq = m.GetProperty("request_seq").GetInt32();
                if (_pending.TryRemove(rseq, out var tcs))
                {
                    bool ok = m.TryGetProperty("success", out var s) && s.GetBoolean();
                    if (ok)
                        tcs.SetResult(m);
                    else
                        tcs.SetException(new DapException(
                            m.GetProperty("command").GetString() ?? "?",
                            m.TryGetProperty("message", out var msg) ? msg.GetString() : null));
                }
                break;

            case "event":
                DispatchEvent(m.GetProperty("event").GetString()!, m.TryGetProperty("body", out var b) ? b : default);
                break;
        }
    }

    void DispatchEvent(string ev, JsonElement body)
    {
        switch (ev)
        {
            case "initialized": _initialized.TrySetResult(true); break;
            case "stopped": Stopped?.Invoke(body); break;
            case "exited":
            case "terminated": ExitedEvent?.Invoke(body); break;
            case "breakpoint": BreakpointChanged?.Invoke(body); break;
            case "process":
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("systemProcessId", out var pid))
                {
                    TargetPid = pid.GetInt32();
                    ProcessStarted?.Invoke(TargetPid);
                }
                break;
            case "output":
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("output", out var o))
                    Output?.Invoke(o.GetString() ?? "");
                break;
        }
    }

    public void Dispose()
    {
        // The process may exit between HasExited and Kill — that race is expected,
        // so swallow only that. Don't mask other failures (e.g. access denied).
        try { if (!_proc.HasExited) _proc.Kill(true); }
        catch (InvalidOperationException) { /* already exited */ }
        finally { _proc.Dispose(); }
    }
}

/// <summary>A failed DAP request response.</summary>
public sealed class DapException(string command, string? message)
    : Exception($"DAP '{command}' failed: {message}")
{
    public string Command { get; } = command;
}
