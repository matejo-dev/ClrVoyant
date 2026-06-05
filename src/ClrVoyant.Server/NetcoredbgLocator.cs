using System.Runtime.InteropServices;

namespace ClrVoyant.Server;

/// <summary>
/// Resolves the netcoredbg executable, in order:
///   1. the CLRVOYANT_NETCOREDBG env var (explicit override);
///   2. a bundled copy next to the server, per-RID — tools/netcoredbg/&lt;rid&gt;/ —
///      which is how the .NET global tool package ships (it bundles every supported
///      RID so the install works offline, with no runtime download);
///   3. a bundled copy in the flat tools/netcoredbg/ layout — dev build output, the
///      container image, and the spike all bundle a single RID flat;
///   4. a per-user cache, fetching the pinned netcoredbg on first run if absent
///      (a last-resort fallback for hosts where no copy was bundled).
/// </summary>
internal static class NetcoredbgLocator
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("CLRVOYANT_NETCOREDBG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        string root = Path.Combine(AppContext.BaseDirectory, "tools", "netcoredbg");

        // Packaged layout: the tool bundles every supported RID under its own subdir.
        if (CurrentRid() is { } rid)
        {
            var ridPath = Path.Combine(root, rid, exe);
            if (File.Exists(ridPath))
                return ridPath;
        }

        // Flat layout: a single-RID bundle (dev build output, container, spike).
        var flat = Path.Combine(root, exe);
        if (File.Exists(flat))
            return flat;

        try
        {
            return NetcoredbgFetcher.EnsureInUserCache();
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "netcoredbg not found and could not be fetched. Set CLRVOYANT_NETCOREDBG to its full path. " +
                $"({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    /// <summary>The .NET RID of the running host, limited to the supported set; null
    /// on any other OS/architecture (the bundled per-RID layout won't have a match).</summary>
    static string? CurrentRid() => (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, Architecture.X64) => "win-x64",
        (false, Architecture.X64) => "linux-x64",
        (false, Architecture.Arm64) => "linux-arm64",
        _ => null,
    };
}
