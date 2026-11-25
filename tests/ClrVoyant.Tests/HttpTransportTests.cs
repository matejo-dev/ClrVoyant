using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClrVoyant.Server;
using ClrVoyant.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClrVoyant.Tests;

/// <summary>
/// End-to-end over real HTTP: the actual host wiring (bearer-token middleware in front of
/// MapMcp, stateless Streamable HTTP) on Kestrel at an ephemeral loopback port, driven by
/// the SDK's own HTTP client transport.
/// </summary>
public class HttpTransportTests
{
    const string Token = "test-token-not-a-secret";

    [Fact]
    public async Task Bearer_token_gates_the_endpoint_and_a_client_can_call_tools_repeatedly()
    {
        var prev = Environment.GetEnvironmentVariable("CLRVOYANT_NETCOREDBG");
        // The SessionManager singleton (resolved by a hosted service) locates netcoredbg at startup.
        Environment.SetEnvironmentVariable("CLRVOYANT_NETCOREDBG", TestPaths.Netcoredbg);
        WebApplication? app = null;
        try
        {
            app = HostFactory.BuildHttpApp([], Token, "http://127.0.0.1:0");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = cts.Token;
            await app.StartAsync(ct);
            var endpoint = new Uri(Assert.Single(app.Urls));
            Assert.NotEqual(0, endpoint.Port);

            // No token / wrong token: 401 with a Bearer challenge, before MCP is reached.
            using var http = new HttpClient();
            foreach (string? header in new string?[] { null, "Bearer wrong", "Basic " + Token, Token })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                if (header is not null) request.Headers.TryAddWithoutValidation("Authorization", header);
                using var response = await http.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
            }

            // Right token: the SDK client completes the handshake, lists tools and calls one
            // tool twice on the same client (proves repeated requests work in the chosen session mode).
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token },
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            // Pins the explicit stateless choice: no Mcp-Session-Id is minted.
            Assert.Null(client.SessionId);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            Assert.Contains(tools, t => t.Name == "get_debug_instructions");

            for (int i = 0; i < 2; i++)
            {
                var result = await client.CallToolAsync("get_debug_instructions", cancellationToken: ct);
                Assert.True(result.IsError != true);
                Assert.StartsWith("ClrVoyant", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            }
        }
        finally
        {
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await app.StopAsync(stop.Token);
                await app.DisposeAsync();
            }
            Environment.SetEnvironmentVariable("CLRVOYANT_NETCOREDBG", prev);
        }
    }
}
