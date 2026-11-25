namespace ClrVoyant.Core;

/// <summary>
/// One window of a result that can be larger than what a tool should hand to an LLM. Every
/// bounded tool (list_tasks, get_async_graph, get_variables, list_methods) returns this shape.
/// <paramref name="Total"/> is the number of matching items when the source could be counted,
/// or null when it cannot (a debugger variables request has no total). <paramref name="NextOffset"/>
/// is the <c>offset</c> to pass to get the following window; it is null when nothing more is known
/// to exist, so <c>Truncated</c> is exactly <c>NextOffset is not null</c>. On the wire a null is
/// omitted (the server does not serialise nulls), so those two keys are simply absent then.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int? Total, bool Truncated, int? NextOffset)
{
    /// <summary>Window over a source that was fetched with one extra item as a lookahead:
    /// <paramref name="fetched"/> holds at most <paramref name="max"/> + 1 items starting at
    /// <paramref name="offset"/>, and a surplus item means more exist. The total is unknown.</summary>
    public static Page<T> FromLookahead(IReadOnlyList<T> fetched, int offset, int max)
    {
        Paging.Validate(offset, max);
        bool more = fetched.Count > max;
        var items = more ? fetched.Take(max).ToList() : fetched;
        return new Page<T>(items, null, more, more ? offset + max : null);
    }
}

/// <summary>Limits and argument checks shared by every bounded tool.</summary>
public static class Paging
{
    /// <summary>Default window size for tools that enumerate runtime objects.</summary>
    public const int DefaultMax = 100;

    /// <summary>Hard upper bound of a window: the point of paging is to keep one answer small.</summary>
    public const int MaxPageSize = 1000;

    /// <exception cref="ArgumentException"><paramref name="offset"/> or <paramref name="max"/> is out of range.</exception>
    public static void Validate(int offset, int max)
    {
        if (offset < 0)
            throw new ArgumentException($"'offset' must be 0 or greater (got {offset}).");
        if (max < 1 || max > MaxPageSize)
            throw new ArgumentException($"'max' must be between 1 and {MaxPageSize} (got {max}).");
    }
}

/// <summary>
/// Collects one window out of a source that is walked completely, so the total is exact while
/// only the items inside the window are kept — and, with <see cref="Add(Func{T})"/>, only those
/// are even built. Use it where the source can be counted cheaply but each item is costly.
/// </summary>
public sealed class PageBuilder<T>
{
    readonly int _offset;
    readonly int _max;
    readonly List<T> _items = new();
    int _total;

    public PageBuilder(int offset, int max)
    {
        Paging.Validate(offset, max);
        _offset = offset;
        _max = max;
    }

    /// <summary>Count one matching item; <paramref name="make"/> runs only if it falls inside the window.</summary>
    public void Add(Func<T> make)
    {
        if (_total >= _offset && _items.Count < _max) _items.Add(make());
        _total++;
    }

    public void Add(T item) => Add(() => item);

    public Page<T> Build()
    {
        int end = _offset + _items.Count;
        bool more = end < _total;
        return new Page<T>(_items, _total, more, more ? end : null);
    }
}
