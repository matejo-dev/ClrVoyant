using ClrVoyant.Core;

namespace ClrVoyant.Tests.Fakes;

/// <summary>
/// The engine factory a <see cref="SessionManager"/> is built with in tests: every session gets a
/// <see cref="FakeDebugEngine"/>, kept in <see cref="Engines"/> (in creation order) so the test can
/// drive its events and read what it was asked to do.
/// </summary>
public sealed class FakeEngineFactory
{
    readonly object _gate = new();
    readonly List<FakeDebugEngine> _engines = new();

    /// <summary>Applied to each engine right after it is created, before any session uses it.</summary>
    public Action<FakeDebugEngine>? Customize { get; set; }

    public IReadOnlyList<FakeDebugEngine> Engines { get { lock (_gate) return _engines.ToArray(); } }

    public IDebugEngine Create()
    {
        var engine = new FakeDebugEngine();
        Customize?.Invoke(engine);
        lock (_gate) _engines.Add(engine);
        return engine;
    }
}
