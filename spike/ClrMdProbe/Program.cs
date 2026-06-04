using System.Globalization;
using Microsoft.Diagnostics.Runtime;

// ClrMdProbe: attaches to a target .NET process (by PID, via a live snapshot)
// or loads a dump file, then enumerates every Task on the managed heap and
// classifies its state. This is the de-risking spike for the "Tasks window"
// capability of the MCP debug server.
//
// Usage:
//   ClrMdProbe <pid>
//   ClrMdProbe <path-to-dump>

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: ClrMdProbe <pid|dumpfile>");
    return 2;
}

// Task.m_stateFlags bit layout (from the .NET runtime reference source).
const int TASK_STATE_STARTED = 0x10000;
const int TASK_STATE_DELEGATE_INVOKED = 0x20000;
const int TASK_STATE_FAULTED = 0x200000;
const int TASK_STATE_CANCELED = 0x400000;
const int TASK_STATE_WAITING_ON_CHILDREN = 0x800000;
const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;
const int TASK_STATE_WAITINGFORACTIVATION = 0x2000000;

static string StatusOf(int flags)
{
    if ((flags & TASK_STATE_FAULTED) != 0) return "Faulted";
    if ((flags & TASK_STATE_CANCELED) != 0) return "Canceled";
    if ((flags & TASK_STATE_RAN_TO_COMPLETION) != 0) return "RanToCompletion";
    if ((flags & TASK_STATE_WAITING_ON_CHILDREN) != 0) return "WaitingForChildrenToComplete";
    if ((flags & TASK_STATE_DELEGATE_INVOKED) != 0) return "Running";
    if ((flags & TASK_STATE_STARTED) != 0) return "WaitingToRun";
    if ((flags & TASK_STATE_WAITINGFORACTIVATION) != 0) return "WaitingForActivation";
    return "Created";
}

DataTarget dt;
try
{
    if (int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
    {
        Console.WriteLine($"[probe] snapshotting live PID {pid} ...");
        dt = DataTarget.CreateSnapshotAndAttach(pid);
    }
    else
    {
        Console.WriteLine($"[probe] loading dump {args[0]} ...");
        dt = DataTarget.LoadDump(args[0]);
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[probe] attach/load FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

using (dt)
{
    if (dt.ClrVersions.Length == 0)
    {
        Console.Error.WriteLine("[probe] no CLR found in target");
        return 1;
    }

    using ClrRuntime runtime = dt.ClrVersions[0].CreateRuntime();
    ClrHeap heap = runtime.Heap;
    Console.WriteLine($"[probe] CLR {dt.ClrVersions[0].Version}, heap canWalk={heap.CanWalkHeap}");

    // True if the type chain contains System.Threading.Tasks.Task.
    static bool IsTask(ClrType? t)
    {
        for (ClrType? cur = t; cur is not null; cur = cur.BaseType)
        {
            if (cur.Name == "System.Threading.Tasks.Task") return true;
        }
        return false;
    }

    var byStatus = new Dictionary<string, int>();
    int total = 0;
    var samples = new List<string>();

    foreach (ClrObject obj in heap.EnumerateObjects())
    {
        ClrType? type = obj.Type;
        if (type is null || !IsTask(type)) continue;

        int flags;
        try { flags = obj.ReadField<int>("m_stateFlags"); }
        catch { continue; } // not a real Task layout

        total++;
        string status = StatusOf(flags);
        byStatus[status] = byStatus.GetValueOrDefault(status) + 1;

        int id = 0;
        try { id = obj.ReadField<int>("m_taskId"); } catch { }

        // Peek at the continuation object: this is the thread that, in the real
        // server, we'd follow to reconstruct the await/continuation graph.
        string cont = "-";
        try
        {
            ClrObject c = obj.ReadObjectField("m_continuationObject");
            if (!c.IsNull) cont = c.Type?.Name ?? "?";
        }
        catch { }

        if (samples.Count < 40)
            samples.Add($"  0x{obj.Address:x12}  id={id,-4} {status,-28} cont={cont}\n        type={type.Name}");
    }

    Console.WriteLine();
    Console.WriteLine($"[probe] Task objects found: {total}");
    foreach (var kv in byStatus.OrderByDescending(k => k.Value))
        Console.WriteLine($"   {kv.Value,4}  {kv.Key}");

    Console.WriteLine();
    Console.WriteLine("[probe] samples:");
    foreach (var s in samples)
        Console.WriteLine(s);
}

return 0;
