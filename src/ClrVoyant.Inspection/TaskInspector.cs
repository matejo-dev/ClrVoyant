using System.Text.RegularExpressions;
using ClrVoyant.Core;
using Microsoft.Diagnostics.Runtime;

namespace ClrVoyant.Inspection;

/// <summary>
/// Enumerates managed Task objects on a target's heap via ClrMD. This is the
/// "Tasks window" capability: DAP cannot list all in-flight Tasks, so we read the
/// (stopped) process and walk the heap. Coexistence with netcoredbg owning the
/// process was validated on both OSes by the spikes.
///
/// The heap is opened per-OS (see <see cref="OpenDataTarget"/>): on Windows a PSS
/// snapshot (a frozen copy); on Linux a passive read of the live process memory
/// (read-only, no second ptrace stop — the Linux spike proved this works while
/// netcoredbg holds the process). Either way the target must be stopped, so the
/// heap is consistent for the lifetime of the read.
///
/// Reads are cached per process and reused for all queries at the same stop
/// (keyed by the session's StopId), so list_tasks + get_async_graph + get_task at
/// one breakpoint open the heap once. A per-pid lock makes reuse safe under
/// concurrent multi-session access (a target is never disposed while in use).
/// </summary>
public sealed class TaskInspector : IDisposable
{
    // Not a primary constructor: dt/runtime are used both to initialize members and
    // again in Dispose(), which trips CS9124 (primary-ctor parameter captured into
    // state). Explicit fields are also clearer about what this type owns.
    sealed class Snapshot : IDisposable
    {
        readonly DataTarget _dt;
        public long StopId { get; }
        public ClrRuntime Runtime { get; }
        public Snapshot(long stopId, DataTarget dt, ClrRuntime runtime)
        {
            StopId = stopId;
            _dt = dt;
            Runtime = runtime;
        }
        public void Dispose() { Runtime.Dispose(); _dt.Dispose(); }
    }

    sealed class PidCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Snapshot? Current;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<int, PidCache> _byPid = new();

    // Run a read against a snapshot for (pid, stopId), reusing the cached one if it
    // matches the current stop, otherwise taking a fresh snapshot.
    T Read<T>(int pid, long stopId, Func<ClrRuntime, T> read)
    {
        var pc = _byPid.GetOrAdd(pid, _ => new PidCache());
        pc.Gate.Wait();
        try
        {
            if (pc.Current is null || pc.Current.StopId != stopId)
            {
                pc.Current?.Dispose();
                var dt = OpenDataTarget(pid);
                pc.Current = new Snapshot(stopId, dt, dt.ClrVersions[0].CreateRuntime());
            }
            return read(pc.Current.Runtime);
        }
        finally { pc.Gate.Release(); }
    }

    // Open a ClrMD view of the (stopped) target. Windows: a PSS snapshot, a frozen
    // copy stable even if the process resumes. Linux: a passive, read-only attach
    // that reads /proc/<pid>/mem without taking a second ptrace stop — the only
    // mechanism that coexists with netcoredbg's ICorDebug hold (validated by the
    // Linux spike; CreateSnapshotAndAttach is Windows-only). The target is held
    // stopped by netcoredbg for the duration, so the live read is consistent.
    static DataTarget OpenDataTarget(int pid)
        => OperatingSystem.IsWindows()
            ? DataTarget.CreateSnapshotAndAttach(pid)
            : DataTarget.AttachToProcess(pid, suspend: false);

    public void Dispose()
    {
        foreach (var pc in _byPid.Values) pc.Current?.Dispose();
        _byPid.Clear();
    }

    // System.Threading.Tasks.Task.m_stateFlags layout (runtime reference source).
    const int TASK_STATE_STARTED = 0x10000;
    const int TASK_STATE_DELEGATE_INVOKED = 0x20000;
    const int TASK_STATE_FAULTED = 0x200000;
    const int TASK_STATE_CANCELED = 0x400000;
    const int TASK_STATE_WAITING_ON_CHILDREN = 0x800000;
    const int TASK_STATE_RAN_TO_COMPLETION = 0x1000000;
    const int TASK_STATE_WAITINGFORACTIVATION = 0x2000000;

    // Patterns to recover the source method from an async state-machine type name,
    // covering: local functions (g__Name|), ordinary async methods (<Name>d__N),
    // and top-level statements (<Main>$).
    static readonly Regex[] MethodPatterns =
    {
        new(@"g__(?<m>[A-Za-z0-9_]+)\|", RegexOptions.Compiled),
        new(@"<(?<m>[A-Za-z0-9_]+)>d__\d+", RegexOptions.Compiled),
        new(@"<(?<m>[A-Za-z0-9_]+)>\$", RegexOptions.Compiled),
    };

    /// <summary>Enumerate every managed <c>Task</c> on the (stopped) target's heap.</summary>
    public IReadOnlyList<TaskInfo> EnumerateTasks(int pid, long stopId) => Read(pid, stopId, runtime =>
    {
        var result = new List<TaskInfo>();
        foreach (ClrObject obj in runtime.Heap.EnumerateObjects())
        {
            if (obj.Type is null || !IsTask(obj.Type)) continue;
            var info = TryReadTask(obj);
            if (info is not null) result.Add(info);
        }
        return (IReadOnlyList<TaskInfo>)result;
    });

    /// <summary>Read a single <c>Task</c> by its heap address (returns null if it is not a Task).</summary>
    public TaskInfo? GetTask(int pid, long stopId, ulong address) => Read(pid, stopId, runtime =>
    {
        var obj = runtime.Heap.GetObject(address);
        return obj.Type is not null && IsTask(obj.Type) ? TryReadTask(obj) : null;
    });

    /// <summary>Reconstruct the async await/continuation graph: for each
    /// async-relevant Task, what it awaits and what it continues into.</summary>
    public IReadOnlyList<AsyncNode> BuildAsyncGraph(int pid, long stopId) => Read(pid, stopId, runtime =>
    {
        var nodes = new List<AsyncNode>();
        foreach (ClrObject obj in runtime.Heap.EnumerateObjects())
        {
            if (obj.Type is null || !IsTask(obj.Type)) continue;
            int flags;
            try { flags = obj.ReadField<int>("m_stateFlags"); }
            catch { continue; }

            string type = obj.Type.Name ?? "?";
            var awaiting = ReadAwaiting(obj);
            ulong contAddr = ContinuationAddr(obj);
            string? asyncMethod = ExtractAsyncMethod(type);

            // Keep only async-relevant nodes to limit noise.
            if (asyncMethod is null && awaiting.Count == 0 && contAddr == 0) continue;

            nodes.Add(new AsyncNode(
                $"0x{obj.Address:x}", StatusOf(flags), asyncMethod, type,
                awaiting, contAddr == 0 ? null : $"0x{contAddr:x}"));
        }
        return (IReadOnlyList<AsyncNode>)nodes;
    });

    /// <summary>Follow continuation links from a starting Task, producing the
    /// logical "what resumes next" chain (the async call stack upward).</summary>
    public IReadOnlyList<AsyncNode> FollowContinuations(int pid, long stopId, ulong startAddress) => Read(pid, stopId, runtime =>
    {
        var heap = runtime.Heap;
        var chain = new List<AsyncNode>();
        var seen = new HashSet<ulong>();
        ulong cur = startAddress;
        while (cur != 0 && seen.Add(cur))
        {
            var obj = heap.GetObject(cur);
            if (obj.Type is null || !IsTask(obj.Type)) break;
            int flags;
            try { flags = obj.ReadField<int>("m_stateFlags"); }
            catch { break; }

            string type = obj.Type.Name ?? "?";
            ulong contAddr = ContinuationAddr(obj);
            chain.Add(new AsyncNode(
                $"0x{cur:x}", StatusOf(flags), ExtractAsyncMethod(type), type,
                ReadAwaiting(obj), contAddr == 0 ? null : $"0x{contAddr:x}"));
            cur = contAddr;
        }
        return (IReadOnlyList<AsyncNode>)chain;
    });

    // Addresses of the Tasks an async state machine box is parked on (its awaiters).
    // Note: in Debug builds the compiler emits the state machine as a CLASS, in
    // Release builds as a STRUCT. Handle both.
    static IReadOnlyList<string> ReadAwaiting(ClrObject box)
    {
        var list = new List<string>();
        if (box.Type?.Name?.Contains("AsyncStateMachineBox") != true) return list;

        // Debug builds: state machine is a reference type.
        try
        {
            var smObj = box.ReadObjectField("StateMachine");
            if (!smObj.IsNull)
            {
                foreach (var f in AwaiterFields(smObj.Type))
                {
                    try { AddAwaiterTask(smObj.ReadValueTypeField(f), list); } catch { /* best-effort heap read: this field/layout may not be present */ }
                }
                if (list.Count > 0) return list;
            }
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }

        // Release builds: state machine is a value type.
        try
        {
            var smVal = box.ReadValueTypeField("StateMachine");
            foreach (var f in AwaiterFields(smVal.Type))
            {
                try { AddAwaiterTask(smVal.ReadValueTypeField(f), list); } catch { /* best-effort heap read: this field/layout may not be present */ }
            }
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }
        return list;
    }

    static IEnumerable<string> AwaiterFields(ClrType? smType)
    {
        if (smType is null) yield break;
        foreach (var f in smType.Fields)
        {
            string name = f.Name ?? "";
            string tn = f.Type?.Name ?? "";
            // Compiler-generated awaiter fields are named <>u__N; their type
            // (when resolvable) contains "Awaiter".
            if (tn.Contains("Awaiter") || name.Contains("u__"))
                yield return f.Name ?? "";
        }
    }

    static void AddAwaiterTask(ClrValueType awaiter, List<string> list)
    {
        ulong addr = ReadAwaiterTask(awaiter);
        if (addr != 0) list.Add($"0x{addr:x}");
    }

    // A TaskAwaiter has m_task directly; configured/value-task awaiters nest it.
    static ulong ReadAwaiterTask(ClrValueType awaiter)
    {
        try
        {
            var t = awaiter.ReadObjectField("m_task");
            if (!t.IsNull) return t.Address;
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }
        // Look one level down for a nested awaiter struct that holds m_task.
        try
        {
            foreach (var f in awaiter.Type!.Fields)
            {
                if (f.Type?.IsValueType != true || string.IsNullOrEmpty(f.Name)) continue;
                try
                {
                    var inner = awaiter.ReadValueTypeField(f.Name);
                    var t = inner.ReadObjectField("m_task");
                    if (!t.IsNull) return t.Address;
                }
                catch { /* best-effort heap read: this field/layout may not be present */ }
            }
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }
        return 0;
    }

    // The Task that continues when this one completes (if the continuation is a Task).
    static ulong ContinuationAddr(ClrObject obj)
    {
        try
        {
            var c = obj.ReadObjectField("m_continuationObject");
            if (!c.IsNull && c.Type is not null && IsTask(c.Type)) return c.Address;
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }
        return 0;
    }

    static bool IsTask(ClrType? t)
    {
        for (var cur = t; cur is not null; cur = cur.BaseType)
            if (cur.Name == "System.Threading.Tasks.Task") return true;
        return false;
    }

    static TaskInfo? TryReadTask(ClrObject obj)
    {
        int flags;
        try { flags = obj.ReadField<int>("m_stateFlags"); }
        catch { return null; } // not the real Task layout

        long id = 0;
        try { id = obj.ReadField<int>("m_taskId"); } catch { /* best-effort heap read: this field/layout may not be present */ }

        string typeName = obj.Type!.Name ?? "?";

        string? continuation = null;
        try
        {
            var c = obj.ReadObjectField("m_continuationObject");
            if (!c.IsNull) continuation = c.Type?.Name;
        }
        catch { /* best-effort heap read: this field/layout may not be present */ }

        // Async method name, if this Task is (or continues into) an async state
        // machine box like AsyncStateMachineBox<Foo+<Method>d__N>.
        string? asyncMethod = ExtractAsyncMethod(typeName) ?? ExtractAsyncMethod(continuation);

        return new TaskInfo(id, $"0x{obj.Address:x}", StatusOf(flags), typeName, asyncMethod, continuation);
    }

    internal static string? ExtractAsyncMethod(string? typeName)
    {
        if (typeName is null || !typeName.Contains("AsyncStateMachineBox")) return null;
        foreach (var pat in MethodPatterns)
        {
            var m = pat.Match(typeName);
            if (m.Success) return m.Groups["m"].Value;
        }
        return null;
    }

    internal static string StatusOf(int flags)
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
}
