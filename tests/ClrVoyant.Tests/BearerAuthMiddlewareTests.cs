using System.Net;
using ClrVoyant.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClrVoyant.Tests;

/// <summary>
/// The bearer-token gate of the HTTP transport (ADR-0009), driven directly: the header rules as a pure
/// function, and the middleware around them. The end-to-end behaviour over Kestrel is pinned in
/// <see cref="HttpTransportTests"/>.
/// </summary>
public class BearerAuthMiddlewareTests
{
    const string Token = "s3cret-Token";

    // ---- what counts as the right credential ----

    [Theory]
    [InlineData("Bearer s3cret-Token")]
    [InlineData("bearer s3cret-Token")]   // RFC 7235: the scheme is case-insensitive
    [InlineData("BEARER s3cret-Token")]
    [InlineData("BeArEr s3cret-Token")]
    [InlineData("Bearer   s3cret-Token")] // 1*SP between scheme and credentials
    public void The_right_token_under_the_bearer_scheme_is_accepted(string header)
    {
        Assert.True(BearerAuthMiddleware.IsAuthorized(header, Token));
    }

    [Theory]
    [InlineData("")]                      // no header
    [InlineData("Bearer")]                // scheme only
    [InlineData("Bearer ")]               // empty bearer
    [InlineData("Bearer    ")]
    [InlineData("Bearer wrong")]
    [InlineData("Bearer s3cret-token")]   // the token itself is case-sensitive
    [InlineData("Bearer s3cret-Toke")]    // a prefix of it
    [InlineData("Bearer s3cret-Token2")]  // an extension of it
    [InlineData("Bearer s3cret-Token ")]
    [InlineData("s3cret-Token")]          // the token as the whole header value
    [InlineData("Basic s3cret-Token")]    // another scheme
    [InlineData("Bearers3cret-Token")]    // no separator
    [InlineData("Bearer\ts3cret-Token")]
    [InlineData("Token Bearer s3cret-Token")]
    public void Anything_else_is_refused(string header)
    {
        Assert.False(BearerAuthMiddleware.IsAuthorized(header, Token));
    }

    [Fact]
    public void A_middleware_without_a_token_cannot_be_built()
    {
        Assert.Throws<ArgumentException>(() => new BearerAuthMiddleware(_ => Task.CompletedTask, ""));
    }

    // ---- the middleware ----

    sealed class Rig
    {
        public RecordingLog Log { get; } = new();
        public bool NextCalled { get; private set; }
        public HttpContext Context { get; }
        readonly BearerAuthMiddleware _middleware;

        public Rig(string? authorization, string? remote = "203.0.113.7")
        {
            var services = new ServiceCollection()
                .AddLogging(b => b.ClearProviders().AddProvider(Log))
                .BuildServiceProvider();
            Context = new DefaultHttpContext { RequestServices = services };
            Context.Response.Body = new MemoryStream();
            if (authorization is not null) Context.Request.Headers.Authorization = authorization;
            if (remote is not null) Context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            _middleware = new BearerAuthMiddleware(_ => { NextCalled = true; return Task.CompletedTask; }, Token);
        }

        public async Task<string> RunAsync()
        {
            await _middleware.InvokeAsync(Context);
            Context.Response.Body.Position = 0;
            return await new StreamReader(Context.Response.Body).ReadToEndAsync();
        }
    }

    [Fact]
    public async Task A_request_with_the_token_goes_on_and_is_not_logged()
    {
        var rig = new Rig("Bearer " + Token);

        await rig.RunAsync();

        Assert.True(rig.NextCalled);
        Assert.Equal(StatusCodes.Status200OK, rig.Context.Response.StatusCode);
        Assert.Empty(rig.Log.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData(Token)]
    [InlineData("Basic " + Token)]
    public async Task A_request_without_the_token_is_answered_401_with_a_challenge_and_goes_no_further(string? header)
    {
        var rig = new Rig(header);

        string body = await rig.RunAsync();

        Assert.False(rig.NextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, rig.Context.Response.StatusCode);
        Assert.Equal("Bearer", rig.Context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Contains("Unauthorized", body);
    }

    [Fact]
    public async Task A_refusal_is_written_to_the_audit_log_with_the_remote_address_and_nothing_that_was_presented()
    {
        var rig = new Rig("Bearer guess-12345");

        await rig.RunAsync();

        var line = Assert.Single(rig.Log.Entries);
        Assert.Equal(AuditLog.Category, line.Category);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("auth -> denied remote=203.0.113.7", line.Message);
        Assert.DoesNotContain("guess", line.Message);
        Assert.DoesNotContain(Token, line.Message);
    }

    [Fact]
    public async Task A_refusal_without_a_known_remote_address_is_still_logged()
    {
        var rig = new Rig(null, remote: null);

        await rig.RunAsync();

        Assert.Equal("auth -> denied", Assert.Single(rig.Log.Entries).Message);
    }

    // ---- the audit suffix ----

    [Theory]
    [InlineData("203.0.113.7", " remote=203.0.113.7")]
    [InlineData("::1", " remote=::1")]
    [InlineData("::ffff:203.0.113.7", " remote=203.0.113.7")] // a dual-stack socket reports IPv4 callers this way
    public void The_remote_address_is_appended_in_its_plain_form(string address, string expected)
    {
        Assert.Equal(expected, AuditLog.RemoteSuffix(IPAddress.Parse(address)));
    }

    [Fact]
    public void No_remote_address_adds_nothing_so_stdio_lines_are_unchanged()
    {
        Assert.Equal("", AuditLog.RemoteSuffix(null));
    }
}
