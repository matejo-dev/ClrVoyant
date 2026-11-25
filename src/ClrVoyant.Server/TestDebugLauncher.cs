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
///
/// The call ends as soon as the outcome is known: the banner appears (attach), the driver
/// exits first (a failed build, no matching test: fails at once with its exit code and the end
/// of its output), the timeout passes, or the caller cancels. Unless the first happened, a driver that is still
/// running is killed together with everything it started.
/// </summary>
public sealed partial class TestDebugLauncher
{
    // How much of the driver's output an error carries: the end of a build log is where the errors are.
    const int OutputTailChars = 3000;
    const int OutputCaptureChars = 16 * 1024;

    /// <summary>Builds the command that runs the tests: project path, working directory, filter,
    /// configuration. A seam so the control flow around the process can be tested with a small
    /// real child process instead of a real <c>dotnet test</c>.</summary>
    internal delegate ProcessStartInfo StartInfoFactory(string projectPath, string workingDirectory, string? testName, string configuration);

    readonly SessionManager _sessions;
    readonly StartInfoFactory _startInfo;

    public TestDebugLauncher(SessionManager sessions) : this(sessions, DotnetTest) { }

    internal TestDebugLauncher(SessionManager sessions, StartInfoFactory startInfo)
    {
        _sessions = sessions;
        _startInfo = startInfo;
    }

    // The test host prints e.g. "Process Id: 12345, Name: testhost".
    [GeneratedRegex(@"Process Id:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessIdRegex();

    /// <summary>The test-host PID announced by an output line, or null if the line is not the banner.</summary>
    internal static int? ParseTestHostPid(string? line)
    {
        if (line is null) return null;
        var m = ProcessIdRegex().Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out int pid) ? pid : null;
    }

    internal static ProcessStartInfo DotnetTest(string projectPath, string workingDirectory, string? testName, string configuration)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
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
        return psi;
    }

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

        var psi = _startInfo(projectPath, workingDir, testName, configuration);
        // Make the test host suspend and announce its PID instead of running.
        psi.Environment["VSTEST_HOST_DEBUG"] = "1";

        var proc = new Process { StartInfo = psi };
        var pidTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new BoundedTextTail(OutputCaptureChars);

        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            output.Append(e.Data + "\n");
            if (ParseTestHostPid(e.Data) is { } pid) pidTcs.TrySetResult(pid);
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
            Task exited = proc.WaitForExitAsync(CancellationToken.None);
            try
            {
                await Task.WhenAny(pidTcs.Task, exited).WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"'dotnet test' did not announce a test-host PID within {timeout.TotalSeconds:0}s. " +
                    "Ensure the project builds in Debug and that the test host honours VSTEST_HOST_DEBUG." +
                    Describe(output));
            }

            if (!pidTcs.Task.IsCompleted)
            {
                // The driver ended first, so there is no test host to attach to.
                await FlushOutputAsync(proc);
                throw new InvalidOperationException(
                    $"'dotnet test' exited with code {proc.ExitCode} before the test host announced its PID, so there is nothing to attach to. " +
                    "Usually the project failed to build, or no test matched the filter." +
                    Describe(output));
            }

            var info = await _sessions.AttachAsync(await pidTcs.Task, ct);
            // Tie the driver process to the session so debug_stop cleans it up.
            _sessions.Resolve(info.SessionId).OwnedResource = owned;
            owned = null; // ownership transferred to the session
            return info;
        }
        finally
        {
            // If we never handed the process to a session (error, timeout, cancellation), kill it and
            // whatever it started.
            if (owned is not null) await owned.DisposeAsync();
        }
    }

    // The exit is known before the last lines of redirected output have been read: wait for them,
    // but not for ever (a grandchild that outlives the driver can keep the pipes open).
    static async Task FlushOutputAsync(Process proc)
    {
        try { await Task.Run(proc.WaitForExit).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { /* what was read so far is what there is */ }
    }

    static string Describe(BoundedTextTail output)
    {
        string tail = output.GetTail(OutputTailChars).TrimEnd();
        return tail.Length == 0 ? " The command printed no output." : $" End of its output:\n{tail}";
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
