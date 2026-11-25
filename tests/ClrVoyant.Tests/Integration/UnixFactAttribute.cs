namespace ClrVoyant.Tests.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Linux/macOS, for behaviour that exists only
/// there (Unix file modes, the Linux DAC). Reported as skipped on Windows rather than silently passing; the
/// Linux CI leg is the one that runs it.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only: the behaviour under test does not exist on Windows.";
    }
}
