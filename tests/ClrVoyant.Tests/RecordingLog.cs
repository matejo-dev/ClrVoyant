using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ClrVoyant.Tests;

/// <summary>Collects what is logged, as formatted lines, for tests that assert on log output. Usable
/// directly as an <see cref="ILogger"/>, or as a provider on a host's logging builder.</summary>
sealed class RecordingLog : ILogger, ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message);

    readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public IEnumerable<string> Lines(string category) => Entries.Where(e => e.Category == category).Select(e => e.Message);

    public ILogger CreateLogger(string categoryName) => new Scoped(categoryName, _entries);
    public void Dispose() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new Entry("", logLevel, formatter(state, exception)));

    sealed class Scoped(string category, ConcurrentQueue<Entry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => sink.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
    }
}
