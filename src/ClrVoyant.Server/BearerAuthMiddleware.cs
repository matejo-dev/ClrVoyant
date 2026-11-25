using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClrVoyant.Server;

/// <summary>
/// Gates every HTTP request on one shared bearer token (ADR-0009): anything without it is answered
/// 401 with a Bearer challenge before MCP is reached, and the refusal is written to the audit log
/// (remote address only, never what was presented).
/// </summary>
internal sealed class BearerAuthMiddleware
{
    readonly RequestDelegate _next;
    readonly string _token;

    public BearerAuthMiddleware(RequestDelegate next, string token)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("A bearer token is required: an empty one would admit an empty credential.", nameof(token));
        _next = next;
        _token = token;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAuthorized(context.Request.Headers.Authorization.ToString(), _token))
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(AuditLog.Category)
                .LogWarning("auth -> denied{Remote}", AuditLog.RemoteSuffix(context.Connection.RemoteIpAddress));
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsync("Unauthorized: missing or invalid bearer token.");
            return;
        }
        await _next(context);
    }

    /// <summary>Whether an Authorization header carries exactly <paramref name="token"/> as a bearer
    /// credential. The scheme is case-insensitive (RFC 7235); the token comparison takes the same time
    /// whatever prefix of it was guessed.</summary>
    internal static bool IsAuthorized(string authorizationHeader, string token)
    {
        const string scheme = "Bearer";
        if (authorizationHeader.Length <= scheme.Length
            || !authorizationHeader.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || authorizationHeader[scheme.Length] != ' ')
            return false;
        string presented = authorizationHeader[(scheme.Length + 1)..].TrimStart(' ');
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
    }
}

internal static class BearerAuthExtensions
{
    /// <summary>Require <paramref name="token"/> as a bearer token on every request that follows.</summary>
    internal static IApplicationBuilder UseBearerToken(this IApplicationBuilder app, string token)
        => app.UseMiddleware<BearerAuthMiddleware>(token);
}
