using ClrVoyant.Core;
using ClrVoyant.Dap;
using ClrVoyant.Inspection;
using ClrVoyant.Server;
using ClrVoyant.Server.Tools;
using ClrVoyant.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
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

    // ---- the engine is located on the first session, not at startup ----

    const string MissingEngine = "netcoredbg not found and could not be fetched. Set CLRVOYANT_NETCOREDBG to its full path.";

    [Fact]
    public async Task The_host_starts_with_its_background_services_without_looking_for_the_engine()
    {
        int lookups = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        HostFactory.AddDebugServices(builder.Services, locateEngine: () => { lookups++; throw new FileNotFoundException(MissingEngine); });
        using var host = builder.Build();

        // ChildProcessWatcher is a hosted service that takes the SessionManager: starting it used to
        // resolve the engine, so a missing one crashed the server before it could answer anything.
        await host.StartAsync();
        await host.StopAsync();

        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task A_server_without_an_engine_answers_initialize_and_tools_list_and_explains_itself_on_first_use()
    {
        int lookups = 0;
        var logs = await McpInMemory.RunRealAsync(
            () => { lookups++; throw new FileNotFoundException(MissingEngine); },
            async run =>
            {
                var tools = await run.Client.ListToolsAsync(cancellationToken: run.Ct);
                Assert.Equal(McpToolSurfaceTests.ToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
                Assert.Equal(0, lookups);

                // Tools that need no engine work.
                var status = await run.Client.CallToolAsync("list_sessions", cancellationToken: run.Ct);
                Assert.True(status.IsError != true);

                // The ones that start a session fail with the actionable reason, and nothing is left behind.
                foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
                {
                    ("debug_launch", new() { ["program"] = "app.dll" }),
                    ("debug_attach", new() { ["pid"] = 1 }),
                })
                {
                    var failed = await run.Client.CallToolAsync(tool, args, cancellationToken: run.Ct);
                    Assert.True(failed.IsError, tool);
                    Assert.Equal(MissingEngine, Assert.IsType<TextContentBlock>(Assert.Single(failed.Content)).Text);
                }
                var listed = await run.Client.CallToolAsync("list_sessions", cancellationToken: run.Ct);
                Assert.Equal("[]", Assert.IsType<TextContentBlock>(Assert.Single(listed.Content)).Text);
            });

        // A failure is not remembered: each attempt looks again, so the operator can fix it and retry.
        Assert.Equal(2, lookups);
        Assert.Contains(logs.Audit, l => l.StartsWith("tools/call debug_launch -> error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_located_engine_is_remembered()
    {
        int lookups = 0;
        await McpInMemory.RunRealAsync(
            () => { lookups++; return Path.Combine(Path.GetTempPath(), "clrvoyant-no-such-engine"); },
            async run =>
            {
                for (int i = 0; i < 2; i++)
                {
                    // The path is not an executable, so starting the engine fails: that is not what is under test.
                    var failed = await run.Client.CallToolAsync("debug_attach", new Dictionary<string, object?> { ["pid"] = 1 }, cancellationToken: run.Ct);
                    Assert.True(failed.IsError);
                }
            });

        Assert.Equal(1, lookups);
    }

    // ---- HTTP binding ----

    [Fact]
    public void The_http_transport_listens_on_this_machine_only_unless_told_otherwise()
    {
        Assert.True(HostFactory.IsLoopbackUrl(HostFactory.DefaultHttpUrl), HostFactory.DefaultHttpUrl);
        Assert.Equal(HostFactory.DefaultHttpUrl, HostFactory.ResolveHttpUrl(null));
        Assert.Equal(HostFactory.DefaultHttpUrl, HostFactory.ResolveHttpUrl("  "));
        Assert.Equal("http://0.0.0.0:8080", HostFactory.ResolveHttpUrl("http://0.0.0.0:8080"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:3001", true)]
    [InlineData("http://localhost:3001", true)]
    [InlineData("http://[::1]:3001", true)]
    [InlineData("http://0.0.0.0:3001", false)]
    [InlineData("http://[::]:3001", false)]
    [InlineData("http://10.1.2.3:3001", false)]
    [InlineData("http://clrvoyant.example:3001", false)]
    [InlineData("http://*:3001", false)]   // Kestrel wildcard: not a URI, and certainly not loopback
    [InlineData("not a url", false)]
    public void Only_loopback_addresses_count_as_this_machine_only(string url, bool loopback)
    {
        Assert.Equal(loopback, HostFactory.IsLoopbackUrl(url));
    }

    [Fact]
    public void A_non_loopback_binding_is_announced_and_warned_about_a_loopback_one_only_announced()
    {
        var log = new RecordingLog();

        HostFactory.LogBinding(log, ["http://127.0.0.1:3001", "http://10.1.2.3:3001"]);

        Assert.Equal(
            [
                (LogLevel.Information, "ClrVoyant HTTP transport listening on http://127.0.0.1:3001"),
                (LogLevel.Information, "ClrVoyant HTTP transport listening on http://10.1.2.3:3001"),
            ],
            log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => (e.Level, e.Message)));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("http://10.1.2.3:3001", warning.Message);
        Assert.Contains("not a loopback address", warning.Message);
    }
}
