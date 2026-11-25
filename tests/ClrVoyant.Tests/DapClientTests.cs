using System.Text.Json;
using ClrVoyant.Dap;
using ClrVoyant.Tests.Fakes;

namespace ClrVoyant.Tests;

/// <summary>
/// DapClient against a scripted in-memory adapter: framing, correlation, the terminal
/// closed state, request bounds, and survival of bad input. No process involved.
/// </summary>
public class DapClientTests : IDisposable
{
    static readonly TimeSpan Bound = FakeAdapter.Bound;

    readonly FakeAdapter _adapter = new();
    DapClient Client => _adapter.Client;

    public void Dispose() => _adapter.Dispose();

    // Starts the read loop, sends a request and returns it together with what the adapter saw.
    async Task<(Task<JsonElement> response, JsonElement request)> SendAsync(string command, object? args = null, TimeSpan? timeout = null)
    {
        Client.Start();
        var response = Client.RequestAsync(command, args, timeout: timeout);
        return (response, await _adapter.NextRequestAsync());
    }

    // ---- requests and responses ----

    [Fact]
    public async Task Success_response_returns_its_body_and_the_request_is_well_formed()
    {
        var (response, request) = await SendAsync("evaluate", new { expression = "1+1" });

        Assert.Equal("request", request.GetProperty("type").GetString());
        Assert.Equal("evaluate", request.GetProperty("command").GetString());
        Assert.Equal("1+1", request.GetProperty("arguments").GetProperty("expression").GetString());

        _adapter.Respond(request, new { result = "2" });
        var reply = await response.WaitAsync(Bound);

        Assert.Equal("2", reply.GetProperty("body").GetProperty("result").GetString());
        Assert.Equal(0, Client.PendingCount);
    }

    [Fact]
    public async Task Error_response_throws_DapException_with_the_adapters_message()
    {
        var (response, request) = await SendAsync("launch");

        _adapter.Reject(request, "program not found");
        var ex = await Assert.ThrowsAsync<DapException>(() => response.WaitAsync(Bound));

        Assert.Equal("launch", ex.Command);
        Assert.Contains("program not found", ex.Message);
        Assert.Equal(0, Client.PendingCount);
    }

    [Fact]
    public async Task Concurrent_requests_are_matched_to_their_own_responses()
    {
        Client.Start();
        var first = Client.RequestAsync("threads");
        var second = Client.RequestAsync("scopes");
        var r1 = await _adapter.NextRequestAsync();
        var r2 = await _adapter.NextRequestAsync();

        // Answer out of order.
        _adapter.Respond(r2, new { which = "second" });
        _adapter.Respond(r1, new { which = "first" });

        Assert.Equal("first", (await first.WaitAsync(Bound)).GetProperty("body").GetProperty("which").GetString());
        Assert.Equal("second", (await second.WaitAsync(Bound)).GetProperty("body").GetProperty("which").GetString());
    }

    // ---- framing ----

    [Fact]
    public async Task A_frame_split_at_every_byte_is_reassembled()
    {
        var (response, request) = await SendAsync("threads");

        _adapter.PushByteByByte(FakeAdapter.Frame(new
        {
            type = "response",
            request_seq = request.GetProperty("seq").GetInt32(),
            success = true,
            command = "threads",
            body = new { note = "split everywhere é" }, // multi-byte UTF-8 straddles reads too
        }));

        var reply = await response.WaitAsync(Bound);
        Assert.Equal("split everywhere é", reply.GetProperty("body").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Two_frames_in_one_read_are_both_delivered_in_order()
    {
        var stops = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.Stopped += body => { stops.Add(body.GetProperty("reason").GetString()!); if (stops.Count == 2) both.SetResult(); };
        Client.Start();

        _adapter.PushRaw([
            .. FakeAdapter.Frame(new { type = "event", @event = "stopped", body = new { reason = "breakpoint" } }),
            .. FakeAdapter.Frame(new { type = "event", @event = "stopped", body = new { reason = "step" } }),
        ]);

        await both.Task.WaitAsync(Bound);
        Assert.Equal(["breakpoint", "step"], stops);
    }

    [Fact]
    public async Task A_read_ending_mid_header_of_the_next_frame_carries_over()
    {
        var reasons = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.Stopped += body => { reasons.Add(body.GetProperty("reason").GetString()!); if (reasons.Count == 2) both.SetResult(); };
        Client.Start();

        var a = FakeAdapter.Frame(new { type = "event", @event = "stopped", body = new { reason = "one" } });
        var b = FakeAdapter.Frame(new { type = "event", @event = "stopped", body = new { reason = "two" } });
        _adapter.PushRaw([.. a, .. b[..7]]);   // first frame + part of the next header
        _adapter.PushRaw(b[7..]);              // the rest

        await both.Task.WaitAsync(Bound);
        Assert.Equal(["one", "two"], reasons);
    }

    // ---- events ----

    [Fact]
    public async Task Events_reach_their_subscribers_with_their_payload()
    {
        var seen = new List<string>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Saw(string s) { lock (seen) { seen.Add(s); if (seen.Count == 6) done.TrySetResult(); } }

        Client.Stopped += b => Saw("stopped:" + b.GetProperty("threadId").GetInt32());
        Client.ExitedEvent += b => Saw("exited:" + b.GetProperty("exitCode").GetInt32());
        Client.TerminatedEvent += _ => Saw("terminated");
        Client.ProcessStarted += pid => Saw("process:" + pid);
        Client.Output += (cat, text) => Saw($"output:{cat}:{text}");
        Client.BreakpointChanged += b => Saw("breakpoint:" + b.GetProperty("breakpoint").GetProperty("id").GetInt32());
        Client.Start();

        _adapter.Event("initialized");
        _adapter.Event("stopped", new { reason = "breakpoint", threadId = 7 });
        _adapter.Event("exited", new { exitCode = 3 });
        _adapter.Event("terminated");
        _adapter.Event("process", new { systemProcessId = 4242 });
        _adapter.Event("output", new { category = "stderr", output = "oops\n" });
        _adapter.Event("breakpoint", new { reason = "changed", breakpoint = new { id = 9, verified = true } });

        await done.Task.WaitAsync(Bound);
        await Client.WaitInitializedAsync().WaitAsync(Bound);
        Assert.Equal(["stopped:7", "exited:3", "terminated", "process:4242", "output:stderr:oops\n", "breakpoint:9"], seen);
        Assert.Equal(4242, Client.TargetPid);
    }

    [Fact]
    public async Task Output_without_a_category_is_reported_as_console()
    {
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Client.Output += (cat, _) => got.TrySetResult(cat);
        Client.Start();

        _adapter.Event("output", new { output = "hello" });

        Assert.Equal("console", await got.Task.WaitAsync(Bound));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_later_messages()
    {
        Client.Stopped += _ => throw new InvalidOperationException("subscriber bug");
        var (response, request) = await SendAsync("threads");

        _adapter.Event("stopped", new { reason = "breakpoint" }); // handler throws on the read loop
        _adapter.Respond(request, new { ok = true });              // the loop must still deliver this

        await response.WaitAsync(Bound);

        // The failure is not silent: it shows up in the tail of any later closed error.
        _adapter.Eof();
        var ex = await Assert.ThrowsAsync<IOException>(() => Client.RequestAsync("x").WaitAsync(Bound));
        Assert.Contains("subscriber of the 'stopped' event threw InvalidOperationException", ex.Message);
    }

    // ---- bad input ----

    [Fact]
    public async Task An_unparseable_message_is_dropped_and_the_connection_survives()
    {
        var (response, request) = await SendAsync("threads");

        _adapter.PushRaw(FakeAdapter.FrameBytes("this is not json"u8.ToArray())); // well framed, bad payload
        _adapter.Respond(request);

        await response.WaitAsync(Bound);
        Assert.False(Client.IsClosed);
    }

    [Fact]
    public async Task A_malformed_response_is_dropped_and_the_connection_survives()
    {
        var (response, request) = await SendAsync("threads");

        // A response without request_seq cannot be matched to anything.
        _adapter.PushRaw(FakeAdapter.Frame(new { type = "response", success = true }));
        _adapter.Respond(request);

        await response.WaitAsync(Bound);
        Assert.False(Client.IsClosed);
    }

    [Fact]
    public async Task A_header_without_a_usable_Content_Length_closes_the_connection()
    {
        var (response, _) = await SendAsync("threads");

        // Without a length the next message boundary is unknowable: not recoverable.
        _adapter.PushRaw("Content-Type: x\r\n\r\n{}"u8.ToArray());

        var ex = await Assert.ThrowsAsync<IOException>(() => response.WaitAsync(Bound));
        Assert.Contains("Content-Length", ex.Message);
        Assert.True(Client.IsClosed);
    }

    [Fact]
    public async Task A_stream_ending_mid_message_closes_the_connection()
    {
        var (response, _) = await SendAsync("threads");

        _adapter.PushRaw("Content-Length: 100\r\n\r\n{\"type\":"u8.ToArray());
        _adapter.Eof();

        var ex = await Assert.ThrowsAsync<IOException>(() => response.WaitAsync(Bound));
        Assert.Contains("middle of a DAP message", ex.Message);
    }

    // ---- terminal closed state ----

    [Fact]
    public async Task End_of_stream_fails_the_pending_request_and_every_later_one_immediately()
    {
        var (pending, _) = await SendAsync("evaluate");

        _adapter.Eof();

        var first = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Bound));
        Assert.Contains("closed", first.Message);
        Assert.True(Client.IsClosed);
        Assert.Equal(0, Client.PendingCount);

        // Issued after the loop died: must fail at once, not register a request nobody answers.
        var later = await Assert.ThrowsAsync<IOException>(() => Client.RequestAsync("variables").WaitAsync(Bound));
        Assert.Equal(first.Message, later.Message);
        Assert.Equal(0, Client.PendingCount);
    }

    [Fact]
    public async Task A_read_error_closes_the_connection_with_its_cause()
    {
        var (pending, _) = await SendAsync("evaluate");

        _adapter.FailReads(new IOException("pipe reset (test)"));

        var ex = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Bound));
        Assert.Contains("pipe reset (test)", ex.Message);
        Assert.True(Client.IsClosed);
    }

    [Fact]
    public async Task Closing_fails_a_wait_for_the_initialized_event()
    {
        Client.Start();
        var initialized = Client.WaitInitializedAsync();

        _adapter.Eof();

        await Assert.ThrowsAsync<IOException>(() => initialized.WaitAsync(Bound));
    }

    [Fact]
    public async Task Dispose_fails_pending_requests_and_rejects_new_ones()
    {
        var (pending, _) = await SendAsync("evaluate");

        Client.Dispose();

        var ex = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Bound));
        Assert.Contains("disposed", ex.Message);
        await Assert.ThrowsAsync<IOException>(() => Client.RequestAsync("x").WaitAsync(Bound));
    }

    [Fact]
    public async Task A_failed_write_closes_the_connection_and_names_the_request()
    {
        Client.Start();
        _adapter.BreakWrites();

        var ex = await Assert.ThrowsAsync<IOException>(() => Client.RequestAsync("evaluate").WaitAsync(Bound));

        Assert.Contains("writing 'evaluate' failed", ex.Message);
        Assert.True(Client.IsClosed);
        Assert.Equal(0, Client.PendingCount);
    }

    [Fact]
    public async Task A_write_the_adapter_never_accepts_times_out_and_closes_the_connection()
    {
        Client.Start();
        _adapter.StallWrites();

        // A half-written frame leaves the stream out of sync, so this is fatal, not retryable.
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => Client.RequestAsync("evaluate", timeout: TimeSpan.FromMilliseconds(100)).WaitAsync(Bound));

        Assert.Contains("evaluate", ex.Message);
        Assert.True(Client.IsClosed);
        Assert.Equal(0, Client.PendingCount);
    }

    // ---- why the adapter went away ----

    [Fact]
    public async Task Closed_error_carries_the_tail_of_adapter_stderr()
    {
        using var adapter = new FakeAdapter(withStderr: true);
        adapter.Client.Start();
        var pending = adapter.Client.RequestAsync("evaluate");
        await adapter.NextRequestAsync();

        adapter.WriteStderr("Unhandled exception: disk on fire\n");
        adapter.Eof();

        var ex = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Bound));
        Assert.Contains("disk on fire", ex.Message);
    }

    [Fact]
    public async Task Stderr_is_drained_into_a_bounded_tail()
    {
        using var adapter = new FakeAdapter(withStderr: true);
        adapter.Client.Start();
        var pending = adapter.Client.RequestAsync("evaluate");
        await adapter.NextRequestAsync();

        // Far more than the buffer holds: only the end may survive, and memory stays bounded.
        adapter.WriteStderr("FIRST-LINE\n");
        for (int i = 0; i < 200; i++) adapter.WriteStderr(new string('x', 1000) + "\n");
        adapter.WriteStderr("LAST-LINE\n");
        adapter.Eof();

        var ex = await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(Bound));
        Assert.Contains("LAST-LINE", ex.Message);
        Assert.DoesNotContain("FIRST-LINE", ex.Message);
        Assert.True(ex.Message.Length < 12 * 1024, $"message is {ex.Message.Length} chars");
    }

    // ---- request bounds ----

    [Fact]
    public async Task A_request_that_gets_no_answer_times_out_naming_the_command_and_is_removed()
    {
        var (response, request) = await SendAsync("evaluate", timeout: TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => response.WaitAsync(Bound));

        Assert.Contains("'evaluate'", ex.Message);
        Assert.Contains("0.2s", ex.Message); // the bound that applied (200 ms)
        Assert.Equal(0, Client.PendingCount);
        Assert.False(Client.IsClosed); // a slow answer is not a dead connection

        // A late answer to the abandoned request is ignored; the connection still works.
        _adapter.Respond(request, new { late = true });
        var next = Client.RequestAsync("threads");
        _adapter.Respond(await _adapter.NextRequestAsync(), new { fresh = true });
        Assert.True((await next.WaitAsync(Bound)).GetProperty("body").GetProperty("fresh").GetBoolean());
    }

    [Fact]
    public async Task Cancelling_a_request_throws_and_removes_it()
    {
        Client.Start();
        using var cts = new CancellationTokenSource();
        var response = Client.RequestAsync("evaluate", ct: cts.Token);
        await _adapter.NextRequestAsync();
        Assert.Equal(1, Client.PendingCount);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(Bound));
        Assert.Equal(0, Client.PendingCount);
        Assert.False(Client.IsClosed);
    }

    [Fact]
    public async Task A_token_cancelled_before_the_call_sends_nothing()
    {
        Client.Start();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.RequestAsync("evaluate", ct: cts.Token));

        Assert.Empty(_adapter.Received);
        Assert.Equal(0, Client.PendingCount);
    }
}
