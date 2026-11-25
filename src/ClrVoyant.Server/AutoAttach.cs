namespace ClrVoyant.Server;

/// <summary>Runtime toggle for child-process auto-attach (see <see cref="ChildProcessWatcher"/>).
/// Off by default so the server never grabs processes the agent did not ask for.</summary>
public sealed class AutoAttachOptions
{
    public volatile bool Enabled;
}
