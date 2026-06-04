using System.Diagnostics;
using System.Text.Json;

// ServerSmoke: drives the REAL ClrVoyant.Server over stdio (MCP, newline-delimited
// JSON-RPC) on Linux, end-to-end: launch SampleApp, set a content breakpoint,
// continue to it, and enumerate Tasks via the cross-OS ClrMD path. This is the
// capstone proving the production server (not just the spike) works on Linux.
//
// args: <ClrVoyant.Server.dll> <netcoredbg> <SampleApp.dll> <SampleApp Program.cs>

if (args.Length != 4) { Console.Error.WriteLine("usage: ServerSmoke <serverDll> <netcoredbg> <sampleDll> <sampleSrc>"); return 2; }
string serverDll = args[0], netcoredbg = args[1], sampleDll = args[2], sampleSrc = args[3];

var psi = new ProcessStartInfo("dotnet", $"\"{serverDll}\"")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
psi.Environment["CLRVOYANT_NETCOREDBG"] = netcoredbg; // use the linux engine
var proc = Process.Start(psi)!;
proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("[server] " + e.Data); };
proc.BeginErrorReadLine();

int id = 0;
async Task<JsonElement> Rpc(string method, object? p = null, bool isNotification = false)
{
    var msg = isNotification
        ? (object)new { jsonrpc = "2.0", method, @params = p }
        : new { jsonrpc = "2.0", id = ++id, method, @params = p };
    await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg));
    await proc.StandardInput.FlushAsync();
    if (isNotification) return default;

    int wanted = id;
    while (true)
    {
        string? line = await proc.StandardOutput.ReadLineAsync();
        if (line is null) throw new Exception($"server closed stdout waiting for {method}");
        if (string.IsNullOrWhiteSpace(line)) continue;
        JsonElement o;
        try { o = JsonDocument.Parse(line).RootElement; } catch { continue; }
        if (o.TryGetProperty("id", out var rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == wanted)
        {
            if (o.TryGetProperty("error", out var err)) throw new Exception($"{method} error: {err.GetRawText()}");
            return o.GetProperty("result");
        }
    }
}

// A tools/call returning the parsed text payload of the first content block.
async Task<JsonElement> Call(string name, object arguments)
{
    var result = await Rpc("tools/call", new { name, arguments });
    string text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    if (result.TryGetProperty("isError", out var e) && e.GetBoolean()) throw new Exception($"tool {name} failed: {text}");
    try { return JsonDocument.Parse(text).RootElement; } catch { return JsonDocument.Parse($"\"{text}\"").RootElement; }
}

try
{
    await Rpc("initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "server-smoke", version = "1" } });
    await Rpc("notifications/initialized", new { }, isNotification: true);
    Console.WriteLine("[ok] MCP initialize");

    var launch = await Call("debug_launch", new { program = sampleDll });
    Console.WriteLine($"[ok] debug_launch -> session {launch.GetProperty("sessionId").GetString()} pid {launch.GetProperty("pid").GetInt32()}");

    var bp = await Call("set_breakpoint", new { file = sampleSrc, content = "int offset = squared + 7;" });
    Console.WriteLine($"[ok] content breakpoint -> line {bp.GetProperty("line").GetInt32()} verified={bp.GetProperty("verified").GetBoolean()}");

    var stop = await Call("continue", new { timeoutMs = 20000 });
    int? line = stop.GetProperty("stop").ValueKind == JsonValueKind.Object ? stop.GetProperty("stop").GetProperty("line").GetInt32() : null;
    if (line != 37) throw new Exception($"expected stop at line 37, got {line}");
    Console.WriteLine($"[ok] stopped at line {line}");

    var tasks = await Call("list_tasks", new { });
    int n = tasks.GetArrayLength();
    var statuses = tasks.EnumerateArray().Select(t => t.GetProperty("status").GetString()).ToList();
    bool wfa = statuses.Contains("WaitingForActivation");
    Console.WriteLine($"[ok] list_tasks -> {n} task(s); statuses: {string.Join(", ", statuses.Distinct())}");
    if (!wfa) throw new Exception("expected a WaitingForActivation task (the parked AwaitForever) — ClrMD Linux path failed");
    Console.WriteLine("[ok] WaitingForActivation present — ClrMD Task enumeration works on Linux via the real server");

    await Call("debug_stop", new { });
    Console.WriteLine("\nSERVER SMOKE (LINUX) PASSED");
    return 0;
}
finally
{
    try { if (!proc.HasExited) proc.Kill(true); } catch { }
}
