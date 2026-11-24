namespace ClrVoyant.Tests.Integration;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Windows, for tests whose target is
/// inherently Windows-only (WinForms). Reported as skipped elsewhere rather than
/// silently passing.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only: the target is a WinForms app.";
    }
}
