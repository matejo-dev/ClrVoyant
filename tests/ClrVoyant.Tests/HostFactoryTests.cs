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
}
