using System.ComponentModel;
using ClrVoyant.Core;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server.Tools;

/// <summary>
/// Session lifecycle: start, attach, restart and end debug sessions, and read their state and
/// output. Every session-scoped tool in this server accepts an optional sessionId; when omitted
/// it targets the active session (the only one, or the most-recently-stopped).
/// </summary>
[McpServerToolType]
public static class LifecycleTools
{
    [McpServerTool(Name = "get_debug_instructions", Title = "Get debugging playbook", ReadOnly = true, OpenWorld = false)]
    [Description("Read this FIRST when you are about to debug a .NET app. Returns a concise playbook: the typical loop, which tools to call in what order, and the gotchas that trip agents up. Costs nothing to call and prevents wrong tool sequencing. (The same text is sent as the server's instructions at connect time; this tool is for clients that do not show those.)")]
    public static string GetDebugInstructions() => DebugInstructions.Text;

    [McpServerTool(Name = "debug_launch", Title = "Launch a .NET program under the debugger", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Launch a built .NET (.dll) under the debugger and start a new debug session. Build the project first; pass the path to the output assembly. Optionally set environment variables for the debuggee with 'env'. Returns the new session's id, pid and state.")]
    public static Task<SessionInfo> DebugLaunch(
        SessionManager sessions,
        [Description("Full path to the built .dll to launch.")] string program,
        [Description("Command-line arguments for the program.")] string[]? args = null,
        [Description("Working directory; defaults to the program's folder.")] string? cwd = null,
        [Description("Break at program entry instead of running freely.")] bool stopAtEntry = false,
        [Description("Environment variables to set for the debuggee, as a name -> value map (e.g. {\"ASPNETCORE_ENVIRONMENT\": \"Development\"}). Added to, and overriding, the environment inherited from the server. Kept across restart_debugging.")] Dictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
        => sessions.LaunchAsync(new LaunchRequest(program, args, cwd, stopAtEntry, env), cancellationToken);

    [McpServerTool(Name = "debug_attach", Title = "Attach the debugger to a running process", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Attach the debugger to an already-running .NET process by PID, creating a new debug session. Use this for secondary/child processes you discover.")]
    public static Task<SessionInfo> DebugAttach(
        SessionManager sessions,
        [Description("OS process id to attach to.")] int pid,
        CancellationToken cancellationToken = default)
        => sessions.AttachAsync(pid, cancellationToken);

    [McpServerTool(Name = "list_processes", Title = "List running processes", ReadOnly = true, OpenWorld = false)]
    [Description("List running processes (pid, parent pid, name, isDotNet, commandLine) so you can pick one to debug_attach: most .NET targets are just named 'dotnet', so use the command line to tell them apart. Defaults to .NET processes only. Useful in a shared-PID-namespace POD to find the app process to debug. NOTE: command lines are returned as the OS reports them (cut at 1000 characters) and can contain secrets such as passwords or tokens passed as arguments; absent when the OS does not let the server read it.")]
    public static IReadOnlyList<ProcessInfo> ListProcesses(
        [Description("List only .NET (Core) processes. Set false to list every process.")] bool dotnetOnly = true)
        => ProcessLister.List(dotnetOnly);

    [McpServerTool(Name = "debug_test", Title = "Debug a unit test", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Debug a unit test in one step: runs 'dotnet test' on the project with the test host suspended, then attaches a session to it. Set breakpoints right after this returns — execution resumes and hits them. Build the test project with a PDB (Debug is safest for full locals). The 'dotnet test' process is killed when you debug_stop the session.")]
    public static Task<SessionInfo> DebugTest(
        TestDebugLauncher launcher,
        [Description("Path to the test project (.csproj) or its directory.")] string testProject,
        [Description("Optional test filter (passed to 'dotnet test --filter'), e.g. a test or class name. Omit to debug the whole project.")] string? testName = null,
        [Description("Build configuration; Debug is safest for full locals (what's actually required is a PDB).")] string configuration = "Debug",
        [Description("Max time to wait for the test host to come up, milliseconds.")] int timeoutMs = 120000,
        CancellationToken cancellationToken = default)
        => launcher.LaunchAndAttachAsync(testProject, testName, configuration, TimeSpan.FromMilliseconds(timeoutMs), cancellationToken);

    [McpServerTool(Name = "set_auto_attach", Title = "Toggle child-process auto-attach", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Enable/disable automatic attaching to .NET child processes spawned by debugged processes (matched by parent PID). Off by default. When on, children appear as new sessions shortly after they start; use wait_for_any_stop and list_sessions to drive them. Note: a child's very first startup instants may run before attach.")]
    public static string SetAutoAttach(
        AutoAttachOptions options,
        [Description("True to enable child-process auto-discovery.")] bool enabled)
    {
        options.Enabled = enabled;
        return enabled ? "auto-attach enabled" : "auto-attach disabled";
    }

    [McpServerTool(Name = "debug_status", Title = "Get a debug session's state", ReadOnly = true, OpenWorld = false)]
    [Description("Get the current state of a debug session (starting/running/stopped/exited) and, when stopped, the current location.")]
    public static SessionInfo DebugStatus(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null)
        => sessions.Resolve(sessionId).ToInfo();

    [McpServerTool(Name = "debug_stop", Title = "End a debug session", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("End the debug session. By default the debuggee is KILLED if this server started it (debug_launch, the test host behind debug_test, or a child process auto-attached from such a session), but only DETACHED from — left running — if the session was created by debug_attach (including a child auto-attached from an attached session). Pass 'terminate' to force either: true kills the process even if you only attached to it; false detaches and leaves it running (for a debug_test session the 'dotnet test' run is always ended with the session, so false cannot keep it alive; for a debug_launch session, a process whose console output goes through the debugger may fail when it next writes). Returns 'stopped'.")]
    public static async Task<string> DebugStop(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        [Description("true = kill the debuggee; false = detach and leave it running; omit for the default described above.")] bool? terminate = null,
        CancellationToken cancellationToken = default)
    {
        await sessions.StopAsync(sessionId, terminate, cancellationToken);
        return "stopped";
    }

    [McpServerTool(Name = "get_output", Title = "Get the debuggee's captured output", ReadOnly = true, OpenWorld = false)]
    [Description("Get the captured stdout/stderr of the debuggee (the most recent part, oldest dropped past a fixed cap), plus the session state and the exit code if it has exited. Use it to see what a crashed or finished process printed. Works in any state; output of a debug_attach'ed process is captured only if the debugger relays it.")]
    public static SessionOutput GetOutput(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        [Description("Maximum number of characters to return (the most recent ones).")] int maxChars = 10000)
        => sessions.Resolve(sessionId).GetOutput(maxChars);

    [McpServerTool(Name = "restart_debugging", Title = "Restart a launched session", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Restart a launched session in place: terminate the debuggee and relaunch it with the SAME program/args, keeping the same session id and all breakpoints. Only works for sessions started with debug_launch (not debug_attach). Returns the restarted session's info.")]
    public static Task<SessionInfo> RestartDebugging(
        SessionManager sessions,
        [Description("Session id; omit for the active session.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
        => sessions.RestartAsync(sessionId, cancellationToken);

    [McpServerTool(Name = "list_sessions", Title = "List debug sessions", ReadOnly = true, OpenWorld = false)]
    [Description("List all debug sessions with their pid, state and last stop.")]
    public static IReadOnlyList<SessionInfo> ListSessions(SessionManager sessions)
        => sessions.List();
}
