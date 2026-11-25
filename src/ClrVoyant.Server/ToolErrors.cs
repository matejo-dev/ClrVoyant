using ClrVoyant.Dap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ClrVoyant.Server;

/// <summary>
/// Turns a failed tool call into an error result the caller can act on. The tools rely on exception
/// messages to tell the LLM how to recover ("Session 's1' is Running, not stopped…", "pass
/// sessionId"), but the SDK replaces the message of every exception except <see cref="McpException"/>
/// with a generic "An error occurred invoking 'tool'". This one filter, at the dispatch boundary,
/// restores the message for every present and future tool.
///
/// What reaches the caller (as <c>isError: true</c> with the text as the only content):
/// <list type="bullet">
/// <item>An exception of a kind this code base throws on purpose to explain a condition to the
/// caller (<see cref="InvalidOperationException"/>, <see cref="ArgumentException"/>,
/// <see cref="KeyNotFoundException"/>, <see cref="TimeoutException"/>, <see cref="IOException"/>,
/// <see cref="DapException"/>, <see cref="McpException"/>): its message, unchanged.</item>
/// <item>Anything else is a probable bug: the message prefixed with the exception type
/// ("Unexpected NullReferenceException: …"), since a bare message such as "Object reference not set"
/// is useless without it, and still more useful to the caller than nothing. The full exception with
/// its stack trace goes to the server log only, never to the client.</item>
/// </list>
/// What does not become an error result: cancellation requested by the client (it stays
/// cancellation, so the SDK answers the cancelled request the way it always did) and
/// <see cref="McpProtocolException"/> (a JSON-RPC protocol error such as invalid params, which is not
/// a tool failure).
///
/// Order: registered BEFORE <see cref="AuditLog"/> (the first filter is the outermost), so the audit
/// filter sees the original exception — its message and cancellation — and this one turns it into the
/// result afterwards.
/// </summary>
public static class ToolErrors
{
    /// <summary>Category of the server-side log entry written for an unexpected exception.</summary>
    public const string Category = "ClrVoyant.ToolErrors";

    public static void Register(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (McpProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!IsExpected(ex))
                {
                    context.Services?.GetService<ILoggerFactory>()?.CreateLogger(Category)
                        .LogError(ex, "tools/call {Tool} failed unexpectedly", context.Params?.Name ?? "(unknown)");
                }
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = Describe(ex) }],
                };
            }
        });
    }

    internal static string Describe(Exception ex)
    {
        string message = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message;
        return IsExpected(ex) ? message : $"Unexpected {ex.GetType().Name}: {message}";
    }

    static bool IsExpected(Exception ex)
        => ex is InvalidOperationException or ArgumentException or KeyNotFoundException
            or TimeoutException or IOException or DapException or McpException;
}
