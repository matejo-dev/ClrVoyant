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
/// </summary>
internal static class NetcoredbgFetcher
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static string EnsureInUserCache()
    {
        string version = Meta("NetcoredbgVersion") ?? throw new InvalidOperationException("netcoredbg version metadata missing.");
        bool win = OperatingSystem.IsWindows();
        string exeName = win ? "netcoredbg.exe" : "netcoredbg";

        // Pick the OS+architecture asset and its pinned checksum. Only the
        // combinations netcoredbg publishes (and we validate) are supported; the
        // running process arch is what matters, since the engine debugs locally.
        (string asset, string? sha) = (win, RuntimeInformation.ProcessArchitecture) switch
        {
            (true,  Architecture.X64)   => ("netcoredbg-win64.zip",          Meta("NetcoredbgSha256Windows")),
            (false, Architecture.X64)   => ("netcoredbg-linux-amd64.tar.gz", Meta("NetcoredbgSha256Linux")),
            (false, Architecture.Arm64) => ("netcoredbg-linux-arm64.tar.gz", Meta("NetcoredbgSha256LinuxArm64")),
            _ => throw new PlatformNotSupportedException(
                $"ClrVoyant supports win-x64, linux-x64 and linux-arm64; this host is {RuntimeInformation.RuntimeIdentifier}. " +
                "Set CLRVOYANT_NETCOREDBG to a netcoredbg you provide."),
        };
        if (sha is null) throw new InvalidOperationException("netcoredbg checksum metadata missing.");

        string cacheRoot = Environment.GetEnvironmentVariable("CLRVOYANT_CACHE_DIR") ?? "";
        if (string.IsNullOrWhiteSpace(cacheRoot))
            cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clrvoyant");
        string cacheDir = Path.Combine(cacheRoot, "netcoredbg", version);
        string exe = Path.Combine(cacheDir, exeName);
        if (File.Exists(exe)) return exe;

        // Opt-out for air-gapped / locked-down environments: cache-only, never download.
        var noFetch = Environment.GetEnvironmentVariable("CLRVOYANT_NO_FETCH");
        if (!string.IsNullOrEmpty(noFetch) && noFetch != "0" && !noFetch.Equals("false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "netcoredbg is not cached and CLRVOYANT_NO_FETCH is set. Provide it via CLRVOYANT_NETCOREDBG or pre-populate the cache.");

        string tmp = Path.Combine(Path.GetTempPath(), $"clrvoyant-ncdbg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            string archive = Path.Combine(tmp, asset);
            string url = $"https://github.com/Samsung/netcoredbg/releases/download/{version}/{asset}";
            // Synchronous on purpose: this runs once, on first-run engine resolution
            // (a sync path) before the host starts, with no SynchronizationContext —
            // so blocking on the one-time download here cannot deadlock.
            using (var resp = Http.GetAsync(url).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var fs = File.Create(archive);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }

            string got = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
            if (!string.Equals(got, sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"netcoredbg checksum mismatch for {asset}: expected {sha}, got {got}.");

            string extract = Path.Combine(tmp, "x");
            Directory.CreateDirectory(extract);
            if (win)
            {
                ZipFile.ExtractToDirectory(archive, extract);
            }
            else
            {
                using var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, extract, overwriteFiles: true);
            }

            // The archive nests files under a netcoredbg/ folder; find the binary and
            // promote its directory to the cache.
            string src = Directory.EnumerateFiles(extract, exeName, SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException($"{exeName} not found inside {asset}.");
            string srcDir = Path.GetDirectoryName(src)!;
            CopyDirectory(srcDir, cacheDir);

            if (!win) File.SetUnixFileMode(exe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return exe;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    static string? Meta(string key) => typeof(NetcoredbgFetcher).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == key)?.Value;
}
