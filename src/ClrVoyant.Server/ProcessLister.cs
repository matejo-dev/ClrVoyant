using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using ClrVoyant.Core;

namespace ClrVoyant.Server;

/// <summary>
/// One row of the process table. <paramref name="StartStamp"/> orders processes by creation
/// time (UTC ticks on Windows, clock ticks since boot on Linux): comparable only between
/// entries of the same enumeration, 0 when unknown. Together with the PID it identifies a
/// process, because PIDs are recycled. <paramref name="Name"/> is the executable name without
/// extension (the /proc comm on Linux, so at most 15 characters there);
/// <paramref name="CommandLine"/> is filled only when the enumeration was asked for it.
/// </summary>
internal readonly record struct ProcessEntry(
    int Pid, int ParentPid, long StartStamp, string? Name = null, string? CommandLine = null);

/// <summary>
/// Cross-OS process enumeration: lists running processes with their parent PID,
/// creation time, name, command line and whether they are .NET (Core). Used by
/// <see cref="ChildProcessWatcher"/> for auto-attach and by the list_processes tool so an
/// agent can find a target to debug_attach (e.g. the app process in a shared-PID-namespace POD).
/// </summary>
internal static class ProcessLister
{
    /// <summary>Longest command line reported; the rest is cut and marked with "...".</summary>
    internal const int MaxCommandLineChars = 1000;

    public static IReadOnlyList<ProcessInfo> List(bool dotnetOnly)
        => List(dotnetOnly, () => Enumerate(includeCommandLine: true), IsDotNet);

    // The process table and the .NET check are parameters so the filtering and the shape of the
    // result can be tested without depending on what runs on the machine. The check is the slow
    // part (on Windows it enumerates the modules of the process; measured ~5 s for ~600
    // processes when run one after the other), and independent per process, so it runs in parallel.
    internal static IReadOnlyList<ProcessInfo> List(bool dotnetOnly,
        Func<IEnumerable<ProcessEntry>> enumerate, Func<int, bool> isDotNet)
    {
        var table = enumerate().ToArray();
        var net = new bool[table.Length];
        Parallel.For(0, table.Length, i => net[i] = isDotNet(table[i].Pid));

        var result = new List<ProcessInfo>();
        for (int i = 0; i < table.Length; i++)
        {
            if (dotnetOnly && !net[i]) continue;
            var p = table[i];
            result.Add(new ProcessInfo(p.Pid, p.ParentPid, p.Name ?? "?", net[i], p.CommandLine));
        }
        return result;
    }

    public static IEnumerable<ProcessEntry> Enumerate(bool includeCommandLine = false)
        => OperatingSystem.IsWindows() ? EnumerateWindows(includeCommandLine) : EnumerateLinux(includeCommandLine);

    [SupportedOSPlatform("windows")]
    static IEnumerable<ProcessEntry> EnumerateWindows(bool includeCommandLine)
    {
        string columns = includeCommandLine
            ? "ProcessId, ParentProcessId, CreationDate, Name, CommandLine"
            : "ProcessId, ParentProcessId, CreationDate, Name";
        using var searcher = new ManagementObjectSearcher($"SELECT {columns} FROM Win32_Process");
        foreach (ManagementBaseObject mo in searcher.Get())
        {
            int pid = ToInt(mo["ProcessId"]);
            if (pid <= 0) continue;
            yield return new ProcessEntry(pid, ToInt(mo["ParentProcessId"]), ToStartStamp(mo["CreationDate"]),
                WithoutExeExtension(mo["Name"] as string),
                includeCommandLine ? Truncate(mo["CommandLine"] as string) : null);
        }
    }

    [SupportedOSPlatform("windows")]
    static long ToStartStamp(object? creationDate)
    {
        // WMI reports a DMTF string; a protected process may report none.
        try { return creationDate is string s ? ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime().Ticks : 0; }
        catch (ArgumentException) { return 0; }
    }

    // WMI names carry the extension ("dotnet.exe"); Process.ProcessName, which this reported
    // before, does not.
    internal static string? WithoutExeExtension(string? name)
        => name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    static IEnumerable<ProcessEntry> EnumerateLinux(bool includeCommandLine)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            int ppid;
            long start;
            string? name;
            string? commandLine = null;
            try
            {
                if (!TryParseStat(File.ReadAllText($"/proc/{pid}/stat"), out name, out ppid, out start)) continue;
                if (includeCommandLine) commandLine = FromProcCmdline(ReadCmdline(pid));
            }
            catch { continue; } // process vanished / not readable
            yield return new ProcessEntry(pid, ppid, start, name, commandLine);
        }
    }

    // A process that exits between the stat read and this one (or hides its cmdline) has none.
    static string ReadCmdline(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline"); }
        catch { return ""; }
    }

    /// <summary>Parse <c>/proc/&lt;pid&gt;/stat</c> into the command name (field 2), the parent PID
    /// (field 4) and the start time in clock ticks since boot (field 22). The name is in
    /// parentheses and may itself contain spaces and parentheses, so it ends at the LAST ')' and
    /// the remaining fields are counted from there.</summary>
    internal static bool TryParseStat(string stat, out string name, out int parentPid, out long startTicks)
    {
        name = "";
        parentPid = 0;
        startTicks = 0;
        int open = stat.IndexOf('(');
        int close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return false;
        name = stat[(open + 1)..close];
        // After the ')': state is field 3, so field N is at index N - 3.
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19
            && int.TryParse(fields[1], out parentPid)
            && long.TryParse(fields[19], out startTicks);
    }

    /// <summary>Turn the NUL-separated contents of <c>/proc/&lt;pid&gt;/cmdline</c> into one
    /// space-separated line; null for an empty one (kernel threads, zombies).</summary>
    internal static string? FromProcCmdline(string raw)
        => Truncate(raw.TrimEnd('\0').Replace('\0', ' '));

    internal static string? Truncate(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        string trimmed = commandLine.Trim();
        return trimmed.Length <= MaxCommandLineChars ? trimmed : trimmed[..MaxCommandLineChars] + "...";
    }

    // Heuristic: a process is .NET (Core) if the CoreCLR runtime is loaded.
    public static bool IsDotNet(int pid)
        => OperatingSystem.IsWindows() ? IsDotNetWindows(pid) : IsDotNetLinux(pid);

    [SupportedOSPlatform("windows")]
    static bool IsDotNetWindows(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule m in p.Modules)
                if (m.ModuleName is "coreclr.dll" or "hostpolicy.dll") return true;
        }
        catch { /* access denied / exited / bitness mismatch */ }
        return false;
    }

    static bool IsDotNetLinux(int pid)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{pid}/maps"))
                if (line.Contains("libcoreclr.so", StringComparison.Ordinal)) return true;
        }
        catch { /* access denied / exited */ }
        return false;
    }

    static int ToInt(object? o) => o is null ? 0 : Convert.ToInt32(o);
}
