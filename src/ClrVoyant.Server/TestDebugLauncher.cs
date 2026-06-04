using System.Diagnostics;
using System.Text.RegularExpressions;
using ClrVoyant.Core;

namespace ClrVoyant.Server;

/// <summary>
/// Orchestrates the "debug a unit test" recipe behind a single call: runs
/// <c>dotnet test</c> with <c>VSTEST_HOST_DEBUG=1</c> (which makes the test host
/// print its PID and spin waiting for a debugger), parses that PID, and attaches
/// a debug session to it — which releases the wait. The <c>dotnet test</c> driver
/// process is tied to the session so it is killed when the session ends.
/// </summary>
public sealed partial class TestDebugLauncher
{
    readonly SessionManager _sessions;

    public TestDebugLauncher(SessionManager sessions) => _sessions = sessions;

    // The test host prints e.g. "Process Id: 12345, Name: testhost".
    [GeneratedRegex(@"Process Id:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessIdRegex();

    public async Task<SessionInfo> LaunchAndAttachAsync(
        string testProject,
        string? testName,
        string configuration,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        string projectPath = Path.GetFullPath(testProject);
        string workingDir = Directory.Exists(projectPath)
            ? projectPath
            : Path.GetDirectoryName(projectPath) ?? Directory.GetCurrentDirectory();

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configuration);
        if (!string.IsNullOrWhiteSpace(testName))
        {
            psi.ArgumentList.Add("--filter");
            psi.ArgumentList.Add(testName);
        }
        // Make the test host suspend and announce its PID instead of running.
        psi.Environment["VSTEST_HOST_DEBUG"] = "1";

        var proc = new Process { StartInfo = psi };
        var pidTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            var m = ProcessIdRegex().Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int pid))
                pidTcs.TrySetResult(pid);
        }

        proc.OutputDataReceived += OnData;
        proc.ErrorDataReceived += OnData; // the banner can land on either stream
        var owned = new OwnedProcess(proc);
        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            int testHostPid;
            try
            {
                testHostPid = await pidTcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"'dotnet test' did not announce a test-host PID within {timeout.TotalSeconds:0}s. " +
                    "Ensure the project builds in Debug and that the test host honours VSTEST_HOST_DEBUG.");
            }

            var info = await _sessions.AttachAsync(testHostPid, ct);
            // Tie the driver process to the session so debug_stop cleans it up.
            _sessions.Resolve(info.SessionId).OwnedResource = owned;
            owned = null; // ownership transferred to the session
            return info;
        }
        finally
        {
            // If we never handed the process to a session (error/timeout), kill it.
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    /// <summary>Wraps the <c>dotnet test</c> driver process and kills the whole
    /// tree (driver + test host) when the owning session is disposed.</summary>
    sealed class OwnedProcess : IAsyncDisposable
    {
        readonly Process _proc;
        public OwnedProcess(Process proc) => _proc = proc;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }
            finally { _proc.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}
