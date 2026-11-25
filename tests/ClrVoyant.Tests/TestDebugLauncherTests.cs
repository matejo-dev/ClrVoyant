using System.Diagnostics;
using System.Text.RegularExpressions;
using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

/// <summary>
/// debug_test: how the launcher reacts to what the <c>dotnet test</c> driver does. The driver is
/// replaced, through the launcher's start-info seam, by small real processes: the sample debuggee,
/// which can exit with a chosen code, announce itself the way a suspended test host does, or just
/// stay alive and quiet; and ParentApp, which starts a child of its own (a process tree).
/// </summary>
public class TestDebugLauncherTests
{
    // ---- the banner ----

    [Theory]
    [InlineData("Process Id: 12345, Name: testhost", 12345)]
    [InlineData("process id:7", 7)]
    [InlineData("Process Id:\t55 (Name: x)", 55)]
    [InlineData("  Process Id: 1, Name: dotnet", 1)]
    public void The_banner_carries_the_pid_of_the_test_host(string line, int pid)
    {
        Assert.Equal(pid, TestDebugLauncher.ParseTestHostPid(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Starting test execution, please wait...")]
    [InlineData("Process Id: abc")]
    [InlineData("Process Id: 99999999999999")] // does not fit a pid
    [InlineData("Id: 12")]
    public void Any_other_line_is_not_the_banner(string? line)
    {
        Assert.Null(TestDebugLauncher.ParseTestHostPid(line));
    }

    // ---- the command ----

    [Fact]
    public void The_default_command_is_dotnet_test_on_the_project_with_the_filter_and_configuration()
    {
        var psi = TestDebugLauncher.DotnetTest("/p/T.csproj", "/p", "FullyQualifiedName~Foo", "Release");

        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal(["test", "/p/T.csproj", "-c", "Release", "--filter", "FullyQualifiedName~Foo"], psi.ArgumentList);
        Assert.Equal("/p", psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_filter_the_whole_project_is_run(string? filter)
    {
        var psi = TestDebugLauncher.DotnetTest("/p/T.csproj", "/p", filter, "Debug");

        Assert.Equal(["test", "/p/T.csproj", "-c", "Debug"], psi.ArgumentList);
    }

    // ---- fixtures ----

    sealed class Rig
    {
        public FakeEngineFactory Engines { get; } = new();
        public SessionManager Manager { get; }
        public TestDebugLauncher Launcher { get; }
        public string Marker { get; } = $"marker-{Guid.NewGuid():N}";

        public Rig(string app, Dictionary<string, string>? env = null, string[]? appArgs = null)
        {
            Manager = new SessionManager(Engines.Create);
            Launcher = new TestDebugLauncher(Manager, (_, _, _, _) =>
            {
                var psi = new ProcessStartInfo("dotnet")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add(app);
                foreach (var a in appArgs ?? []) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add(Marker); // makes this run's processes recognisable by command line
                foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
                return psi;
            });
        }

        public Task<SessionInfo> RunAsync(TimeSpan timeout, CancellationToken ct = default)
            => Launcher.LaunchAndAttachAsync("irrelevant.csproj", null, "Debug", timeout, ct);
    }

    static int PidIn(string text) => int.Parse(Regex.Match(text, @"SampleApp PID=(\d+)").Groups[1].Value);

    /// <summary>Wait (bounded) for the process to be gone. An already-gone process counts.</summary>
    static void AssertGone(int pid, string what)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (p)
            Assert.True(p.WaitForExit(20000), $"{what} (pid {pid}) is still running");
    }

    // ---- the driver ends before the banner: fail at once, with the reason ----

    [Theory]
    [InlineData("3")]
    [InlineData("0")] // "no test matched" ends cleanly, and is as useless to wait for
    public async Task A_driver_that_exits_before_the_banner_fails_the_call_at_once_with_its_exit_code_and_output(string exitCode)
    {
        var rig = new Rig(TestPaths.SampleAppDll, new() { ["SAMPLE_EXIT_CODE"] = exitCode });
        var started = Stopwatch.StartNew();

        // A generous timeout: only reacting to the exit can end this call early.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync(TimeSpan.FromMinutes(2)));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(60), $"took {started.Elapsed}");
        Assert.Contains($"exited with code {exitCode}", ex.Message);
        Assert.Contains("SampleApp PID=", ex.Message); // what the driver printed
        Assert.Empty(rig.Manager.List());
    }

    // ---- nothing happens: time out, with what was printed so far ----

    [Fact]
    public async Task A_driver_that_neither_announces_nor_exits_times_out_with_its_output_and_is_killed()
    {
        var rig = new Rig(TestPaths.SampleAppDll);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => rig.RunAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("did not announce a test-host PID within 5s", ex.Message);
        Assert.Contains("SampleApp PID=", ex.Message);
        AssertGone(PidIn(ex.Message), "the driver");
        Assert.Empty(rig.Manager.List());
    }

    // ---- the caller cancels: the whole tree goes ----

    [Fact]
    public async Task Cancelling_the_call_ends_it_as_a_cancellation_and_kills_the_driver_and_what_it_started()
    {
        var rig = new Rig(TestPaths.ParentAppDll, appArgs: [TestPaths.SampleAppDll]);
        using var cts = new CancellationTokenSource();
        var call = rig.RunAsync(TimeSpan.FromMinutes(2), cts.Token);

        // The driver (ParentApp) has started its child (SampleApp): a tree of two processes.
        var (parent, child) = await WaitForTree(rig.Marker);
        using var parentProc = Process.GetProcessById(parent);
        using var childProc = Process.GetProcessById(child);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(parentProc.WaitForExit(20000), "the driver survived the cancellation");
        Assert.True(childProc.WaitForExit(20000), "what the driver started survived the cancellation");
        Assert.Empty(rig.Manager.List());
    }

    /// <summary>The ParentApp process carrying <paramref name="marker"/> and its SampleApp child, once both
    /// exist. Waiting for another process to start has no event to wait on: this polls, bounded.</summary>
    static async Task<(int parent, int child)> WaitForTree(string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var table = ProcessLister.Enumerate(includeCommandLine: true).ToList();
            var parent = table.FirstOrDefault(p => p.CommandLine?.Contains(marker, StringComparison.Ordinal) == true);
            if (parent.Pid != 0
                && table.FirstOrDefault(p => p.ParentPid == parent.Pid && p.CommandLine?.Contains("SampleApp.dll", StringComparison.Ordinal) == true) is { Pid: not 0 } child)
                return (parent.Pid, child.Pid);
            await Task.Delay(100);
        }
        throw new TimeoutException("The process tree never came up.");
    }

    // ---- the banner appears: attach, and tie the driver to the session ----

    [Fact]
    public async Task The_announced_test_host_is_attached_and_ends_with_its_session()
    {
        var rig = new Rig(TestPaths.SampleAppDll, new() { ["SAMPLE_ANNOUNCE_TESTHOST"] = "1" });

        var info = await rig.RunAsync(TimeSpan.FromMinutes(2));

        // The session is on the pid the banner named (the fake engine records what it was asked to attach to)...
        Assert.Equal(info.Pid, rig.Engines.Engines.Single().Pid);
        var session = rig.Manager.Resolve(info.SessionId);
        Assert.NotNull(session.OwnedResource);
        Assert.True(session.OwnsDebuggee);
        using var driver = Process.GetProcessById(info.Pid);
        Assert.False(driver.HasExited);

        // ...and ending the session ends the process that was started for it.
        await rig.Manager.StopAsync(info.SessionId);
        Assert.True(driver.WaitForExit(20000), "the driver survived debug_stop");
    }

    [Fact]
    public async Task A_failed_attach_kills_the_driver_instead_of_leaving_it_waiting_for_a_debugger()
    {
        var rig = new Rig(TestPaths.SampleAppDll, new() { ["SAMPLE_ANNOUNCE_TESTHOST"] = "1" });
        rig.Engines.Customize = e => e.AttachFailure = new InvalidOperationException("attach denied");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunAsync(TimeSpan.FromMinutes(2)));

        Assert.Equal("attach denied", ex.Message);
        // The engine got the announced pid before it failed.
        AssertGone(rig.Engines.Engines.Single().Pid, "the driver");
        Assert.Empty(rig.Manager.List());
    }
}
