namespace ClrVoyant.Tests.Integration;

/// <summary>Locates repo artifacts (netcoredbg, sample targets) for integration tests.</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Netcoredbg { get; } =
        Path.Combine(RepoRoot, "tools", "netcoredbg",
            OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");

    public static string SampleAppDll { get; } = FindBuiltDll("SampleApp");
    public static string ParentAppDll { get; } = FindBuiltDll("ParentApp");
    public static string SampleAppSrc { get; } =
        Path.Combine(RepoRoot, "samples", "SampleApp", "Program.cs");

    // Windows-only target: resolved on access (not at type init, like the others), so
    // the suite still loads on Linux, where it is never built.
    public static string WinFormsAppDll => FindBuiltDll("WinFormsApp", "net8.0-windows");
    public static string WinFormsAppSrc { get; } =
        Path.Combine(RepoRoot, "samples", "WinFormsApp", "Program.cs");

    /// <summary>The 1-based line marked with BREAKPOINT-TARGET in SampleApp.</summary>
    public static int BreakpointLine { get; } = FindBreakpointLine();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClrVoyant.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (ClrVoyant.slnx) not found");
    }

    static string FindBuiltDll(string project, string tfm = "net8.0")
    {
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var p = Path.Combine(RepoRoot, "samples", project, "bin", cfg, tfm, $"{project}.dll");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException($"{project}.dll not built; build the solution first.");
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
