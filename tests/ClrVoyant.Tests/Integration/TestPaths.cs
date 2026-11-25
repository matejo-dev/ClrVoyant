using System.Diagnostics;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// Locates repo artifacts (netcoredbg, sample targets) for integration tests. Every
/// member is resolved lazily, so a missing sample build or engine fails only the tests
/// that need it, never the whole suite through a type-initializer failure.
/// </summary>
internal static class TestPaths
{
    /// <summary>The runtime most integration tests debug against.</summary>
    public const string DefaultTfm = "net10.0";

    /// <summary>Every runtime the sample debuggees are built for (and ClrVoyant claims to debug).</summary>
    public static readonly string[] SampleTfms = ["net8.0", "net9.0", "net10.0"];

    static readonly Lazy<string> _repoRoot = new(FindRepoRoot);
    static readonly Lazy<IReadOnlyList<int>> _installedRuntimeMajors = new(ListInstalledRuntimeMajors);
    static readonly Lazy<int> _breakpointLine = new(FindBreakpointLine);

    public static string RepoRoot => _repoRoot.Value;

    public static string Netcoredbg =>
        Path.Combine(RepoRoot, "tools", "netcoredbg",
            OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");

    public static string SampleAppDll => SampleAppDllFor(DefaultTfm);
    public static string ParentAppDll => ParentAppDllFor(DefaultTfm);

    public static string SampleAppDllFor(string tfm) => FindBuiltDll("SampleApp", tfm);
    public static string ParentAppDllFor(string tfm) => FindBuiltDll("ParentApp", tfm);

    /// <summary>The server as built by its own project (with the engine bundled next to it), runnable as a child process.</summary>
    public static string ServerDll => FindBuilt("src", "ClrVoyant.Server", DefaultTfm);

    public static string SampleAppSrc => Path.Combine(RepoRoot, "samples", "SampleApp", "Program.cs");

    // Windows-only target: its build exists only on Windows.
    public static string WinFormsAppDll => FindBuiltDll("WinFormsApp", "net8.0-windows");
    public static string WinFormsAppSrc => Path.Combine(RepoRoot, "samples", "WinFormsApp", "Program.cs");

    /// <summary>The 1-based line marked with BREAKPOINT-TARGET in SampleApp.</summary>
    public static int BreakpointLine => _breakpointLine.Value;

    /// <summary>
    /// Why a debuggee built for <paramref name="tfm"/> cannot run on this machine, or
    /// null when the matching shared runtime is installed. A debuggee runs on the
    /// runtime it targets (no roll-forward to a newer major by default), so a missing
    /// runtime means the test must be skipped, not that something is broken.
    /// </summary>
    public static string? SkipReasonForRuntime(string tfm)
    {
        if (!TryParseMajor(tfm, out int major))
            throw new ArgumentException($"Unrecognized target framework '{tfm}'.", nameof(tfm));
        return _installedRuntimeMajors.Value.Contains(major)
            ? null
            : $"The .NET {major} runtime is not installed on this machine, so a {tfm} debuggee cannot run.";
    }

    static bool TryParseMajor(string tfm, out int major)
    {
        major = 0;
        if (!tfm.StartsWith("net", StringComparison.Ordinal)) return false;
        int dot = tfm.IndexOf('.');
        return dot > 3 && int.TryParse(tfm.AsSpan(3, dot - 3), out major);
    }

    static IReadOnlyList<int> ListInstalledRuntimeMajors()
    {
        var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return []; }

        // Lines look like: "Microsoft.NETCore.App 10.0.7 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"
        var majors = new HashSet<int>();
        foreach (var line in stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] != "Microsoft.NETCore.App") continue;
            int dot = parts[1].IndexOf('.');
            if (dot > 0 && int.TryParse(parts[1].AsSpan(0, dot), out int major)) majors.Add(major);
        }
        return majors.ToList();
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClrVoyant.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (ClrVoyant.slnx) not found");
    }

    /// <summary>
    /// The configuration this test assembly was built with (the parent folder of its
    /// target-framework folder: .../bin/&lt;configuration&gt;/&lt;tfm&gt;/), so a Release run debugs
    /// the Release samples it just built rather than a stale Debug build.
    /// </summary>
    static string? OwnConfiguration()
    {
        var tfmDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var cfgDir = tfmDir.Parent;
        return cfgDir?.Parent?.Name == "bin" ? cfgDir.Name : null;
    }

    static string FindBuiltDll(string project, string tfm) => FindBuilt("samples", project, tfm);

    static string FindBuilt(string folder, string project, string tfm)
    {
        var configs = new List<string>();
        if (OwnConfiguration() is { } own) configs.Add(own);
        foreach (var cfg in new[] { "Debug", "Release" })
            if (!configs.Contains(cfg)) configs.Add(cfg);

        foreach (var cfg in configs)
        {
            var p = Path.Combine(RepoRoot, folder, project, "bin", cfg, tfm, $"{project}.dll");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(
            $"{project}.dll for {tfm} is not built (looked in configurations: {string.Join(", ", configs)}); " +
            "it is built by the test project's own build, so rebuild the tests.");
    }

    static int FindBreakpointLine()
    {
        var lines = File.ReadAllLines(SampleAppSrc);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains("BREAKPOINT-TARGET"))
                return i + 1;
        throw new InvalidOperationException("BREAKPOINT-TARGET marker not found in SampleApp.");
    }
}
