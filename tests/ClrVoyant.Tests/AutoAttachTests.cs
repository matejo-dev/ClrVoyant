using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class AutoAttachTests
{
    const int ParentPid = 100;
    const long ParentStart = 1000;
    const long LaterStart = 2000; // a child created after the debugged process

    /// <summary>A manager over fake engines, and a watcher whose "process table" is
    /// <see cref="Rig.Processes"/> (pid, parent pid, creation stamp) and whose .NET check is <see cref="Rig.DotNet"/>.</summary>
    sealed class Rig
    {
        public List<FakeDebugEngine> Engines { get; } = new();
        public List<ProcessEntry> Processes { get; } = new();
        public HashSet<int> DotNet { get; } = new();
        public Exception? AttachFailure { get; set; }
        public SessionManager Manager { get; }
        public ChildProcessWatcher Watcher { get; }

        public Rig(bool enabled = true)
        {
            Manager = new SessionManager(() =>
            {
                var f = new FakeDebugEngine { Pid = ParentPid, AttachFailure = AttachFailure };
                Engines.Add(f);
                return f;
            });
            Processes.Add(new ProcessEntry(ParentPid, 1, ParentStart)); // the debugged process itself
            Watcher = new ChildProcessWatcher(Manager, new AutoAttachOptions { Enabled = enabled }, null,
                enumerate: () => Processes.ToList(), isDotNet: DotNet.Contains);
        }
    }

    [Fact]
    public async Task A_dotnet_child_of_a_debugged_process_is_attached_as_a_new_session()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());

        Assert.Contains(rig.Manager.List(), s => s.Pid == 200);
    }

    [Fact]
    public async Task A_child_that_is_already_a_session_is_not_attached_again()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        await rig.Watcher.ScanOnceAsync();

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Equal(2, rig.Manager.List().Count);
    }

    [Fact]
    public async Task Processes_that_are_not_dotnet_or_not_children_of_a_debugged_process_are_left_alone()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart)); // child, but not .NET
        rig.Processes.Add(new ProcessEntry(300, 999, LaterStart));       // .NET, but unrelated
        rig.DotNet.Add(300);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Single(rig.Manager.List());
    }

    [Fact]
    public async Task A_disabled_watcher_attaches_nothing()
    {
        var rig = new Rig(enabled: false);
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Single(rig.Engines); // no engine was even created for the child
    }

    [Fact]
    public async Task A_failing_attach_does_not_stop_the_scan_and_is_retried_on_the_next_pass()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.Processes.Add(new ProcessEntry(201, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        rig.DotNet.Add(201);
        rig.AttachFailure = new InvalidOperationException("denied");
        var failing = rig.Engines.Count; // engines created from here on fail to attach

        // 200 fails, then 201 (also failing) is still tried: the scan went on.
        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());
        Assert.Equal(failing + 2, rig.Engines.Count);
        Assert.Single(rig.Manager.List()); // no ghost sessions for the failed attaches

        rig.AttachFailure = null;
        Assert.Equal(2, await rig.Watcher.ScanOnceAsync()); // both retried, both attached
        Assert.Equal(3, rig.Manager.List().Count);
    }

    // ---- ownership: a child belongs to the process tree of the session it came from ----

    static async Task<FakeDebugEngine> StopChildOf(Rig rig, string parentSessionId)
    {
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        await rig.Watcher.ScanOnceAsync();
        var child = rig.Manager.List().Single(s => s.Pid == 200);
        await rig.Manager.StopAsync(child.SessionId);
        return rig.Engines[^1];
    }

    [Fact]
    public async Task A_child_of_a_launched_session_is_terminated_with_its_session()
    {
        var rig = new Rig();
        var parent = await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));

        var childEngine = await StopChildOf(rig, parent.SessionId);

        Assert.True(childEngine.LastTerminateDebuggee);
    }

    [Fact]
    public async Task A_child_of_an_attached_session_is_only_detached()
    {
        var rig = new Rig();
        var parent = await rig.Manager.AttachAsync(ParentPid);

        var childEngine = await StopChildOf(rig, parent.SessionId);

        Assert.False(childEngine.LastTerminateDebuggee);
    }

    [Fact]
    public async Task Ownership_passes_down_to_grandchildren()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.Processes.Add(new ProcessEntry(300, 200, LaterStart));
        rig.DotNet.Add(200);
        rig.DotNet.Add(300);
        await rig.Watcher.ScanOnceAsync(); // child
        await rig.Watcher.ScanOnceAsync(); // grandchild, found through the child

        var grandchild = rig.Manager.List().Single(s => s.Pid == 300);
        await rig.Manager.StopAsync(grandchild.SessionId);

        Assert.True(rig.Engines[^1].LastTerminateDebuggee);
    }

    // ---- parentage: a matching parent PID is not enough (PIDs are recycled) ----

    [Fact]
    public async Task A_process_older_than_its_supposed_parent_is_a_leftover_of_a_recycled_pid_and_is_never_attached()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        // Created BEFORE the debugged process, yet it names its pid as parent: the pid used to
        // belong to another process, which is the real parent of this one.
        rig.Processes.Add(new ProcessEntry(200, ParentPid, ParentStart - 1));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Single(rig.Manager.List());
        Assert.Single(rig.Engines); // not even an engine was spun up for it
    }

    [Fact]
    public async Task Only_the_younger_of_two_processes_naming_the_same_parent_is_attached()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, ParentStart - 500)); // stale
        rig.Processes.Add(new ProcessEntry(201, ParentPid, ParentStart + 500)); // genuine
        rig.DotNet.Add(200);
        rig.DotNet.Add(201);

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());

        Assert.Equal([ParentPid, 201], rig.Manager.List().Select(s => s.Pid).Order());
    }

    [Theory]
    [InlineData(0L, LaterStart)]   // parent's creation time unknown
    [InlineData(ParentStart, 0L)]  // child's creation time unknown
    public async Task A_parentage_that_cannot_be_shown_is_not_assumed(long parentStart, long childStart)
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes[0] = new ProcessEntry(ParentPid, 1, parentStart);
        rig.Processes.Add(new ProcessEntry(200, ParentPid, childStart));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());
    }

    [Fact]
    public async Task A_parent_that_is_not_in_the_process_table_has_no_children()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.RemoveAt(0);
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());
    }

    [Fact]
    public async Task A_session_whose_process_has_ended_does_not_lend_its_pid_to_a_recycled_one()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Engines[0].RaiseExited(0);
        // A new, unrelated process now holds the pid, and has a child of its own.
        rig.Processes[0] = new ProcessEntry(ParentPid, 1, ParentStart + 100);
        rig.Processes.Add(new ProcessEntry(200, ParentPid, ParentStart + 200));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());
    }

    // ---- retry budget: a child that cannot be attached must not cost a debugger spawn per scan ----

    static async Task<int> ScansUntilQuiet(Rig rig, int maxScans = 10)
    {
        int engines = rig.Engines.Count;
        for (int i = 0; i < maxScans; i++)
        {
            await rig.Watcher.ScanOnceAsync();
            if (rig.Engines.Count == engines) return rig.Engines.Count;
            engines = rig.Engines.Count;
        }
        return rig.Engines.Count;
    }

    [Fact]
    public async Task A_child_that_keeps_failing_to_attach_is_given_up_after_a_few_attempts()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        rig.AttachFailure = new InvalidOperationException("owned by another debugger");
        int before = rig.Engines.Count;

        int after = await ScansUntilQuiet(rig);

        Assert.Equal(ChildProcessWatcher.MaxAttachAttempts, after - before);
    }

    [Fact]
    public async Task A_new_process_with_the_same_pid_gets_a_fresh_attach_budget()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        rig.AttachFailure = new InvalidOperationException("denied");
        await ScansUntilQuiet(rig);

        // Same pid, different creation time: another process. It is attachable.
        rig.Processes[^1] = new ProcessEntry(200, ParentPid, LaterStart + 1);
        rig.AttachFailure = null;

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());
    }

    [Fact]
    public async Task A_child_that_fails_and_then_succeeds_within_the_budget_is_attached()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        rig.AttachFailure = new InvalidOperationException("not ready");
        for (int i = 0; i < ChildProcessWatcher.MaxAttachAttempts - 1; i++) await rig.Watcher.ScanOnceAsync();
        rig.AttachFailure = null;

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());
    }

    [Fact]
    public async Task The_failure_budget_does_not_outlive_the_process_that_used_it()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add(new ProcessEntry(200, ParentPid, LaterStart));
        rig.DotNet.Add(200);
        rig.AttachFailure = new InvalidOperationException("denied");
        await ScansUntilQuiet(rig);

        // The process exits (a scan notices), then the very same identity reappears: the budget is fresh.
        var gone = rig.Processes[^1];
        rig.Processes.RemoveAt(rig.Processes.Count - 1);
        await rig.Watcher.ScanOnceAsync();
        rig.Processes.Add(gone);
        rig.AttachFailure = null;

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());
    }
}
