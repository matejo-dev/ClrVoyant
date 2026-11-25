using System.Runtime.InteropServices;

namespace ClrVoyant.Server;

/// <summary>
/// Resolves the netcoredbg executable, in order:
///   1. the CLRVOYANT_NETCOREDBG env var (explicit override: when set, the file must exist,
///      or resolution fails; it never falls through to the other sources);
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
    internal const string OverrideVariable = "CLRVOYANT_NETCOREDBG";

    public static string Resolve()
        => Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable, CurrentRid());

    /// <summary>The resolution itself, over its inputs: the directory the server runs from, the
    /// environment, and the RID whose bundle to prefer (null: none).</summary>
    internal static string Resolve(string baseDirectory, Func<string, string?> env, string? currentRid)
    {
        // An explicit override is a promise: when it is wrong the server must say so, not
        // quietly run some other engine than the one the operator asked for.
        var overridePath = env(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath)) return overridePath;
            throw new FileNotFoundException(
                $"{OverrideVariable} is set to '{overridePath}', but no file exists there. " +
                "Point it to the netcoredbg executable, or unset it to use the bundled engine.");
        }

        string exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        string root = Path.Combine(baseDirectory, "tools", "netcoredbg");

        // Packaged layout: the tool bundles every supported RID under its own subdir.
        if (currentRid is { } rid)
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
            return NetcoredbgFetcher.EnsureInUserCache(env);
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                $"netcoredbg not found and could not be fetched. Set {OverrideVariable} to its full path. " +
                $"({ex.GetType().Name}: {ex.Message})", ex);
        }
    }

    /// <summary>The .NET RID of the running host, limited to the supported set; null
    /// on any other OS/architecture (the bundled per-RID layout won't have a match).</summary>
    internal static string? CurrentRid() => (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
    {
        (true, Architecture.X64) => "win-x64",
        (false, Architecture.X64) => "linux-x64",
        (false, Architecture.Arm64) => "linux-arm64",
        _ => null,
    };
}
