using ClrVoyant.Server.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server;

/// <summary>
/// The one MCP server configuration. Stdio and HTTP differ only in the transport added after
/// it, and the tests build the server through the same code, so what a test exercises is what runs.
/// </summary>
public static class McpRegistration
{
    /// <summary>Add the MCP server with the ClrVoyant tools (every <c>[McpServerToolType]</c> in this
    /// assembly) and request pipeline. The caller adds the transport to the returned builder.</summary>
    public static IMcpServerBuilder AddClrVoyantMcpServer(this IServiceCollection services)
        => services.AddMcpServer()
            .WithToolsFromAssembly(typeof(McpRegistration).Assembly)
            .UseClrVoyantPipeline();

    /// <summary>The tool-independent part of the configuration: the server instructions (the playbook,
    /// sent to the client in the initialize handshake) and the call-tool filters. Order matters, see
    /// <see cref="ToolErrors"/>: error mapping outermost, audit inside it.</summary>
    public static IMcpServerBuilder UseClrVoyantPipeline(this IMcpServerBuilder builder)
    {
        builder.Services.Configure<McpServerOptions>(o => o.ServerInstructions = DebugInstructions.Text);
        return builder.WithRequestFilters(filters =>
        {
            ToolErrors.Register(filters);
            AuditLog.Register(filters);
        });
    }
}
