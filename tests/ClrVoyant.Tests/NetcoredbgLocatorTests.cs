using ClrVoyant.Server;

namespace ClrVoyant.Tests;

public class NetcoredbgLocatorTests
{
    const string EnvVar = "CLRVOYANT_NETCOREDBG";

    static string BundledPath =>
        Path.Combine(AppContext.BaseDirectory, "tools", "netcoredbg",
            OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg");

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
