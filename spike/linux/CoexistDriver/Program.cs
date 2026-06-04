using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

// CoexistDriver: Linux variant of the DAP driver for the coexistence spike.
// Unlike the Windows DapDriver it stops the target AT ENTRY, sets the breakpoint
// while stopped (so it is bound before the line runs — no bind/exec race), then
// continues to the breakpoint and, while netcoredbg holds the process there, runs
// the ClrMD probe against the same PID.
//
// args: <netcoredbg> <target.dll> <sourceFile> <bpLine> <probeExe>

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: CoexistDriver <netcoredbg> <target.dll> <sourceFile> <bpLine> <probeExe>");
    return 2;
}

string dbg = Path.GetFullPath(args[0]);
string targetDll = Path.GetFullPath(args[1]);
string sourceFile = Path.GetFullPath(args[2]);
int bpLine = int.Parse(args[3]);
string probeExe = Path.GetFullPath(args[4]);

var client = new DapClient(dbg);
client.Start();

await client.Request("initialize", new
{
    clientID = "coexist-driver",
    adapterID = "coreclr",
    linesStartAt1 = true,
    columnsStartAt1 = true,
    pathFormat = "path",
    supportsRunInTerminalRequest = false,
});
Console.WriteLine("[dap] initialize OK");

// Launch stopped at entry so we get a deterministic first stop.
_ = client.Request("launch", new
{
    request = "launch",
    type = "coreclr",
    program = targetDll,
    cwd = Path.GetDirectoryName(targetDll),
    stopAtEntry = true,
    justMyCode = false,
});

await client.WaitInitialized();
Console.WriteLine("[dap] initialized; finishing configuration");
await client.Request("configurationDone", new { });

// 1) Entry stop.
var entry = await client.NextStop(TimeSpan.FromSeconds(30));
int entryThread = entry.GetProperty("threadId").GetInt32();
Console.WriteLine($"[dap] entry stop (thread {entryThread}); binding breakpoint while stopped");

// 2) Bind the breakpoint now (while stopped, so it is bound before the line runs).
//    The target line must be SYNCHRONOUS and executed repeatedly (a loop body) so
//    binding is trivial and any one-shot race is irrelevant — it re-executes.
var bpResp = await client.Request("setBreakpoints", new
{
    source = new { path = sourceFile },
    breakpoints = new[] { new { line = bpLine } },
});
Console.WriteLine($"[dap] setBreakpoints @ {Path.GetFileName(sourceFile)}:{bpLine} -> verified={BpVerified(bpResp)}");

// 3) Continue to the breakpoint (netcoredbg's pause is unreliable on a freely
//    running target, so we drive to a real breakpoint instead).
try { await client.Request("continue", new { threadId = entryThread }); Console.WriteLine("[dap] continue OK"); }
catch (Exception ex) { Console.WriteLine("[dap] continue FAILED: " + ex.Message); }
var stopped = await client.NextStop(TimeSpan.FromSeconds(30));
if (stopped.ValueKind == JsonValueKind.Undefined)
{
    Console.WriteLine("[dap] target exited/terminated before we could hold it — see [evt] lines above.");
    client.Kill();
    return 3;
}
string reason = stopped.TryGetProperty("reason", out var r) ? r.GetString() ?? "?" : "?";
Console.WriteLine($"[dap] STOPPED reason={reason} pid={client.TargetPid}");

// 4) Sanity: read the call stack through netcoredbg (control plane works).
var st = await client.Request("stackTrace", new { threadId = stopped.GetProperty("threadId").GetInt32(), startFrame = 0, levels = 3 });
Console.WriteLine("[dap] top frames: " + string.Join(" | ",
    st.GetProperty("body").GetProperty("stackFrames").EnumerateArray().Select(f => f.GetProperty("name").GetString())));

// 5) COEXISTENCE: while netcoredbg owns the stopped process, run the ClrMD probe.
Console.WriteLine($"\n[coexist] running ClrMD probe against held PID {client.TargetPid} ...\n");
var psi = new ProcessStartInfo(probeExe, client.TargetPid.ToString())
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
var probe = Process.Start(psi)!;
probe.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
probe.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[probe-err] " + e.Data); };
probe.BeginOutputReadLine();
probe.BeginErrorReadLine();
await probe.WaitForExitAsync();

try { await client.Request("disconnect", new { terminateDebuggee = true }); } catch { }
client.Kill();
Console.WriteLine($"\n[dap] done. probe exit={probe.ExitCode}");
return probe.ExitCode;

static bool BpVerified(JsonElement resp)
{
    try { return resp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(); }
    catch { return false; }
}

// Minimal DAP client over netcoredbg's stdio, supporting a SEQUENCE of stops.
sealed class DapClient
{
    readonly Process _proc;
    int _seq;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Channel<JsonElement> _stops = Channel.CreateUnbounded<JsonElement>();
    public int TargetPid { get; private set; }

    public DapClient(string dbgPath)
    {
        _proc = new Process
        {
            StartInfo = new ProcessStartInfo(dbgPath, "--interpreter=vscode")
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
        _ = Task.Run(ReadLoop);
        _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[netcoredbg] " + e.Data); };
        _proc.BeginErrorReadLine();
    }

    public Task WaitInitialized() => _initialized.Task;

    public async Task<JsonElement> NextStop(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try { return await _stops.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("no 'stopped' event within timeout"); }
    }

    public Task<JsonElement> Request(string command, object arguments)
    {
        int seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        WriteMessage(JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command, arguments }));
        return tcs.Task;
    }

    void WriteMessage(byte[] body)
    {
        var s = _proc.StandardInput.BaseStream;
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (s) { s.Write(header); s.Write(body); s.Flush(); }
    }

    async Task ReadLoop()
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
        catch (Exception ex) { Console.Error.WriteLine("[dap] read loop ended: " + ex.Message); }
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
        string type = m.GetProperty("type").GetString()!;
        if (type == "response")
        {
            int rseq = m.GetProperty("request_seq").GetInt32();
            if (_pending.TryRemove(rseq, out var tcs))
            {
                bool ok = m.TryGetProperty("success", out var s) && s.GetBoolean();
                if (ok) tcs.SetResult(m);
                else tcs.SetException(new Exception($"DAP '{m.GetProperty("command").GetString()}' failed: {(m.TryGetProperty("message", out var msg) ? msg.GetString() : "")}"));
            }
        }
        else if (type == "event")
        {
            string ev = m.GetProperty("event").GetString()!;
            switch (ev)
            {
                case "initialized": _initialized.TrySetResult(true); break;
                case "stopped": _stops.Writer.TryWrite(m.GetProperty("body")); break;
                case "process":
                    if (m.GetProperty("body").TryGetProperty("systemProcessId", out var pid))
                        TargetPid = pid.GetInt32();
                    Console.Error.WriteLine($"[evt] process pid={TargetPid}");
                    break;
                case "output":
                    var b = m.GetProperty("body");
                    if (b.TryGetProperty("output", out var o)) Console.Write("[target] " + o.GetString());
                    break;
                case "exited":
                case "terminated":
                    Console.Error.WriteLine($"[evt] {ev}: {m.GetProperty("body").GetRawText()}");
                    // Unblock NextStop so the driver reports instead of hanging.
                    _stops.Writer.TryWrite(default);
                    break;
                default:
                    Console.Error.WriteLine($"[evt] {ev}");
                    break;
            }
        }
    }

    public void Kill() { try { if (!_proc.HasExited) _proc.Kill(true); } catch { } }
}
