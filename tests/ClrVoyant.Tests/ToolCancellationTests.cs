using System.Text.Json;
using ClrVoyant.Core;
using ClrVoyant.Server;
using ClrVoyant.Tests.Fakes;
using ModelContextProtocol.Protocol;

namespace ClrVoyant.Tests;

/// <summary>
/// A client that cancels an in-flight tool call must reach the engine side: the wait ends at once, and
/// the session is not left believing a continue is still in progress (which would swallow the next stop
/// or refuse the next resume). Driven by a real MCP client against the real registration.
/// </summary>
public class ToolCancellationTests
{
    [Fact]
    public async Task Cancelling_an_in_flight_continue_ends_it_promptly_and_leaves_the_session_usable()
    {
        var engines = new FakeEngineFactory { Customize = e => e.StopOnResume = null }; // continue never stops by itself
        var logs = await McpInMemory.RunRealAsync(engines, async run =>
        {
            await run.Client.CallToolAsync("debug_launch", new Dictionary<string, object?> { ["program"] = "app.dll" }, cancellationToken: run.Ct);
            var engine = Assert.Single(engines.Engines);

            // 20 s is the tool's own timeout: only a cancellation that reaches the tool can end the call sooner.
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Ct);
            var call = run.Client.CallToolAsync("continue", new Dictionary<string, object?> { ["timeoutMs"] = 20000 },
                cancellationToken: cancel.Token).AsTask();
            await engine.ResumeCalledAsync(); // the server is now waiting for the next stop
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

            // The server side saw the cancellation (not a 20 s timeout) ...
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(run.Ct);
            bound.CancelAfter(TimeSpan.FromSeconds(10));
            await run.Logs.WaitForAuditAsync("tools/call continue -> canceled", bound.Token);

            // ... and released the wait: the stop that comes now is spontaneous, so wait_for_any_stop reports it.
            // (A wait left armed would have swallowed it and this would time out.)
            engine.RaiseStopped(new StopLocation("F.cs", 3, "breakpoint", 1));
            var any = await run.Client.CallToolAsync("wait_for_any_stop", new Dictionary<string, object?> { ["timeoutMs"] = 5000 }, cancellationToken: run.Ct);
            var stop = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(any.Content)).Text).RootElement;
            Assert.Equal("s1", stop.GetProperty("sessionId").GetString());
            Assert.Equal("Stopped", stop.GetProperty("state").GetString());

            // And the session accepts a new resume (it would be refused while a continue was in progress).
            var again = await run.Client.CallToolAsync("continue", new Dictionary<string, object?> { ["timeoutMs"] = 100 }, cancellationToken: run.Ct);
            Assert.True(again.IsError != true);
        });

        // The cancellation passed through the error mapping as a cancellation: it was not turned into a
        // failure (which would have been logged as one). Checked after the server has shut down.
        Assert.DoesNotContain(logs.Entries, e => e.Category == ToolErrors.Category);
    }
}
