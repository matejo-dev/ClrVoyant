using ClrVoyant.Inspection;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

public class AssemblyMethodScannerTests
{
    [Fact]
    public void Lists_a_real_method_by_full_name()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, typeFilter: "Calc").Items;
        // Calc.Compute is the named-type method the function-breakpoint test breaks on;
        // its FullName must be exactly what set_function_breakpoint accepts.
        Assert.Contains(methods, m => m.FullName == "Calc.Compute" && m.DeclaringType == "Calc" && m.Method == "Compute");
    }

    [Fact]
    public void Method_filter_narrows_results()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, methodFilter: "Compute").Items;
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Contains("Compute", m.Method, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skips_compiler_generated_members()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll).Items;
        Assert.NotEmpty(methods);
        // Local functions / state machines (mangled '<...>' names) are not breakable
        // by name, so they must never appear.
        Assert.DoesNotContain(methods, m => m.Method.Contains('<'));
    }

    [Fact]
    public void A_capped_page_says_it_is_truncated_and_where_the_next_one_starts()
    {
        var all = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll).Items;
        Assert.True(all.Count >= 3, "the sample must declare enough methods to page over");

        var first = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, max: 2);

        Assert.Equal(all.Take(2), first.Items);
        Assert.Equal(all.Count, first.Total);
        Assert.True(first.Truncated);
        Assert.Equal(2, first.NextOffset);

        // Following nextOffset reaches the rest, with nothing repeated and nothing lost.
        var rest = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, max: 1000, offset: first.NextOffset!.Value);
        Assert.Equal(all.Skip(2), rest.Items);
        Assert.False(rest.Truncated);
        Assert.Null(rest.NextOffset);
    }

    [Fact]
    public void An_offset_past_the_end_is_an_empty_final_page()
    {
        var page = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, offset: 100000);
        Assert.Empty(page.Items);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void Throws_a_clear_error_for_a_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.dll");
        Assert.Throws<FileNotFoundException>(() => AssemblyMethodScanner.ListMethods(missing));
    }
}
