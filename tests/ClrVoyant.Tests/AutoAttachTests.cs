using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

public class AutoAttachTests
{
    [Fact]
    public async Task Watcher_is_noop_when_disabled()
    {
        var mgr = new SessionManager(() => new FakeDebugEngine());
        await mgr.LaunchAsync(new LaunchRequest("a.dll"));
        var watcher = new ChildProcessWatcher(mgr, new AutoAttachOptions { Enabled = false });
        Assert.Equal(0, await watcher.ScanOnceAsync());
    }

    [Fact]
    public async Task Watcher_is_noop_when_no_sessions()
    {
        var mgr = new SessionManager(() => new FakeDebugEngine());
        var watcher = new ChildProcessWatcher(mgr, new AutoAttachOptions { Enabled = true });
        Assert.Equal(0, await watcher.ScanOnceAsync());
    }
}
