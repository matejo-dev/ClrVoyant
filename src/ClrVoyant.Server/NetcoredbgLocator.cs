namespace ClrVoyant.Server;

/// <summary>
/// Resolves the netcoredbg executable, in order:
///   1. the CLRVOYANT_NETCOREDBG env var (explicit override);
///   2. a bundled copy next to the server (dev builds, published output, container);
///   3. a per-user cache, fetching the pinned netcoredbg on first run if absent
///      (the path used when installed as a .NET global tool).
/// </summary>
internal static class NetcoredbgLocator
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("CLRVOYANT_NETCOREDBG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string exe = OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "netcoredbg", exe);
        if (File.Exists(bundled))
            return bundled;

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
}
