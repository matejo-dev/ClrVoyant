using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using ClrVoyant.Server;
using ClrVoyant.Server.Tools;
using ClrVoyant.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// What the host wiring must keep doing. Behaviour of the tools and of the pipeline is covered where
/// it can be driven (McpToolSurfaceTests, ToolExceptionTests, AuditLogTests); what only the real host
/// can show is that both transports are built from the shared configuration, and that the services
/// are tied together.
/// </summary>
public class HostFactoryTests
{
    // The MCP options of a built host: what a transport would serve to its clients.
    static void AssertServesTheClrVoyantSurface(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;

        Assert.Equal(DebugInstructions.Text, options.ServerInstructions);
        Assert.Equal(
            McpToolSurfaceTests.ToolNames.Order(StringComparer.Ordinal),
            options.ToolCollection!.Select(t => t.ProtocolTool.Name).Order(StringComparer.Ordinal));
        // Error mapping and audit: one call-tool filter each (their order and behaviour are pinned elsewhere).
        Assert.Equal(2, options.Filters.Request.CallToolFilters.Count);
    }

    [Fact]
    public void The_stdio_host_is_built_from_the_shared_mcp_configuration()
    {
        using var host = HostFactory.Create(Array.Empty<string>());

        AssertServesTheClrVoyantSurface(host.Services);
    }

    [Fact]
    public async Task The_http_host_is_built_from_the_shared_mcp_configuration()
    {
        // Built, not started: BuildHttpApp is the whole wiring.
        await using WebApplication app = HostFactory.BuildHttpApp([], "unused-token", "http://127.0.0.1:0");

        AssertServesTheClrVoyantSurface(app.Services);
    }

    // The session manager tells the heap inspector when a process is gone, so a dead process does not
    // keep its snapshot (or lend it to a recycled pid). The wiring is made in AddDebugServices; this
    // drives it end to end against a real debuggee, through the services as the host registers them.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Ending_a_session_releases_its_heap_snapshot_through_the_hosts_wiring()
    {
        var services = new ServiceCollection();
        HostFactory.AddDebugServices(services, () => new DapEngine(TestPaths.Netcoredbg));
        await using var provider = services.BuildServiceProvider();
        var mgr = provider.GetRequiredService<SessionManager>();
        var inspector = provider.GetRequiredService<TaskInspector>();

        var info = await LifecycleTools.DebugLaunch(mgr, TestPaths.SampleAppDll);
        await BreakpointTools.SetBreakpoint(mgr, TestPaths.SampleAppSrc, TestPaths.BreakpointLine);
        await ExecutionTools.Continue(mgr, threadId: null, timeoutMs: 20000);
        var s = mgr.Resolve(info.SessionId);
        Assert.NotEmpty(inspector.EnumerateTasks(s.Pid, s.StopId, null, 0, 10).Items);
        Assert.Contains(s.Pid, inspector.CachedPids); // taking the snapshot cached it

        await mgr.StopAsync(info.SessionId);

        Assert.DoesNotContain(s.Pid, inspector.CachedPids);
    }

    [Fact]
    public async Task Http_transport_fails_closed_without_a_token()
    {
        // A debug server that can launch processes and evaluate code is an RCE
        // surface, so the HTTP transport must refuse to start without an auth token
        // rather than bind unauthenticated.
        var prevTransport = Environment.GetEnvironmentVariable("CLRVOYANT_TRANSPORT");
        var prevToken = Environment.GetEnvironmentVariable("CLRVOYANT_AUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CLRVOYANT_TRANSPORT", "http");
            Environment.SetEnvironmentVariable("CLRVOYANT_AUTH_TOKEN", null);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => HostFactory.RunAsync(Array.Empty<string>()));
            Assert.Contains("CLRVOYANT_AUTH_TOKEN", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLRVOYANT_TRANSPORT", prevTransport);
            Environment.SetEnvironmentVariable("CLRVOYANT_AUTH_TOKEN", prevToken);
        }
    }
}
