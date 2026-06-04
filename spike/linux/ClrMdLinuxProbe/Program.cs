using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;

// ClrMdLinuxProbe: the LINUX coexistence spike. Invoked while netcoredbg already
// owns the target process (stopped at a breakpoint), it tries every Linux way to
// read the managed heap and enumerate Tasks, and reports which one works. This
// answers the risk the Windows spike never touched: on Windows we used PSS
// (CreateSnapshotAndAttach), which does not exist on Linux, and Linux allows only
// one ptrace tracer at a time — so can ClrMD still see the heap while netcoredbg
// holds the process?
//
// Usage: ClrMdLinuxProbe <pid>

if (args.Length != 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
{
    Console.Error.WriteLine("usage: ClrMdLinuxProbe <pid>");
    return 2;
}

Console.WriteLine($"[probe] target PID {pid}; netcoredbg is assumed to own it (stopped).");

var results = new List<(string mechanism, bool ok, int tasks, string note)>();

// Ordered least-disruptive first; the invasive ptrace attach runs last because it
// can perturb the process for the others.

// 1) Passive ClrMD attach: read /proc/pid/mem WITHOUT a second ptrace-stop.
//    Closest analog to the Windows snapshot — read-only, non-invasive.
TryMechanism("AttachToProcess(suspend:false) [passive]", () => DataTarget.AttachToProcess(pid, suspend: false));

// 2) createdump → LoadDump: ask the runtime's createdump tool to write an ELF core.
TryMechanism("createdump + LoadDump", () =>
{
    string core = Path.Combine(Path.GetTempPath(), $"core_{pid}_createdump.dmp");
    string tool = FindCreatedump() ?? throw new FileNotFoundException("createdump not found in shared framework");
    RunTool(tool, $"-f {core} {pid}", TimeSpan.FromSeconds(30));
    if (!File.Exists(core)) throw new FileNotFoundException($"createdump produced no file at {core}");
    return DataTarget.LoadDump(core);
});

// 3) DiagnosticsClient.WriteDump → LoadDump: ask the RUNTIME (via the diagnostic
//    IPC socket) to dump itself. No ptrace at all — most likely to coexist, IF the
//    runtime's diagnostic thread is still serviceable while netcoredbg holds it.
TryMechanism("DiagnosticsClient.WriteDump + LoadDump", () =>
{
    string core = Path.Combine(Path.GetTempPath(), $"core_{pid}_ipc.dmp");
    var task = Task.Run(() => new DiagnosticsClient(pid).WriteDump(DumpType.Full, core, logDumpGeneration: false));
    if (!task.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("WriteDump via diagnostic IPC timed out");
    return DataTarget.LoadDump(core);
});

// 4) Invasive ClrMD attach: ClrMD itself ptrace-stops the process. Expected to
//    clash with netcoredbg owning ptrace. Last because it is the most disruptive.
TryMechanism("AttachToProcess(suspend:true) [invasive ptrace]", () => DataTarget.AttachToProcess(pid, suspend: true));

Console.WriteLine();
Console.WriteLine("==================== COEXISTENCE RESULT ====================");
foreach (var r in results)
    Console.WriteLine($"  [{(r.ok ? "PASS" : "FAIL")}] {r.mechanism,-45} tasks={r.tasks,-4} {r.note}");
bool anyWin = results.Any(r => r.ok && r.tasks > 0);
Console.WriteLine($"\n  VERDICT: {(anyWin ? "AT LEAST ONE mechanism enumerates Tasks while netcoredbg holds the process." : "NO mechanism worked — the Windows snapshot model has no Linux analog under a held process.")}");
Console.WriteLine("============================================================");
return anyWin ? 0 : 1;

void TryMechanism(string name, Func<DataTarget> open)
{
    Console.WriteLine($"\n[probe] === {name} ===");
    try
    {
        using DataTarget dt = open();
        if (dt.ClrVersions.Length == 0) { results.Add((name, false, 0, "no CLR in target")); return; }
        using ClrRuntime runtime = dt.ClrVersions[0].CreateRuntime();
        ClrHeap heap = runtime.Heap;
        if (!heap.CanWalkHeap) { results.Add((name, false, 0, "heap not walkable")); return; }

        int total = 0; var byStatus = new Dictionary<string, int>();
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (obj.Type is null || !IsTask(obj.Type)) continue;
            int flags;
            try { flags = obj.ReadField<int>("m_stateFlags"); } catch { continue; }
            total++;
            string st = StatusOf(flags);
            byStatus[st] = byStatus.GetValueOrDefault(st) + 1;
        }
        string summary = string.Join(", ", byStatus.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}"));
        Console.WriteLine($"[probe] OK — CLR {dt.ClrVersions[0].Version}, {total} Task(s): {summary}");
        results.Add((name, true, total, summary));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[probe] FAILED: {ex.GetType().Name}: {ex.Message}");
        results.Add((name, false, 0, $"{ex.GetType().Name}: {ex.Message}"));
    }
}

static bool IsTask(ClrType? t)
{
    for (ClrType? cur = t; cur is not null; cur = cur.BaseType)
        if (cur.Name == "System.Threading.Tasks.Task") return true;
    return false;
}

static string StatusOf(int flags)
{
    const int STARTED = 0x10000, INVOKED = 0x20000, FAULTED = 0x200000, CANCELED = 0x400000,
              WAIT_CHILDREN = 0x800000, COMPLETE = 0x1000000, WFA = 0x2000000;
    if ((flags & FAULTED) != 0) return "Faulted";
    if ((flags & CANCELED) != 0) return "Canceled";
    if ((flags & COMPLETE) != 0) return "RanToCompletion";
    if ((flags & WAIT_CHILDREN) != 0) return "WaitingForChildren";
    if ((flags & INVOKED) != 0) return "Running";
    if ((flags & STARTED) != 0) return "WaitingToRun";
    if ((flags & WFA) != 0) return "WaitingForActivation";
    return "Created";
}

static string? FindCreatedump()
{
    // createdump ships in the shared framework, e.g.
    // /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.x/createdump
    var roots = new[]
    {
        Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        "/usr/share/dotnet",
        "/usr/lib/dotnet",
    };
    foreach (var root in roots)
    {
        if (string.IsNullOrEmpty(root)) continue;
        var dir = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(dir)) continue;
        foreach (var v in Directory.EnumerateDirectories(dir).OrderByDescending(d => d))
        {
            var cd = Path.Combine(v, "createdump");
            if (File.Exists(cd)) return cd;
        }
    }
    return null;
}

static void RunTool(string file, string args, TimeSpan timeout)
{
    var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    string err = p.StandardError.ReadToEnd();
    string outp = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } throw new TimeoutException($"{Path.GetFileName(file)} timed out"); }
    if (p.ExitCode != 0) throw new Exception($"{Path.GetFileName(file)} exit {p.ExitCode}: {err.Trim()} {outp.Trim()}");
}
