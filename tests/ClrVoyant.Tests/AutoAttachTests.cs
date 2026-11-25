using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class AutoAttachTests
{
    const int ParentPid = 100;

    /// <summary>A manager over fake engines, and a watcher whose "process table" is
    /// <see cref="Rig.Processes"/> (pid, parent pid) and whose .NET check is <see cref="Rig.DotNet"/>.</summary>
    sealed class Rig
    {
        public List<FakeDebugEngine> Engines { get; } = new();
        public List<(int pid, int parentPid)> Processes { get; } = new();
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
            Watcher = new ChildProcessWatcher(Manager, new AutoAttachOptions { Enabled = enabled },
                enumerate: () => Processes.ToList(), isDotNet: DotNet.Contains);
        }
    }

    [Fact]
    public async Task A_dotnet_child_of_a_debugged_process_is_attached_as_a_new_session()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add((200, ParentPid));
        rig.DotNet.Add(200);

        Assert.Equal(1, await rig.Watcher.ScanOnceAsync());

        Assert.Contains(rig.Manager.List(), s => s.Pid == 200);
    }

    [Fact]
    public async Task A_child_that_is_already_a_session_is_not_attached_again()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add((200, ParentPid));
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
        rig.Processes.Add((200, ParentPid)); // child, but not .NET
        rig.Processes.Add((300, 999));       // .NET, but unrelated
        rig.DotNet.Add(300);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Single(rig.Manager.List());
    }

    [Fact]
    public async Task A_disabled_watcher_attaches_nothing()
    {
        var rig = new Rig(enabled: false);
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add((200, ParentPid));
        rig.DotNet.Add(200);

        Assert.Equal(0, await rig.Watcher.ScanOnceAsync());

        Assert.Single(rig.Engines); // no engine was even created for the child
    }

    [Fact]
    public async Task A_failing_attach_does_not_stop_the_scan_and_is_retried_on_the_next_pass()
    {
        var rig = new Rig();
        await rig.Manager.LaunchAsync(new LaunchRequest("a.dll"));
        rig.Processes.Add((200, ParentPid));
        rig.Processes.Add((201, ParentPid));
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
        rig.Processes.Add((200, ParentPid));
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
        rig.Processes.Add((200, ParentPid));
        rig.Processes.Add((300, 200));
        rig.DotNet.Add(200);
        rig.DotNet.Add(300);
        await rig.Watcher.ScanOnceAsync(); // child
        await rig.Watcher.ScanOnceAsync(); // grandchild, found through the child

        var grandchild = rig.Manager.List().Single(s => s.Pid == 300);
        await rig.Manager.StopAsync(grandchild.SessionId);

        Assert.True(rig.Engines[^1].LastTerminateDebuggee);
    }
}
