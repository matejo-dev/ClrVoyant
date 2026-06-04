using ClrVoyant.Server;

namespace ClrVoyant.Tests;

public class ProcessListerTests
{
    [Fact]
    public void Lists_self_as_a_dotnet_process()
    {
        // The test host is a .NET process, so it must be discoverable and flagged .NET.
        var procs = ProcessLister.List(dotnetOnly: true);
        var me = procs.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Assert.NotNull(me);
        Assert.True(me!.IsDotNet);
        Assert.False(string.IsNullOrEmpty(me.Name));
    }

    [Fact]
    public void DotnetOnly_filter_is_a_subset_of_all()
    {
        var all = ProcessLister.List(dotnetOnly: false);
        var net = ProcessLister.List(dotnetOnly: true);
        Assert.True(net.Count <= all.Count);
        Assert.All(net, p => Assert.True(p.IsDotNet));
    }
}
