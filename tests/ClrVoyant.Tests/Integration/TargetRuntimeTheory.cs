using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace ClrVoyant.Tests.Integration;

/// <summary>
/// A theory over the target runtimes (<see cref="TestPaths.SampleTfms"/>), passed to the
/// test as its single <c>string tfm</c> argument. A row whose runtime is not installed
/// is reported as skipped with the reason, never as silently passed; with
/// <c>CLRVOYANT_REQUIRE_TARGET_RUNTIMES=1</c> (set in CI) it is a failure instead.
/// </summary>
[XunitTestCaseDiscoverer("ClrVoyant.Tests.Integration.TargetRuntimeTheoryDiscoverer", "ClrVoyant.Tests")]
public sealed class TargetRuntimeTheoryAttribute : TheoryAttribute;

/// <summary>Supplies one row per sample target framework.</summary>
[DataDiscoverer("Xunit.Sdk.DataDiscoverer", "xunit.core")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class AllTargetRuntimesAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod) =>
        TestPaths.SampleTfms.Select(tfm => new object[] { tfm });
}

/// <summary>Turns data rows for uninstalled runtimes into skipped test cases.</summary>
public sealed class TargetRuntimeTheoryDiscoverer : TheoryDiscoverer
{
    public TargetRuntimeTheoryDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink) { }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo theoryAttribute, object[] dataRow)
    {
        if (dataRow is [string tfm] && TestPaths.SkipReasonForRuntime(tfm) is { } reason)
        {
            // CI installs every target runtime and sets this, so a missing runtime there
            // is a broken pipeline (a red test), not coverage quietly turning into skips.
            if (Environment.GetEnvironmentVariable("CLRVOYANT_REQUIRE_TARGET_RUNTIMES") == "1")
            {
                return
                [
                    new ExecutionErrorTestCase(
                        DiagnosticMessageSink,
                        discoveryOptions.MethodDisplayOrDefault(),
                        discoveryOptions.MethodDisplayOptionsOrDefault(),
                        testMethod,
                        $"{reason} CLRVOYANT_REQUIRE_TARGET_RUNTIMES=1 forbids skipping it."),
                ];
            }
            return
            [
                new XunitSkippedDataRowTestCase(
                    DiagnosticMessageSink,
                    discoveryOptions.MethodDisplayOrDefault(),
                    discoveryOptions.MethodDisplayOptionsOrDefault(),
                    testMethod, reason, dataRow),
            ];
        }
        return base.CreateTestCasesForDataRow(discoveryOptions, testMethod, theoryAttribute, dataRow);
    }
}
