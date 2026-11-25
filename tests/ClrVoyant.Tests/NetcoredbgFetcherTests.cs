using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClrVoyant.Server;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

/// <summary>
/// The first-run download of netcoredbg: the SHA-256 check is the supply-chain control, and the cache
/// it fills is trusted on every later start. Everything runs on archives built here, in temp
/// directories, through the fetcher's download seam: no network, nothing outside the temp directory.
/// </summary>
public sealed class NetcoredbgFetcherTests : IDisposable
{
    const string Version = "9.9.9-test";

    readonly string _root = Path.Combine(Path.GetTempPath(), $"clrvoyant-fetch-{Guid.NewGuid():N}");
    readonly string _cacheRoot;
    readonly Dictionary<string, string?> _env = new();

    public NetcoredbgFetcherTests()
    {
        Directory.CreateDirectory(_root);
        _cacheRoot = Path.Combine(_root, "cache");
        _env["CLRVOYANT_CACHE_DIR"] = _cacheRoot;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- fixtures ----

    sealed record Platform(bool Windows, Architecture Arch, string Asset, string ExeName, string ShaKey);

    public static IEnumerable<object[]> Platforms() =>
    [
        [true, Architecture.X64],    // zip
        [false, Architecture.X64],   // tar.gz
        [false, Architecture.Arm64], // tar.gz
    ];

    static Platform For(bool windows, Architecture arch) => (windows, arch) switch
    {
        (true, _) => new(true, arch, "netcoredbg-win64.zip", "netcoredbg.exe", "NetcoredbgSha256Windows"),
        (false, Architecture.X64) => new(false, arch, "netcoredbg-linux-amd64.tar.gz", "netcoredbg", "NetcoredbgSha256Linux"),
        _ => new(false, arch, "netcoredbg-linux-arm64.tar.gz", "netcoredbg", "NetcoredbgSha256LinuxArm64"),
    };

    /// <summary>Build an archive in the platform's format, shaped like upstream's (everything under netcoredbg/).</summary>
    string BuildArchive(Platform p, bool withEngine = true)
    {
        string path = Path.Combine(_root, $"src-{Guid.NewGuid():N}-{p.Asset}");
        var entries = new List<(string name, string content)>
        {
            ("netcoredbg/libdbgshim.so", "shim"),
            ("netcoredbg/sub/ManagedPart.dll", "managed"),
        };
        if (withEngine) entries.Add(($"netcoredbg/{p.ExeName}", "engine"));

        if (p.Asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        else
        {
            using var gz = new GZipStream(File.Create(path), CompressionLevel.Fastest);
            using var tar = new TarWriter(gz);
            foreach (var (name, content) in entries)
            {
                // Mode 0644 even for the engine: the fetcher itself must make it executable.
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    Mode = (UnixFileMode)0b110_100_100,
                });
            }
        }
        return path;
    }

    static string Sha(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    sealed class Source(string archive)
    {
        public List<Uri> Downloads { get; } = new();
        public Action? DuringDownload { get; init; }

        public void Download(Uri url, string destination)
        {
            Downloads.Add(url);
            DuringDownload?.Invoke();
            File.Copy(archive, destination);
        }
    }

    static Func<string, string?> Meta(Platform p, string sha) => key =>
        key == "NetcoredbgVersion" ? Version : key == p.ShaKey ? sha : "unused-for-this-platform";

    string Ensure(Platform p, string sha, Action<Uri, string> download)
        => NetcoredbgFetcher.EnsureInUserCache(k => _env.GetValueOrDefault(k), Meta(p, sha), p.Windows, p.Arch, download);

    string CacheDir => Path.Combine(_cacheRoot, "netcoredbg", Version);

    static void WriteCompleteCache(string dir, string exeName, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, exeName), content);
        File.WriteAllText(Path.Combine(dir, NetcoredbgFetcher.StampName(Version)), Version);
    }

    // ---- a correct download ----

    [Theory]
    [MemberData(nameof(Platforms))]
    public void A_matching_archive_is_extracted_into_a_stamped_cache(bool windows, Architecture arch)
    {
        var p = For(windows, arch);
        string archive = BuildArchive(p);
        var source = new Source(archive);

        string exe = Ensure(p, Sha(archive), source.Download);

        Assert.Equal(Path.Combine(CacheDir, p.ExeName), exe);
        Assert.Equal("engine", File.ReadAllText(exe));
        Assert.Equal("shim", File.ReadAllText(Path.Combine(CacheDir, "libdbgshim.so")));
        Assert.Equal("managed", File.ReadAllText(Path.Combine(CacheDir, "sub", "ManagedPart.dll")));
        Assert.True(File.Exists(Path.Combine(CacheDir, NetcoredbgFetcher.StampName(Version))));
        // The pinned version and the right asset for the platform were asked for.
        Assert.Equal(new Uri($"https://github.com/Samsung/netcoredbg/releases/download/{Version}/{p.Asset}"), Assert.Single(source.Downloads));
        // Nothing but the cache itself is left behind: the work directory is gone.
        Assert.Equal([CacheDir], Directory.GetDirectories(Path.Combine(_cacheRoot, "netcoredbg")));
    }

    [Fact]
    public void The_hash_is_accepted_in_either_case()
    {
        var p = For(false, Architecture.X64);
        string archive = BuildArchive(p);

        Ensure(p, Sha(archive).ToUpperInvariant(), new Source(archive).Download);

        Assert.True(File.Exists(Path.Combine(CacheDir, p.ExeName)));
    }

    [UnixFact]
    public void The_engine_is_made_executable_even_when_the_archive_does_not_say_so()
    {
        if (OperatingSystem.IsWindows()) return; // [UnixFact] skips it there; this also tells the platform analyzer
        var p = For(false, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.Arm64 : Architecture.X64);
        string archive = BuildArchive(p);

        string exe = Ensure(p, Sha(archive), new Source(archive).Download);

        var mode = File.GetUnixFileMode(exe);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute), mode.ToString());
    }

    // ---- a wrong download fails closed ----

    [Fact]
    public void A_wrong_hash_extracts_nothing_and_leaves_no_cache()
    {
        var p = For(false, Architecture.X64);
        string archive = BuildArchive(p);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Ensure(p, new string('0', 64), new Source(archive).Download));

        Assert.Contains("checksum mismatch", ex.Message);
        Assert.Contains(Sha(archive), ex.Message);
        Assert.False(Directory.Exists(CacheDir));
        // ...and not even the work directory.
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_cacheRoot, "netcoredbg")));
    }

    [Fact]
    public void A_wrong_hash_does_not_replace_an_unstamped_cache()
    {
        // Verification comes before anything in the cache is touched.
        var p = For(false, Architecture.X64);
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(Path.Combine(CacheDir, p.ExeName), "old");
        string archive = BuildArchive(p);

        Assert.Throws<InvalidOperationException>(() => Ensure(p, new string('0', 64), new Source(archive).Download));

        Assert.Equal("old", File.ReadAllText(Path.Combine(CacheDir, p.ExeName)));
    }

    [Fact]
    public void An_archive_without_the_engine_is_rejected_and_leaves_no_cache()
    {
        var p = For(false, Architecture.X64);
        string archive = BuildArchive(p, withEngine: false);

        var ex = Assert.Throws<InvalidOperationException>(() => Ensure(p, Sha(archive), new Source(archive).Download));

        Assert.Contains("not found inside", ex.Message);
        Assert.False(Directory.Exists(CacheDir));
    }

    [Fact]
    public void A_failed_download_leaves_no_cache_and_no_work_directory()
    {
        var p = For(false, Architecture.X64);

        Assert.Throws<HttpRequestException>(() => Ensure(p, "00", (_, _) => throw new HttpRequestException("offline")));

        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_cacheRoot, "netcoredbg")));
    }

    // ---- what a cache must hold to be trusted ----

    [Fact]
    public void A_complete_cache_is_used_without_downloading()
    {
        var p = For(false, Architecture.X64);
        WriteCompleteCache(CacheDir, p.ExeName, "cached");

        string exe = Ensure(p, "unused", (_, _) => throw new InvalidOperationException("must not download"));

        Assert.Equal("cached", File.ReadAllText(exe));
    }

    [Theory]
    [InlineData(true, false)]  // the engine without the stamp: an interrupted copy, or a cache from before stamps
    [InlineData(false, true)]  // the stamp without the engine
    public void A_partial_cache_is_not_trusted_and_is_replaced(bool engine, bool stamp)
    {
        var p = For(false, Architecture.X64);
        Directory.CreateDirectory(CacheDir);
        if (engine) File.WriteAllText(Path.Combine(CacheDir, p.ExeName), "half-written");
        if (stamp) File.WriteAllText(Path.Combine(CacheDir, NetcoredbgFetcher.StampName(Version)), Version);
        File.WriteAllText(Path.Combine(CacheDir, "stale.bin"), "left by the interrupted run");
        string archive = BuildArchive(p);
        var source = new Source(archive);

        string exe = Ensure(p, Sha(archive), source.Download);

        Assert.Single(source.Downloads);
        Assert.Equal("engine", File.ReadAllText(exe));
        Assert.False(File.Exists(Path.Combine(CacheDir, "stale.bin")), "the partial cache was merged into, not replaced");
        Assert.True(File.Exists(Path.Combine(CacheDir, NetcoredbgFetcher.StampName(Version))));
    }

    [Fact]
    public void A_cache_of_another_version_is_a_different_directory_and_does_not_count()
    {
        var p = For(false, Architecture.X64);
        string other = Path.Combine(_cacheRoot, "netcoredbg", "1.0.0-old");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, p.ExeName), "old engine");
        File.WriteAllText(Path.Combine(other, "netcoredbg-1.0.0-old.stamp"), "1.0.0-old");
        string archive = BuildArchive(p);

        string exe = Ensure(p, Sha(archive), new Source(archive).Download);

        Assert.Equal(CacheDir, Path.GetDirectoryName(exe));
        Assert.Equal("old engine", File.ReadAllText(Path.Combine(other, p.ExeName))); // untouched
    }

    [Fact]
    public void Losing_the_race_to_another_server_keeps_the_winners_finished_cache()
    {
        var p = For(false, Architecture.X64);
        string archive = BuildArchive(p);
        // While this server downloads, another one finishes its own copy into the same place.
        var source = new Source(archive) { DuringDownload = () => WriteCompleteCache(CacheDir, p.ExeName, "winner") };

        string exe = Ensure(p, Sha(archive), source.Download);

        Assert.Equal("winner", File.ReadAllText(exe));
        Assert.Equal([CacheDir], Directory.GetDirectories(Path.Combine(_cacheRoot, "netcoredbg"))); // our work dir is gone
    }

    [Fact]
    public void A_half_written_cache_that_appears_during_the_download_is_replaced()
    {
        var p = For(false, Architecture.X64);
        string archive = BuildArchive(p);
        var source = new Source(archive)
        {
            DuringDownload = () =>
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(Path.Combine(CacheDir, p.ExeName), "half");
            },
        };

        string exe = Ensure(p, Sha(archive), source.Download);

        Assert.Equal("engine", File.ReadAllText(exe));
    }

    // ---- CLRVOYANT_NO_FETCH ----

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    public void No_fetch_is_set_by_anything_but_empty_zero_or_false(string? value, bool expected)
    {
        Assert.Equal(expected, NetcoredbgFetcher.IsNoFetch(value));
    }

    [Fact]
    public void No_fetch_refuses_to_download_and_says_what_the_cache_must_hold()
    {
        var p = For(false, Architecture.X64);
        _env["CLRVOYANT_NO_FETCH"] = "1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => Ensure(p, "unused", (_, _) => throw new InvalidOperationException("must not download")));

        Assert.Contains("CLRVOYANT_NO_FETCH", ex.Message);
        Assert.Contains(CacheDir, ex.Message);
        Assert.Contains(NetcoredbgFetcher.StampName(Version), ex.Message);
    }

    [Fact]
    public void No_fetch_still_uses_a_complete_cache()
    {
        var p = For(false, Architecture.X64);
        _env["CLRVOYANT_NO_FETCH"] = "1";
        WriteCompleteCache(CacheDir, p.ExeName, "cached");

        Assert.Equal("cached", File.ReadAllText(Ensure(p, "unused", (_, _) => throw new InvalidOperationException())));
    }

    [Fact]
    public void No_fetch_set_to_zero_downloads_as_usual()
    {
        var p = For(false, Architecture.X64);
        _env["CLRVOYANT_NO_FETCH"] = "0";
        string archive = BuildArchive(p);
        var source = new Source(archive);

        Ensure(p, Sha(archive), source.Download);

        Assert.Single(source.Downloads);
    }

    // ---- what to download, and where to keep it ----

    [Theory]
    [InlineData(true, Architecture.Arm64)]   // netcoredbg publishes no win-arm64
    [InlineData(true, Architecture.X86)]
    [InlineData(false, Architecture.Arm)]
    [InlineData(false, Architecture.X86)]
    public void An_unsupported_platform_is_refused_with_the_supported_set_and_the_way_out(bool windows, Architecture arch)
    {
        var ex = Assert.Throws<PlatformNotSupportedException>(
            () => NetcoredbgFetcher.SelectAsset(windows, arch, _ => "sha"));

        Assert.Contains("win-x64, linux-x64 and linux-arm64", ex.Message);
        Assert.Contains(arch.ToString(), ex.Message);
        Assert.Contains("CLRVOYANT_NETCOREDBG", ex.Message);
    }

    [Theory]
    [InlineData(true, Architecture.X64, "netcoredbg-win64.zip", "NetcoredbgSha256Windows")]
    [InlineData(false, Architecture.X64, "netcoredbg-linux-amd64.tar.gz", "NetcoredbgSha256Linux")]
    [InlineData(false, Architecture.Arm64, "netcoredbg-linux-arm64.tar.gz", "NetcoredbgSha256LinuxArm64")]
    public void Each_supported_platform_gets_its_own_asset_and_its_own_pinned_hash(
        bool windows, Architecture arch, string asset, string shaKey)
    {
        var (gotAsset, gotSha) = NetcoredbgFetcher.SelectAsset(windows, arch, key => $"hash-of-{key}");

        Assert.Equal(asset, gotAsset);
        Assert.Equal($"hash-of-{shaKey}", gotSha);
    }

    [Fact]
    public void Missing_build_metadata_is_reported_not_ignored()
    {
        var p = For(false, Architecture.X64);

        var noVersion = Assert.Throws<InvalidOperationException>(() => NetcoredbgFetcher.EnsureInUserCache(
            k => _env.GetValueOrDefault(k), _ => null, p.Windows, p.Arch, (_, _) => { }));
        var noChecksum = Assert.Throws<InvalidOperationException>(() => NetcoredbgFetcher.EnsureInUserCache(
            k => _env.GetValueOrDefault(k), k => k == "NetcoredbgVersion" ? Version : null, p.Windows, p.Arch, (_, _) => { }));

        Assert.Contains("version", noVersion.Message);
        Assert.Contains("checksum", noChecksum.Message);
    }

    [Fact]
    public void The_cache_lives_where_CLRVOYANT_CACHE_DIR_says_or_else_in_the_user_profile()
    {
        Assert.Equal(_cacheRoot, NetcoredbgFetcher.CacheRoot(k => _env.GetValueOrDefault(k)));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clrvoyant"),
            NetcoredbgFetcher.CacheRoot(_ => " "));
    }
}
