using System.ComponentModel;
using ClrVoyant.Core;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>
/// Execution control. The resume tools are wait-based (ADR-0004): they block until the next stop,
/// the exit or the timeout. A client cancelling the call releases the wait and leaves the session
/// usable — the next stop is then reported as spontaneous (see wait_for_any_stop).
/// </summary>
[McpServerToolType]
public static class ExecutionTools
{
    [McpServerTool(Name = "continue", Title = "Resume and wait for the next stop", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Resume execution and BLOCK until the next stop (breakpoint/exception), the process exits, or the timeout. Returns the resulting state and location.")]
    public static Task<StopOutcome> Continue(
        SessionManager sessions,
        [Description("Thread to resume; omit for the last stopped thread.")] int? threadId = null,
        [Description("Max time to wait for the next stop, milliseconds.")] int timeoutMs = 30000,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).ContinueAsync(threadId, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    [McpServerTool(Name = "step_over", Title = "Step over", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Step over the current line (run called methods without stepping in) and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepOver(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).StepAsync(StepKind.Over, threadId, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    [McpServerTool(Name = "step_into", Title = "Step into", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Step into the call on the current line and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepInto(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).StepAsync(StepKind.Into, threadId, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    [McpServerTool(Name = "step_out", Title = "Step out", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Step out of the current method and block until stopped. Returns the new location.")]
    public static Task<StopOutcome> StepOut(SessionManager sessions, int? threadId = null, int timeoutMs = 30000, string? sessionId = null, CancellationToken cancellationToken = default)
        => sessions.Resolve(sessionId).StepAsync(StepKind.Out, threadId, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    [McpServerTool(Name = "pause", Title = "Pause a running target", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Break into a running target at its current point of execution.")]
    public static async Task<string> Pause(
        SessionManager sessions, int? threadId = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        await sessions.Resolve(sessionId).PauseAsync(threadId, cancellationToken);
        return "pausing";
    }

    // Not read-only: a stop that is reported here is consumed, so asking again does not return it again.
    [McpServerTool(Name = "wait_for_any_stop", Title = "Wait for any session to stop", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Block until ANY session hits a stop (breakpoint, step, exception) or its process exits (or its debugger connection is lost), then report which one and its state; events that are no longer true are skipped. Essential when several processes run asynchronously.")]
    public static async Task<object> WaitForAnyStop(
        SessionManager sessions,
        [Description("Max time to wait, milliseconds.")] int timeoutMs = 30000,
        CancellationToken cancellationToken = default)
    {
        var stop = await sessions.WaitForAnyStopAsync(TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);
        return stop is null ? new { timedOut = true } : stop;
    }
}
