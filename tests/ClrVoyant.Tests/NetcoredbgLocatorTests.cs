using System.Runtime.InteropServices;
using ClrVoyant.Server;

namespace ClrVoyant.Tests;

public class NetcoredbgLocatorTests
{
    const string EnvVar = "CLRVOYANT_NETCOREDBG";

    static string ExeName => OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

    static string BundledPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "netcoredbg", ExeName);

    static string? CurrentRid => (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, Architecture.X64) => "win-x64",
        (false, Architecture.X64) => "linux-x64",
        (false, Architecture.Arm64) => "linux-arm64",
        _ => null,
    };

    [Fact]
    public void Resolve_prefers_per_rid_bundle_over_flat()
    {
        // The packaged tool bundles every RID under tools/netcoredbg/<rid>/; the
        // locator must pick the current host's RID even when a flat copy also exists.
        if (CurrentRid is not { } rid) return; // unsupported host: nothing to assert

        var prev = Environment.GetEnvironmentVariable(EnvVar);
        var root = Path.Combine(AppContext.BaseDirectory, "tools", "netcoredbg");
        var flat = Path.Combine(root, ExeName);
        var ridPath = Path.Combine(root, rid, ExeName);
        bool createdFlat = false, createdRid = false;
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            if (!File.Exists(flat)) { Directory.CreateDirectory(root); File.WriteAllText(flat, "flat"); createdFlat = true; }
            if (!File.Exists(ridPath)) { Directory.CreateDirectory(Path.GetDirectoryName(ridPath)!); File.WriteAllText(ridPath, "rid"); createdRid = true; }
            Assert.Equal(ridPath, NetcoredbgLocator.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
            if (createdRid) File.Delete(ridPath);
            if (createdFlat) File.Delete(flat);
        }
    }

    [Fact]
    public void Resolve_prefers_env_var_when_file_exists()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ncdbg_{Guid.NewGuid():N}.exe");
        File.WriteAllText(tmp, "x");
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, tmp);
            Assert.Equal(tmp, NetcoredbgLocator.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Resolve_falls_back_to_bundled_copy()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        var bundled = BundledPath;
        bool created = false;
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            if (!File.Exists(bundled))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
                File.WriteAllText(bundled, "x");
                created = true;
            }
            Assert.Equal(bundled, NetcoredbgLocator.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
            if (created) File.Delete(bundled);
        }
    }

    [Fact]
    public void Resolve_throws_when_not_found_and_fetch_disabled()
    {
        // With no env override, no bundled copy, an empty cache, and fetching
        // disabled (CLRVOYANT_NO_FETCH), resolution fails with a clear error rather
        // than downloading.
        var prevEnv = Environment.GetEnvironmentVariable(EnvVar);
        var prevCache = Environment.GetEnvironmentVariable("CLRVOYANT_CACHE_DIR");
        var prevNoFetch = Environment.GetEnvironmentVariable("CLRVOYANT_NO_FETCH");
        var bundled = BundledPath;
        var backup = bundled + ".bak";
        bool moved = false;
        var emptyCache = Path.Combine(Path.GetTempPath(), $"clrvoyant-cache-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, "C:\\does\\not\\exist\\netcoredbg.exe");
            Environment.SetEnvironmentVariable("CLRVOYANT_CACHE_DIR", emptyCache);
            Environment.SetEnvironmentVariable("CLRVOYANT_NO_FETCH", "1");
            if (File.Exists(bundled)) { File.Move(bundled, backup, true); moved = true; }
            Assert.Throws<FileNotFoundException>(() => NetcoredbgLocator.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prevEnv);
            Environment.SetEnvironmentVariable("CLRVOYANT_CACHE_DIR", prevCache);
            Environment.SetEnvironmentVariable("CLRVOYANT_NO_FETCH", prevNoFetch);
            if (moved) File.Move(backup, bundled, true);
            try { Directory.Delete(emptyCache, true); } catch { }
        }
    }
}
