namespace ClrVoyant.Tests.Integration;

/// <summary>Locates repo artifacts (netcoredbg, SampleApp) for integration tests.</summary>
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

    /// <summary>The 1-based line marked with BREAKPOINT-TARGET in SampleApp.</summary>
    public static int BreakpointLine { get; } = FindBreakpointLine();

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClrVoyant.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (ClrVoyant.slnx) not found");
    }

    static string FindBuiltDll(string project)
    {
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var p = Path.Combine(RepoRoot, "samples", project, "bin", cfg, "net8.0", $"{project}.dll");
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
