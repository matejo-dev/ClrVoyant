namespace ClrVoyant.Tests.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when CLRVOYANT_PACKAGING_TESTS=1.
/// Packaging tests pack the tool, install it as a .NET global tool, and drive a real
/// debug loop through it — slow and network-touching (the pack downloads the per-RID
/// netcoredbg), so they are opt-in (CI's packaging job, or locally on demand) and
/// reported as skipped otherwise rather than silently passing.
/// </summary>
public sealed class PackagingFactAttribute : FactAttribute
{
    public PackagingFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLRVOYANT_PACKAGING_TESTS") != "1")
            Skip = "Set CLRVOYANT_PACKAGING_TESTS=1 to run installed-tool packaging tests.";
    }
}
