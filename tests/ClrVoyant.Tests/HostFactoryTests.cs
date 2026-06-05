using ClrVoyant.Core;
using ClrVoyant.Inspection;
using ClrVoyant.Server;
using ClrVoyant.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace ClrVoyant.Tests;

public class HostFactoryTests
{
    [Fact]
    public void Create_wires_session_manager_and_inspector()
    {
        var prev = Environment.GetEnvironmentVariable("CLRVOYANT_NETCOREDBG");
        try
        {
            // SessionManager's factory resolves netcoredbg lazily; point it at the bundled copy.
            Environment.SetEnvironmentVariable("CLRVOYANT_NETCOREDBG", TestPaths.Netcoredbg);
            using var host = HostFactory.Create(Array.Empty<string>());
            Assert.NotNull(host.Services.GetRequiredService<SessionManager>());
            Assert.NotNull(host.Services.GetRequiredService<TaskInspector>());
        }
        finally { Environment.SetEnvironmentVariable("CLRVOYANT_NETCOREDBG", prev); }
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
