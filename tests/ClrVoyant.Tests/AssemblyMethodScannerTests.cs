using ClrVoyant.Inspection;
using ClrVoyant.Tests.Integration;

namespace ClrVoyant.Tests;

public class AssemblyMethodScannerTests
{
    [Fact]
    public void Lists_a_real_method_by_full_name()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, typeFilter: "Calc");
        // Calc.Compute is the named-type method the function-breakpoint test breaks on;
        // its FullName must be exactly what set_function_breakpoint accepts.
        Assert.Contains(methods, m => m.FullName == "Calc.Compute" && m.DeclaringType == "Calc" && m.Method == "Compute");
    }

    [Fact]
    public void Method_filter_narrows_results()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, methodFilter: "Compute");
        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Contains("Compute", m.Method, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Skips_compiler_generated_members()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll);
        Assert.NotEmpty(methods);
        // Local functions / state machines (mangled '<...>' names) are not breakable
        // by name, so they must never appear.
        Assert.DoesNotContain(methods, m => m.Method.Contains('<'));
    }

    [Fact]
    public void Respects_the_max_cap()
    {
        var methods = AssemblyMethodScanner.ListMethods(TestPaths.SampleAppDll, max: 1);
        Assert.Single(methods);
    }

    [Fact]
    public void Throws_a_clear_error_for_a_missing_file()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.dll");
        Assert.Throws<FileNotFoundException>(() => AssemblyMethodScanner.ListMethods(missing));
    }
}
