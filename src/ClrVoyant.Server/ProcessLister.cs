using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using ClrVoyant.Core;

namespace ClrVoyant.Server;

/// <summary>
/// Cross-OS process enumeration: lists running processes with their parent PID,
/// name and whether they are .NET (Core). Used by <see cref="ChildProcessWatcher"/>
/// for auto-attach and by the list_processes tool so an agent can find a target to
/// debug_attach (e.g. the app process in a shared-PID-namespace POD).
/// </summary>
internal static class ProcessLister
{
    public static IReadOnlyList<ProcessInfo> List(bool dotnetOnly)
    {
        var result = new List<ProcessInfo>();
        foreach (var (pid, ppid) in Enumerate())
        {
            bool net = IsDotNet(pid);
            if (dotnetOnly && !net) continue;
            result.Add(new ProcessInfo(pid, ppid, GetName(pid), net));
        }
        return result;
    }

    public static IEnumerable<(int pid, int parentPid)> Enumerate()
        => OperatingSystem.IsWindows() ? EnumerateWindows() : EnumerateLinux();

    [SupportedOSPlatform("windows")]
    static IEnumerable<(int pid, int parentPid)> EnumerateWindows()
    {
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
        foreach (ManagementBaseObject mo in searcher.Get())
        {
            int pid = ToInt(mo["ProcessId"]);
            if (pid > 0) yield return (pid, ToInt(mo["ParentProcessId"]));
        }
    }

    // Linux: parse /proc/<pid>/stat. The parent PID is the 4th field, but the 2nd
    // field (comm) may contain spaces/parens, so read ppid relative to the LAST ')'.
    static IEnumerable<(int pid, int parentPid)> EnumerateLinux()
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            int ppid = 0;
            try
            {
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                int close = stat.LastIndexOf(')');
                if (close < 0) continue;
                var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2) int.TryParse(fields[1], out ppid);
            }
            catch { continue; } // process vanished / not readable
            yield return (pid, ppid);
        }
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

    static string GetName(int pid)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.GetProcessById(pid);
                return p.ProcessName;
            }
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch { return "?"; }
    }

    static int ToInt(object? o) => o is null ? 0 : Convert.ToInt32(o);
}
