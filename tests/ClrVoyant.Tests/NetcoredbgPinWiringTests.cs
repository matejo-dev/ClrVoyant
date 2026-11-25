using ClrVoyant.Server;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

/// <summary>
/// The pinned netcoredbg version and hashes live once, in Directory.Build.props. The build-time
/// fetch reads them as MSBuild properties and names its stamp file after the version; the runtime
/// fetch reads them from assembly metadata that the server csproj copies from the same properties.
/// The other fetcher tests stub that metadata, so this is the one test that reads the real
/// assembly: a misspelled or missing metadata item would otherwise only fail on a user's machine,
/// on the first download.
/// </summary>
public sealed class NetcoredbgPinWiringTests : IDisposable
{
    readonly string _cacheRoot = Path.Combine(Path.GetTempPath(), $"clrvoyant-pin-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void The_server_assembly_carries_the_version_the_build_fetched_for_this_OS()
    {
        // The engine the build staged for this OS, and the version its stamp file is named after.
        string builtEngine = TestPaths.Netcoredbg;
        string exeName = Path.GetFileName(builtEngine);
        string stamp = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(builtEngine)!, "netcoredbg-*.stamp"));
        string stampName = Path.GetFileName(stamp);
        string builtVersion = stampName["netcoredbg-".Length..^".stamp".Length];

        // A complete user cache for exactly that version, and fetching forbidden: the real
        // metadata must lead the runtime fetch to this directory, or it has nowhere to go.
        string cacheDir = Path.Combine(_cacheRoot, "netcoredbg", builtVersion);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, exeName), "");
        File.WriteAllText(Path.Combine(cacheDir, stampName), builtVersion);

        var env = new Dictionary<string, string?>
        {
            ["CLRVOYANT_CACHE_DIR"] = _cacheRoot,
            ["CLRVOYANT_NO_FETCH"] = "1",
        };

        string resolved = NetcoredbgFetcher.EnsureInUserCache(env.GetValueOrDefault);

        Assert.Equal(Path.Combine(cacheDir, exeName), resolved);
    }
}
