using System.Diagnostics;
using ClrVoyant.Server;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

public class ProcessListerTests
{
    // ---- the real process table ----

    [Fact]
    public void Lists_self_as_a_dotnet_process_with_its_name_and_command_line()
    {
        var me = ProcessLister.List(dotnetOnly: true).SingleOrDefault(p => p.Pid == Environment.ProcessId);

        Assert.NotNull(me);
        Assert.True(me.IsDotNet);
        using var self = Process.GetCurrentProcess();
        // Linux reports the 15-character comm; Windows the full image name.
        Assert.StartsWith(me.Name, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(me.Name));
        // The test host is started as "dotnet testhost.dll ..." or "testhost.exe ...".
        Assert.Contains("testhost", me.CommandLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_spawned_child_is_listed_with_its_parent_its_later_start_and_its_command_line()
    {
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add(TestPaths.SampleAppDll);
        psi.RedirectStandardOutput = true;
        using var child = Process.Start(psi)!;
        try
        {
            var table = ProcessLister.Enumerate(includeCommandLine: true).ToList();

            var self = table.Single(p => p.Pid == Environment.ProcessId);
            var entry = table.Single(p => p.Pid == child.Id);
            Assert.Equal(Environment.ProcessId, entry.ParentPid);
            Assert.True(self.StartStamp > 0);
            Assert.True(entry.StartStamp >= self.StartStamp, "a child is never older than its parent");
            Assert.Contains("SampleApp.dll", entry.CommandLine);
            Assert.Equal("dotnet", entry.Name, ignoreCase: true);
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit(10000);
        }
    }

    [Fact]
    public void The_command_line_is_only_read_when_asked_for()
    {
        Assert.All(ProcessLister.Enumerate(), p => Assert.Null(p.CommandLine));
    }

    // ---- listing: filtering and shape, over a table the test controls ----

    static IEnumerable<ProcessEntry> Table() =>
    [
        new ProcessEntry(10, 1, 100, "dotnet", "dotnet app.dll --flag"),
        new ProcessEntry(20, 10, 200, "bash", null),
        new ProcessEntry(30, 1, 300),
    ];

    [Fact]
    public void Listing_dotnet_only_keeps_exactly_the_dotnet_processes()
    {
        var list = ProcessLister.List(true, Table, pid => pid is 10 or 30);

        Assert.Equal([10, 30], list.Select(p => p.Pid));
        Assert.All(list, p => Assert.True(p.IsDotNet));
    }

    [Fact]
    public void Listing_everything_flags_which_ones_are_dotnet_and_carries_every_field()
    {
        var list = ProcessLister.List(false, Table, pid => pid == 10);

        Assert.Equal([10, 20, 30], list.Select(p => p.Pid));
        Assert.Equal([true, false, false], list.Select(p => p.IsDotNet));
        Assert.Equal(new Core.ProcessInfo(10, 1, "dotnet", true, "dotnet app.dll --flag"), list[0]);
        Assert.Equal(new Core.ProcessInfo(20, 10, "bash", false, null), list[1]);
        Assert.Equal("?", list[2].Name); // a process whose name could not be read
    }

    // ---- /proc/<pid>/stat: the Linux branch no Windows run executes ----

    // pid (comm) state ppid pgrp session tty tpgid flags minflt cminflt majflt cmajflt utime stime
    // cutime cstime priority nice threads itrealvalue starttime ...
    static string Stat(string comm, int ppid, long start)
        => $"123 ({comm}) S {ppid} 123 123 0 -1 4194560 100 0 0 0 1 2 0 0 20 0 5 0 {start} 1000000 500 18446744073709551615";

    [Theory]
    [InlineData("dotnet")]
    [InlineData("my proc")]
    [InlineData("a (b) c")]
    [InlineData("weird)")]
    [InlineData("(")]
    public void Stat_parsing_finds_the_name_parent_and_start_whatever_the_name_contains(string comm)
    {
        Assert.True(ProcessLister.TryParseStat(Stat(comm, 456, 98765), out var name, out int ppid, out long start));

        Assert.Equal(comm, name);
        Assert.Equal(456, ppid);
        Assert.Equal(98765, start);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123 no parens S 1")]
    [InlineData("123 (short) S 1 2 3")]                                       // truncated: no start time
    [InlineData("123 (x) S notanumber 1 1 0 -1 0 0 0 0 0 1 2 0 0 20 0 5 0 7")] // ppid not a number
    public void Stat_parsing_rejects_what_is_not_a_complete_stat_line(string stat)
    {
        Assert.False(ProcessLister.TryParseStat(stat, out _, out _, out _));
    }

    // ---- /proc/<pid>/cmdline and command-line size ----

    [Fact]
    public void A_proc_cmdline_becomes_one_space_separated_line()
    {
        Assert.Equal("dotnet app.dll --flag", ProcessLister.FromProcCmdline("dotnet\0app.dll\0--flag\0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0")]
    public void An_empty_cmdline_is_no_command_line(string raw)
    {
        Assert.Null(ProcessLister.FromProcCmdline(raw));
    }

    [Fact]
    public void A_long_command_line_is_cut_and_marked_and_a_short_one_is_untouched()
    {
        string exact = new('x', ProcessLister.MaxCommandLineChars);
        string longer = exact + "tail";

        Assert.Equal(exact, ProcessLister.Truncate(exact));
        Assert.Equal(exact + "...", ProcessLister.Truncate(longer));
        Assert.Null(ProcessLister.Truncate(null));
        Assert.Null(ProcessLister.Truncate("  "));
    }

    [Theory]
    [InlineData("dotnet.exe", "dotnet")]
    [InlineData("TESTHOST.EXE", "TESTHOST")]
    [InlineData("dotnet", "dotnet")]
    [InlineData("exe", "exe")]
    [InlineData(null, null)]
    public void A_windows_image_name_loses_its_extension(string? wmi, string? expected)
    {
        Assert.Equal(expected, ProcessLister.WithoutExeExtension(wmi));
    }
}
