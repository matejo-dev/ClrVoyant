using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server;

/// <summary>
/// Audit logging for the MCP tool surface. A debug server that can launch
/// processes and <c>evaluate</c> code is an RCE surface, so every <c>tools/call</c>
/// is logged (tool name + outcome) to make debug sessions attributable — see
/// docs/security.md. Over HTTP each line also names the caller (" remote=&lt;address&gt;"),
/// and a request refused by the bearer-token gate is logged as "auth -> denied" with the
/// same address (<see cref="BearerAuthMiddleware"/>); never what was presented.
/// The log goes through the host's <see cref="ILoggerFactory"/>:
/// on stdio that means stderr (stdout is the JSON-RPC stream); on HTTP it is the
/// normal console/sink.
/// </summary>
public static class AuditLog
{
    /// <summary>Category under which audit lines are emitted.</summary>
    public const string Category = "ClrVoyant.Audit";

    /// <summary>The " remote=&lt;address&gt;" suffix of an audit line, empty when there is no remote
    /// address (stdio).</summary>
    internal static string RemoteSuffix(IPAddress? remote)
        => remote is null ? "" : $" remote={(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)}";

    /// <summary>
    /// Register the audit filter on the MCP request pipeline. Wraps the call-tool
    /// handler so it observes every invocation regardless of transport.
    /// </summary>
    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            string tool = context.Params?.Name ?? "(unknown)";
            // Resolve the logger per-call from the request scope: the filter factory
            // runs before the host provider exists, so there is nothing to capture
            // at registration time.
            ILogger? logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(Category);
            // Over HTTP the line says who called (stdio has no remote: the parent process is the caller).
            string remote = RemoteSuffix(context.Services?.GetService<IHttpContextAccessor>()?.HttpContext?.Connection.RemoteIpAddress);
            try
            {
                CallToolResult result = await next(context, cancellationToken);
                // A failed call reaches the filter two ways: the tool returns a result
                // with IsError=true, or it throws and the exception propagates here
                // (handled below). Both are logged as "error" so the audit vocabulary
                // stays a parseable ok | error | canceled.
                logger?.LogInformation("tools/call {Tool} -> {Outcome}{Remote}", tool, result.IsError == true ? "error" : "ok", remote);
                return result;
            }
            catch (OperationCanceledException)
            {
                logger?.LogInformation("tools/call {Tool} -> canceled{Remote}", tool, remote);
                throw;
            }
            catch (Exception ex)
            {
                logger?.LogWarning("tools/call {Tool} -> error ({Message}){Remote}", tool, ex.Message, remote);
                throw;
            }
        });
    }
}
