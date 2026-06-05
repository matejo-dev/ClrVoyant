using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// End-to-end validation of the SHIPPED artifact: pack ClrVoyant, install it as a
/// .NET global tool from a local folder feed, then drive a real debug loop through
/// the installed `clrvoyant` over MCP/stdio. CLRVOYANT_NO_FETCH=1 forces the engine
/// to come from the bundled per-RID payload (no runtime download), proving the
/// package works offline — the gap that shipped untested in 0.1.0.
/// Opt-in via CLRVOYANT_PACKAGING_TESTS=1 (see <see cref="PackagingFactAttribute"/>).
/// </summary>
[Trait("Category", "Packaging")]
public sealed class InstalledToolTests
{
    [PackagingFact]
    public async Task Installed_tool_runs_a_real_debug_loop_with_the_bundled_engine()
    {
        string work = Path.Combine(Path.GetTempPath(), $"clrvoyant-pkg-{Guid.NewGuid():N}");
        string packDir = Path.Combine(work, "pack");
        string toolDir = Path.Combine(work, "tool");
        Directory.CreateDirectory(packDir);
        Directory.CreateDirectory(toolDir);
        try
        {
            // 1. Pack the tool (this stages netcoredbg for every supported RID).
            string serverCsproj = Path.Combine(TestPaths.RepoRoot, "src", "ClrVoyant.Server", "ClrVoyant.Server.csproj");
            RunDotnet($"pack \"{serverCsproj}\" -c Release -o \"{packDir}\" --nologo", TimeSpan.FromMinutes(8));

            string nupkg = Directory.GetFiles(packDir, "ClrVoyant.*.nupkg").Single();
            // ClrVoyant.<version>.nupkg -> <version>
            string version = Path.GetFileNameWithoutExtension(nupkg)["ClrVoyant.".Length..];

            // 2. Install it as a global tool from the local folder feed only.
            RunDotnet(
                $"tool install ClrVoyant --tool-path \"{toolDir}\" --add-source \"{packDir}\" --version {version} --ignore-failed-sources",
                TimeSpan.FromMinutes(3));

            string toolExe = Path.Combine(toolDir, OperatingSystem.IsWindows() ? "clrvoyant.exe" : "clrvoyant");
            Assert.True(File.Exists(toolExe), $"installed tool not found at {toolExe}");

            // 3. Drive the installed tool over MCP/stdio with fetching disabled, so the
            //    engine MUST resolve from the bundled per-RID payload or the loop fails.
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "clrvoyant",
                Command = toolExe,
                EnvironmentVariables = new Dictionary<string, string?> { ["CLRVOYANT_NO_FETCH"] = "1" },
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = cts.Token;
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            var launch = await client.CallToolAsync("debug_launch",
                new Dictionary<string, object?> { ["program"] = TestPaths.SampleAppDll }, cancellationToken: ct);
            Assert.True(launch.IsError != true, $"debug_launch failed: {Payload(launch)}");

            var bp = await client.CallToolAsync("set_breakpoint",
                new Dictionary<string, object?> { ["file"] = TestPaths.SampleAppSrc, ["line"] = TestPaths.BreakpointLine },
                cancellationToken: ct);
            Assert.True(bp.IsError != true, $"set_breakpoint failed: {Payload(bp)}");

            var cont = await client.CallToolAsync("continue",
                new Dictionary<string, object?> { ["timeoutMs"] = 20000 }, cancellationToken: ct);
            Assert.True(cont.IsError != true, $"continue failed: {Payload(cont)}");
            string contPayload = Payload(cont);
            Assert.Contains("stopped", contPayload, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(TestPaths.BreakpointLine.ToString(), contPayload);

            var stack = await client.CallToolAsync("get_callstack", cancellationToken: ct);
            Assert.True(stack.IsError != true, $"get_callstack failed: {Payload(stack)}");
            Assert.Contains("SampleApp", Payload(stack), StringComparison.OrdinalIgnoreCase);

            await client.CallToolAsync("debug_stop", cancellationToken: ct);
        }
        finally
        {
            try { RunDotnet($"tool uninstall ClrVoyant --tool-path \"{toolDir}\"", TimeSpan.FromMinutes(1), throwOnError: false); }
            catch { /* best effort */ }
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    static string Payload(CallToolResult r)
    {
        var sb = new StringBuilder();
        if (r.StructuredContent is JsonElement el) sb.Append(el.GetRawText());
        foreach (var c in r.Content)
            if (c is TextContentBlock t) sb.Append(' ').Append(t.Text);
        return sb.ToString();
    }

    static void RunDotnet(string args, TimeSpan timeout, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException($"`dotnet {args}` timed out after {timeout}.");
        }
        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException(
                $"`dotnet {args}` exited {p.ExitCode}.\nSTDOUT:\n{stdout.Result}\nSTDERR:\n{stderr.Result}");
    }
}
