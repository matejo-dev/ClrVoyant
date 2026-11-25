using System.Runtime.InteropServices;
using ClrVoyant.Server;

namespace ClrVoyant.Tests;

/// <summary>
/// Where the server looks for netcoredbg. Runs against a throwaway directory standing in for the
/// server's own folder and a fake environment: never the real build output, never the process
/// environment.
/// </summary>
public sealed class NetcoredbgLocatorTests : IDisposable
{
    const string Rid = "test-rid";

    static string ExeName => OperatingSystem.IsWindows() ? "netcoredbg.exe" : "netcoredbg";

    readonly string _base = Path.Combine(Path.GetTempPath(), $"clrvoyant-locator-{Guid.NewGuid():N}");
    readonly Dictionary<string, string?> _env = new();

    public NetcoredbgLocatorTests()
    {
        Directory.CreateDirectory(_base);
        // Fetching must never reach the network or the real profile from here.
        _env["CLRVOYANT_CACHE_DIR"] = Path.Combine(_base, "cache");
        _env["CLRVOYANT_NO_FETCH"] = "1";
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    string Flat => Path.Combine(_base, "tools", "netcoredbg", ExeName);
    string PerRid => Path.Combine(_base, "tools", "netcoredbg", Rid, ExeName);

    string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    string Resolve(string? rid = Rid) => NetcoredbgLocator.Resolve(_base, k => _env.GetValueOrDefault(k), rid);

    // ---- the explicit override ----

    [Fact]
    public void An_existing_override_wins_over_everything_bundled()
    {
        Touch(Flat);
        Touch(PerRid);
        string custom = Touch(Path.Combine(_base, "custom", ExeName));
        _env["CLRVOYANT_NETCOREDBG"] = custom;

        Assert.Equal(custom, Resolve());
    }

    [Fact]
    public void An_override_pointing_nowhere_fails_loudly_instead_of_using_the_bundled_copy()
    {
        Touch(Flat); // would be a perfectly good engine, and must not be used behind the operator's back
        string missing = Path.Combine(_base, "typo", ExeName);
        _env["CLRVOYANT_NETCOREDBG"] = missing;

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve());

        Assert.Contains("CLRVOYANT_NETCOREDBG", ex.Message);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void An_override_pointing_to_a_directory_is_not_an_engine()
    {
        _env["CLRVOYANT_NETCOREDBG"] = _base;

        var ex = Assert.Throws<FileNotFoundException>(() => Resolve());

        Assert.Contains(_base, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unset_or_blank_override_is_no_override(string? value)
    {
        Touch(Flat);
        _env["CLRVOYANT_NETCOREDBG"] = value;

        Assert.Equal(Flat, Resolve());
    }

    // ---- the bundled copies ----

    [Fact]
    public void The_per_rid_bundle_wins_over_the_flat_one()
    {
        // The packaged tool bundles every RID under tools/netcoredbg/<rid>/: the host's must be
        // picked even when a flat copy exists too.
        Touch(Flat);
        Touch(PerRid);

        Assert.Equal(PerRid, Resolve());
    }

    [Fact]
    public void A_bundle_for_another_rid_is_not_used()
    {
        Touch(Path.Combine(_base, "tools", "netcoredbg", "some-other-rid", ExeName));
        Touch(Flat);

        Assert.Equal(Flat, Resolve());
    }

    [Fact]
    public void Without_a_supported_rid_only_the_flat_bundle_applies()
    {
        Touch(PerRid);
        Touch(Flat);

        Assert.Equal(Flat, Resolve(rid: null));
    }

    [Fact]
    public void The_flat_bundle_is_used_when_there_is_no_per_rid_one()
    {
        Touch(Flat);

        Assert.Equal(Flat, Resolve());
    }

    // ---- nothing bundled: the user cache, or a message that says what to do ----

    [Fact]
    public void Nothing_bundled_and_fetching_disabled_names_the_override_and_the_reason()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => Resolve());

        Assert.Contains("CLRVOYANT_NETCOREDBG", ex.Message);
        Assert.Contains("CLRVOYANT_NO_FETCH", ex.Message); // the fetcher's reason travels with it
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    // ---- the RID of this host ----

    [Fact]
    public void The_rid_of_a_supported_host_matches_its_os_and_architecture()
    {
        string? rid = NetcoredbgLocator.CurrentRid();

        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Assert.Equal("win-x64", rid);
        else if (!OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            Assert.Equal("linux-x64", rid);
        else if (!OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Equal("linux-arm64", rid);
        else
            Assert.Null(rid);
    }
}
