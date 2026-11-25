using System.Diagnostics;
using ClrVoyant.Server;
using ModelContextProtocol.Client;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// What is left behind when the MCP client kills the server instead of closing it (a crashed or
/// force-quit client). Nothing in this code base handles it: it holds because netcoredbg, reading
/// its debug protocol from the server's pipe, exits when the pipe closes and takes the debuggee it
/// launched with it. That is the behaviour of a third-party binary, so it is pinned here, against the
/// real server and the real engine, instead of being assumed: a netcoredbg update that stopped doing
/// it would leave orphaned debuggees (still holding ports and files) behind every killed client.
/// A process the server merely attached to is not the server's to kill, and must survive.
/// </summary>
[Trait("Category", "Integration")]
public class ServerKilledTests
{
    const int BoundMs = 30_000;

    static async Task<McpClient> StartServerAsync(CancellationToken ct)
        => await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "killed", Command = "dotnet", Arguments = [TestPaths.ServerDll] }),
            cancellationToken: ct);

    /// <summary>The engine the server just started: the netcoredbg that lives in the server's own build
    /// output (not the one the other tests use), and its parent, the server. Both exist as soon as a
    /// session has been started, and there is exactly one of each because tests run one at a time.</summary>
    static (Process engine, Process server, ProcessEntry engineEntry) FindEngineAndServer()
    {
        string serverDir = Path.GetDirectoryName(TestPaths.ServerDll)!;
        var table = ProcessLister.Enumerate(includeCommandLine: true).ToList();
        var engineEntry = table.Single(p => p.Name?.StartsWith("netcoredbg", StringComparison.OrdinalIgnoreCase) == true
            && p.CommandLine?.Contains(serverDir, StringComparison.Ordinal) == true);
        return (Process.GetProcessById(engineEntry.Pid), Process.GetProcessById(engineEntry.ParentPid), engineEntry);
    }

    [Fact]
    public async Task Killing_the_server_ends_netcoredbg_and_the_debuggee_it_launched()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var client = await StartServerAsync(cts.Token);
        var launch = await client.CallToolAsync("debug_launch",
            new Dictionary<string, object?> { ["program"] = TestPaths.SampleAppDll }, cancellationToken: cts.Token);
        Assert.True(launch.IsError != true);

        var (engine, server, engineEntry) = FindEngineAndServer();
        using var _e = engine;
        using var _s = server;
        // The debuggee is netcoredbg's child.
        using var debuggee = Process.GetProcessById(
            ProcessLister.Enumerate().Single(p => p.ParentPid == engineEntry.Pid).Pid);

        server.Kill(); // hard: no shutdown handlers run
        Assert.True(server.WaitForExit(BoundMs));

        Assert.True(engine.WaitForExit(BoundMs), "netcoredbg outlived the server");
        Assert.True(debuggee.WaitForExit(BoundMs), "the launched debuggee outlived the server");
    }

    [Fact]
    public async Task Killing_the_server_leaves_a_process_it_only_attached_to_running()
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add(TestPaths.SampleAppDll);
        using var external = Process.Start(psi)!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var client = await StartServerAsync(cts.Token);
            var attach = await client.CallToolAsync("debug_attach",
                new Dictionary<string, object?> { ["pid"] = external.Id }, cancellationToken: cts.Token);
            Assert.True(attach.IsError != true);

            var (engine, server, _) = FindEngineAndServer();
            using var _e = engine;
            using var _s = server;
            server.Kill();
            Assert.True(server.WaitForExit(BoundMs));
            Assert.True(engine.WaitForExit(BoundMs), "netcoredbg outlived the server");

            Assert.False(external.HasExited, "killing the server took down a process it had only attached to");
        }
        finally
        {
            try { external.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }
}
