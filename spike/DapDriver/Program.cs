using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

// DapDriver: a minimal Debug Adapter Protocol client that drives netcoredbg to
// validate the control plane of the MCP debug server, and the coexistence of
// netcoredbg (controlling the process) with a ClrMD snapshot taken at a stop.
//
// args: <netcoredbg.exe> <target.dll> <sourceFile> <bpLine> <ClrMdProbe.exe>

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: DapDriver <netcoredbg.exe> <target.dll> <sourceFile> <bpLine> <ClrMdProbe.exe>");
    return 2;
}

// Normalize to full OS paths (converts '/' to '\' on Windows so breakpoint
// source paths match what the PDB recorded at compile time).
string dbg = Path.GetFullPath(args[0]);
string targetDll = Path.GetFullPath(args[1]);
string sourceFile = Path.GetFullPath(args[2]);
string probeExe = Path.GetFullPath(args[4]);
int bpLine = int.Parse(args[3]);

var client = new DapClient(dbg);
client.Start();

// 1) Handshake.
await client.Request("initialize", new
{
    clientID = "dap-driver-spike",
    adapterID = "coreclr",
    linesStartAt1 = true,
    columnsStartAt1 = true,
    pathFormat = "path",
    supportsRunInTerminalRequest = false,
});
Console.WriteLine("[dap] initialize OK");

// 2) Launch (response arrives later; fire it and continue the handshake).
var launchTask = client.Request("launch", new
{
    request = "launch",
    type = "coreclr",
    program = targetDll,
    cwd = Path.GetDirectoryName(targetDll),
    stopAtEntry = false,
    justMyCode = false,
});

// 3) After 'initialized', register breakpoints and finish configuration.
await client.WaitInitialized();
Console.WriteLine("[dap] initialized event received");

var bpResp = await client.Request("setBreakpoints", new
{
    source = new { path = sourceFile },
    breakpoints = new[] { new { line = bpLine } },
});
Console.WriteLine($"[dap] setBreakpoints @ {Path.GetFileName(sourceFile)}:{bpLine} -> verified={BpVerified(bpResp)}");

await client.Request("configurationDone", new { });
Console.WriteLine("[dap] configurationDone OK; running...");

// 4) Wait for the breakpoint hit.
var stopped = await client.WaitStopped(TimeSpan.FromSeconds(30));
int threadId = stopped.GetProperty("threadId").GetInt32();
string reason = stopped.TryGetProperty("reason", out var r) ? r.GetString() ?? "?" : "?";
Console.WriteLine($"\n[dap] STOPPED reason={reason} thread={threadId} pid={client.TargetPid}");

// 5a) Control-plane introspection through netcoredbg.
var st = await client.Request("stackTrace", new { threadId, startFrame = 0, levels = 5 });
var frames = st.GetProperty("body").GetProperty("stackFrames");
int topFrameId = frames[0].GetProperty("id").GetInt32();
Console.WriteLine("[dap] call stack (top 5):");
foreach (var f in frames.EnumerateArray())
    Console.WriteLine($"     {f.GetProperty("name").GetString()}");

var scopes = await client.Request("scopes", new { frameId = topFrameId });
var scope0 = scopes.GetProperty("body").GetProperty("scopes")[0];
int varRef = scope0.GetProperty("variablesReference").GetInt32();
var vars = await client.Request("variables", new { variablesReference = varRef });
Console.WriteLine($"[dap] locals in scope '{scope0.GetProperty("name").GetString()}':");
foreach (var v in vars.GetProperty("body").GetProperty("variables").EnumerateArray())
    Console.WriteLine($"     {v.GetProperty("name").GetString()} = {v.GetProperty("value").GetString()}");

var ev = await client.Request("evaluate", new { expression = "Zoo.Roots.Count", frameId = topFrameId, context = "repl" });
Console.WriteLine($"[dap] evaluate 'Zoo.Roots.Count' -> {EvalResult(ev)}");

// 5b) COEXISTENCE TEST: while netcoredbg holds the process stopped, run the
//     ClrMD snapshot probe against the same PID.
Console.WriteLine($"\n[coexist] launching ClrMD probe against PID {client.TargetPid} while netcoredbg owns it...");
var psi = new ProcessStartInfo(probeExe, client.TargetPid.ToString())
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
var probe = Process.Start(psi)!;
string probeOut = await probe.StandardOutput.ReadToEndAsync();
string probeErr = await probe.StandardError.ReadToEndAsync();
await probe.WaitForExitAsync();
Console.WriteLine($"[coexist] probe exit={probe.ExitCode}");
Console.WriteLine(probeOut);
if (probeErr.Length > 0) Console.WriteLine("[coexist] stderr: " + probeErr);

// 6) Tear down.
try { await client.Request("disconnect", new { terminateDebuggee = true }); } catch { }
client.Kill();
Console.WriteLine("[dap] done.");
return probe.ExitCode == 0 ? 0 : 1;

static bool BpVerified(JsonElement resp)
{
    try { return resp.GetProperty("body").GetProperty("breakpoints")[0].GetProperty("verified").GetBoolean(); }
    catch { return false; }
}
static string EvalResult(JsonElement resp)
{
    try { return resp.GetProperty("body").GetProperty("result").GetString() ?? "?"; }
    catch { return "(eval failed)"; }
}

// Minimal DAP client over netcoredbg's stdio.
sealed class DapClient
{
    readonly Process _proc;
    int _seq;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    readonly TaskCompletionSource<bool> _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<JsonElement> _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public async Task<JsonElement> WaitStopped(TimeSpan timeout)
    {
        var done = await Task.WhenAny(_stopped.Task, Task.Delay(timeout));
        if (done != _stopped.Task) throw new TimeoutException("no 'stopped' event within timeout");
        return (await _stopped.Task).GetProperty("body");
    }

    public Task<JsonElement> Request(string command, object arguments)
    {
        int seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;
        var msg = new { seq, type = "request", command, arguments };
        WriteMessage(JsonSerializer.SerializeToUtf8Bytes(msg));
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
        // Read bytes until "\r\n\r\n", parse Content-Length.
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
                case "stopped": _stopped.TrySetResult(m); break;
                case "breakpoint":
                    try
                    {
                        var bb = m.GetProperty("body").GetProperty("breakpoint");
                        Console.WriteLine($"[dap] breakpoint event: verified={bb.GetProperty("verified").GetBoolean()}" +
                            (bb.TryGetProperty("line", out var bl) ? $" line={bl.GetInt32()}" : ""));
                    }
                    catch { }
                    break;
                case "process":
                    if (m.GetProperty("body").TryGetProperty("systemProcessId", out var pid))
                        TargetPid = pid.GetInt32();
                    break;
                case "output":
                    var b = m.GetProperty("body");
                    if (b.TryGetProperty("output", out var o)) Console.Write("[target] " + o.GetString());
                    break;
            }
        }
    }

    public void Kill() { try { if (!_proc.HasExited) _proc.Kill(true); } catch { } }
}
