using System.Diagnostics;
using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Real netcoredbg: the debuggee environment, its output and exit code, and the
/// kill-versus-detach policy when a session ends.
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationLifecycleTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    static SessionManager NewManager() => new(() => new DapEngine(TestPaths.Netcoredbg));

    static async Task CleanupAsync(SessionManager mgr)
    {
        foreach (var s in mgr.List())
        {
            try { await mgr.StopAsync(s.SessionId); } catch { }
        }
        await mgr.DisposeAsync();
    }

    const string EnvName = "CLRVOYANT_TEST_VALUE";
    const string EvalEnv = "System.Environment.GetEnvironmentVariable(\"" + EnvName + "\")";

    static async Task<string> EvaluateAtTopFrameAsync(SessionManager mgr, string expression)
    {
        var stack = await DebugTools.GetCallstack(mgr, null, 0, 1);
        var result = await DebugTools.Evaluate(mgr, expression, stack[0].Id);
        return result.Result;
    }

    [Fact]
    public async Task Launch_environment_reaches_the_debuggee_and_survives_restart()
    {
        var mgr = NewManager();
        try
        {
            await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll,
                env: new Dictionary<string, string> { [EnvName] = "hello-env" });
            await DebugTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
            var stop = await DebugTools.Continue(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, stop.State);
            Assert.Contains("hello-env", await EvaluateAtTopFrameAsync(mgr, EvalEnv));

            await DebugTools.RestartDebugging(mgr);
            stop = await DebugTools.Continue(mgr, null, 20000);
            Assert.Equal(SessionState.Stopped, stop.State);
            Assert.Contains("hello-env", await EvaluateAtTopFrameAsync(mgr, EvalEnv));
        }
        finally { await CleanupAsync(mgr); }
    }

    [Fact]
    public async Task Exit_code_and_stdout_of_a_finished_debuggee_are_reported()
    {
        var mgr = NewManager();
        try
        {
            // SampleApp prints its PID line and then exits with SAMPLE_EXIT_CODE when set.
            await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll, stopAtEntry: true,
                env: new Dictionary<string, string> { ["SAMPLE_EXIT_CODE"] = "7" });
            if (DebugTools.DebugStatus(mgr).State != SessionState.Stopped)
                await DebugTools.WaitForAnyStop(mgr, 10000);

            var outcome = await DebugTools.Continue(mgr, null, 20000);

            Assert.Equal(SessionState.Exited, outcome.State);
            Assert.Equal(7, outcome.ExitCode);
            Assert.Equal(7, DebugTools.DebugStatus(mgr).ExitCode);
            Assert.Contains("SampleApp PID=", DebugTools.GetOutput(mgr).Text);
        }
        finally { await CleanupAsync(mgr); }
    }

    // ---- kill vs detach ----

    static Process StartExternalSample()
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add(TestPaths.SampleAppDll);
        return Process.Start(psi)!;
    }

    // Reads the sample's stdout until a 'tick' line shows up (proof it is still running).
    static async Task<string> NextTickAsync(Process p)
    {
        using var cts = new CancellationTokenSource(Bound);
        while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
            if (line.StartsWith("tick", StringComparison.Ordinal)) return line;
        throw new InvalidOperationException("the process closed its stdout");
    }

    [Fact]
    public async Task Stopping_an_attached_session_leaves_the_process_running()
    {
        var proc = StartExternalSample();
        var mgr = NewManager();
        try
        {
            var info = await DebugTools.DebugAttach(mgr, proc.Id);
            await DebugTools.DebugStop(mgr, info.SessionId);

            // Still alive, and still doing its work after the debugger is gone.
            Assert.False(proc.HasExited);
            await NextTickAsync(proc);
            await NextTickAsync(proc);
            Assert.False(proc.HasExited);
        }
        finally
        {
            await CleanupAsync(mgr);
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Stopping_an_attached_session_with_terminate_true_kills_the_process()
    {
        var proc = StartExternalSample();
        var mgr = NewManager();
        try
        {
            var info = await DebugTools.DebugAttach(mgr, proc.Id);
            await DebugTools.DebugStop(mgr, info.SessionId, terminate: true);

            Assert.True(proc.WaitForExit((int)Bound.TotalMilliseconds));
        }
        finally
        {
            await CleanupAsync(mgr);
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            proc.Dispose();
        }
    }

    [Fact]
    public async Task Stopping_a_launched_session_kills_the_debuggee()
    {
        var mgr = NewManager();
        try
        {
            var info = await DebugTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
            using var debuggee = Process.GetProcessById(info.Pid);

            await DebugTools.DebugStop(mgr, info.SessionId);

            Assert.True(debuggee.WaitForExit((int)Bound.TotalMilliseconds));
        }
        finally { await CleanupAsync(mgr); }
    }
}
