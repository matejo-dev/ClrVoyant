using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClrVoyant.Server;

/// <summary>
/// Downloads the pinned netcoredbg for the current OS into a per-user cache, on
/// first run. This is the fallback used when ClrVoyant is installed as a .NET global
/// tool (a single cross-platform package can't bundle a per-OS native binary).
/// Build/publish/container layouts ship netcoredbg next to the server, so this path
/// is not taken there. Version + SHA-256 are baked into the assembly at build time
/// (see the AssemblyMetadata in the csproj) — the same pinned, verified coordinates
/// as the build-time fetch.
///
/// The cache is populated atomically: the archive is verified and extracted in a sibling
/// work directory, the version stamp is written last, and the finished directory is renamed
/// into place. A cache is trusted only when the engine AND the stamp are there (the same
/// completeness rule as the build-time fetch scripts, so an interrupted run or a concurrent
/// server never leaves a half-written engine that is trusted forever).
/// </summary>
internal static class NetcoredbgFetcher
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static string EnsureInUserCache(Func<string, string?> env)
        => EnsureInUserCache(env, Meta,
            OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture, Download);

    /// <summary>The whole fetch, with everything outside the verify-and-extract logic passed in:
    /// the environment, the build-time metadata, the platform, and <paramref name="download"/>
    /// (url, destination file) which is the only network access.</summary>
    internal static string EnsureInUserCache(Func<string, string?> env, Func<string, string?> meta,
        bool windows, Architecture arch, Action<Uri, string> download)
    {
        // The pin is per platform (see Directory.Build.props), so the version follows the
        // RUNNING OS, not the OS the package was built on.
        string version = meta(windows ? "NetcoredbgVersionWindows" : "NetcoredbgVersionLinux")
            ?? throw new InvalidOperationException("netcoredbg version metadata missing.");
        var (asset, sha) = SelectAsset(windows, arch, meta);
        string exeName = windows ? "netcoredbg.exe" : "netcoredbg";

        string cacheDir = Path.Combine(CacheRoot(env), "netcoredbg", version);
        string exe = Path.Combine(cacheDir, exeName);
        // NOTE: the stamp gates trust. A cache directory holding the engine but not
        // `netcoredbg-<version>.stamp` (filled by hand, or by a release that wrote no stamp) is
        // NOT complete: it is refetched below, and under CLRVOYANT_NO_FETCH resolution fails
        // instead of using it. That is deliberate (an interrupted extraction must never be trusted)
        // but air-gapped users with a hand-filled cache must add the stamp or use CLRVOYANT_NETCOREDBG.
        if (IsComplete(cacheDir, exeName, version)) return exe;

        // Opt-out for air-gapped / locked-down environments: cache-only, never download.
        if (IsNoFetch(env("CLRVOYANT_NO_FETCH")))
            throw new InvalidOperationException(
                "netcoredbg is not cached and CLRVOYANT_NO_FETCH is set. Provide it via CLRVOYANT_NETCOREDBG or pre-populate the cache " +
                $"({cacheDir} must hold {exeName} and {StampName(version)}).");

        Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
        // A sibling of the cache directory, so the final rename stays on one file system.
        // NOTE: the `finally` below removes this directory on every path that runs it, but a
        // process that is killed mid-fetch never gets there and leaves it behind. Nothing sweeps
        // `*.tmp` siblings of the cache directory, so such leftovers accumulate (never trusted,
        // only disk space). Add a sweep (e.g. delete stale `<version>.*.tmp` before creating this
        // one) if that ever matters.
        string work = $"{cacheDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(work);
        try
        {
            string archive = Path.Combine(work, asset);
            download(new Uri($"https://github.com/Samsung/netcoredbg/releases/download/{version}/{asset}"), archive);
            string src = VerifyAndExtract(archive, sha, asset, Path.Combine(work, "x"), exeName);
            File.WriteAllText(Path.Combine(src, StampName(version)), version); // last: marks the engine complete

            if (Directory.Exists(cacheDir))
            {
                if (IsComplete(cacheDir, exeName, version)) return exe; // another server finished first
                // Left by an interrupted run (or by a version of this tool that did not stamp): replace it.
                try { Directory.Delete(cacheDir, recursive: true); } catch (DirectoryNotFoundException) { }
            }
            try { Directory.Move(src, cacheDir); }
            catch (IOException) when (IsComplete(cacheDir, exeName, version)) { /* lost the race to another server */ }
            return exe;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Check the archive against the pinned SHA-256 (failing closed: nothing is extracted
    /// on a mismatch), extract it under <paramref name="extractDir"/> and return the directory
    /// holding the engine, made executable on Unix. The upstream archive nests the files under a
    /// netcoredbg/ folder.</summary>
    internal static string VerifyAndExtract(string archive, string sha256, string asset, string extractDir, string exeName)
    {
        string got;
        using (var stream = File.OpenRead(archive))
            got = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"netcoredbg checksum mismatch for {asset}: expected {sha256}, got {got}.");

        Directory.CreateDirectory(extractDir);
        if (asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, extractDir);
        }
        else
        {
            using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, extractDir, overwriteFiles: true);
        }

        string exe = Directory.EnumerateFiles(extractDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"{exeName} not found inside {asset}.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return Path.GetDirectoryName(exe)!;
    }

    /// <summary>The OS+architecture asset and its pinned checksum. Only the combinations
    /// netcoredbg publishes (and we validate) are supported; the running process arch is what
    /// matters, since the engine debugs locally.</summary>
    internal static (string asset, string sha256) SelectAsset(bool windows, Architecture arch, Func<string, string?> meta)
    {
        (string asset, string key) = (windows, arch) switch
        {
            (true,  Architecture.X64)   => ("netcoredbg-win64.zip",          "NetcoredbgSha256Windows"),
            (false, Architecture.X64)   => ("netcoredbg-linux-amd64.tar.gz", "NetcoredbgSha256Linux"),
            (false, Architecture.Arm64) => ("netcoredbg-linux-arm64.tar.gz", "NetcoredbgSha256LinuxArm64"),
            _ => throw new PlatformNotSupportedException(
                $"ClrVoyant supports win-x64, linux-x64 and linux-arm64; this host is {(windows ? "Windows" : "a non-Windows OS")} on {arch}. " +
                "Set CLRVOYANT_NETCOREDBG to a netcoredbg you provide."),
        };
        return (asset, meta(key) ?? throw new InvalidOperationException("netcoredbg checksum metadata missing."));
    }

    /// <summary>True when CLRVOYANT_NO_FETCH asks for cache-only: set to anything but empty, 0 or false.</summary>
    internal static bool IsNoFetch(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim() != "0"
            && !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    internal static string CacheRoot(Func<string, string?> env)
    {
        string? root = env("CLRVOYANT_CACHE_DIR");
        return string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clrvoyant")
            : root;
    }

    internal static string StampName(string version) => $"netcoredbg-{version}.stamp";

    static bool IsComplete(string cacheDir, string exeName, string version)
        => File.Exists(Path.Combine(cacheDir, exeName)) && File.Exists(Path.Combine(cacheDir, StampName(version)));

    // Synchronous on purpose: it runs inside the engine factory, a sync delegate, on a thread
    // pool thread with no SynchronizationContext, so blocking on the one-time download cannot deadlock.
    static void Download(Uri url, string destination)
    {
        using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var fs = File.Create(destination);
        resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
    }

    static string? Meta(string key) => typeof(NetcoredbgFetcher).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;
}
