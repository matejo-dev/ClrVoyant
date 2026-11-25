using System.ComponentModel;
using System.Globalization;
using ClrVoyant.Core;
using ClrVoyant.Inspection;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>
/// Async/Task introspection: the "Tasks window" DAP cannot provide, read from the heap of the
/// stopped target with ClrMD. The heap walks run off the request thread and stop when the client
/// cancels the call.
/// </summary>
[McpServerToolType]
public static class TaskTools
{
    [McpServerTool(Name = "list_tasks", Title = "List managed Tasks", ReadOnly = true, OpenWorld = false)]
    [Description("Enumerate the managed Tasks on the heap of a stopped target (the 'Tasks window' equivalent), with status and async method. Optionally filter by status (e.g. 'WaitingForActivation', 'Running', 'Faulted'). Returns a page { items, total, truncated, nextOffset }: at most 'max' tasks (total counts every match); when truncated, repeat with offset=nextOffset, or narrow with 'status'.")]
    public static Task<Page<TaskInfo>> ListTasks(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Optional status filter, case-insensitive.")] string? status = null,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        [Description("Maximum number of tasks to return in this page (1-1000).")] int max = Paging.DefaultMax,
        [Description("Number of matching tasks to skip (use the previous page's nextOffset).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => OnStoppedTarget(sessions, sessionId, cancellationToken,
            (pid, stopId) => inspector.EnumerateTasks(pid, stopId, status, offset, max, cancellationToken));

    [McpServerTool(Name = "get_task", Title = "Inspect one Task", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect a single Task by its heap address (as returned by list_tasks, e.g. '0x1c3...'). Returns null if not a Task.")]
    public static Task<TaskInfo?> GetTask(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Heap address, hex (0x...) or decimal.")] string address,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ulong addr = ParseAddress(address);
        return OnStoppedTarget(sessions, sessionId, cancellationToken,
            (pid, stopId) => inspector.GetTask(pid, stopId, addr, cancellationToken));
    }

    [McpServerTool(Name = "get_async_graph", Title = "Get the async await/continuation graph", ReadOnly = true, OpenWorld = false)]
    [Description("Reconstruct the async await/continuation graph of a stopped target: for each async Task, which Tasks it is awaiting and which Task it continues into. Use addresses to correlate with list_tasks/get_task. Returns a page { items, total, truncated, nextOffset }: at most 'max' nodes; when truncated, repeat with offset=nextOffset.")]
    public static Task<Page<AsyncNode>> GetAsyncGraph(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        [Description("Maximum number of nodes to return in this page (1-1000).")] int max = Paging.DefaultMax,
        [Description("Number of nodes to skip (use the previous page's nextOffset).")] int offset = 0,
        CancellationToken cancellationToken = default)
        => OnStoppedTarget(sessions, sessionId, cancellationToken,
            (pid, stopId) => inspector.BuildAsyncGraph(pid, stopId, offset, max, cancellationToken));

    [McpServerTool(Name = "get_async_callstack", Title = "Get the logical async call stack", ReadOnly = true, OpenWorld = false)]
    [Description("Follow continuation links from a starting Task address (from list_tasks) to produce the logical async call stack — the chain of async methods that resume one after another.")]
    public static Task<IReadOnlyList<AsyncNode>> GetAsyncCallstack(
        SessionManager sessions,
        TaskInspector inspector,
        [Description("Starting Task heap address (hex 0x... or decimal).")] string address,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ulong addr = ParseAddress(address);
        return OnStoppedTarget(sessions, sessionId, cancellationToken,
            (pid, stopId) => inspector.FollowContinuations(pid, stopId, addr, cancellationToken));
    }

    // The heap belongs to one stop of one process: capture (pid, stopId) while the session is
    // verified stopped, then do the (slow, blocking) heap read on the thread pool.
    static Task<T> OnStoppedTarget<T>(
        SessionManager sessions, string? sessionId, CancellationToken ct, Func<int, long, T> read)
    {
        var s = sessions.Resolve(sessionId);
        s.EnsureStopped();
        int pid = s.Pid;
        long stopId = s.StopId;
        return Task.Run(() => read(pid, stopId), ct);
    }

    internal static ulong ParseAddress(string address)
    {
        bool hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        bool ok = hex
            ? ulong.TryParse(address.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value)
            : ulong.TryParse(address, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return ok
            ? value
            : throw new ArgumentException($"'{address}' is not a heap address: pass hex (0x1c3...) or decimal, as returned by list_tasks.");
    }
}
