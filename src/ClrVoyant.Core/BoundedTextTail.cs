using System.Text;

namespace ClrVoyant.Core;

/// <summary>
/// Thread-safe text buffer that keeps only the most recent <c>capacity</c> characters,
/// dropping the oldest when full. Used wherever the useful part of a stream is its
/// end and an unbounded buffer would be a leak: the adapter's stderr, and a debuggee's
/// captured stdout/stderr.
/// </summary>
public sealed class BoundedTextTail
{
    readonly object _gate = new();
    readonly StringBuilder _text = new();
    readonly int _capacity;
    bool _dropped;

    public BoundedTextTail(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    /// <summary>True once any text has been discarded to stay within the capacity.</summary>
    public bool Dropped { get { lock (_gate) return _dropped; } }

    public void Append(string text)
    {
        if (text.Length == 0) return;
        lock (_gate)
        {
            _text.Append(text);
            if (_text.Length > _capacity)
            {
                _text.Remove(0, _text.Length - _capacity);
                _dropped = true;
            }
        }
    }

    /// <summary>The last <paramref name="maxChars"/> characters (or everything, if shorter).</summary>
    public string GetTail(int maxChars)
    {
        lock (_gate)
        {
            int take = Math.Min(Math.Max(maxChars, 0), _text.Length);
            return _text.ToString(_text.Length - take, take);
        }
    }

    public int Length { get { lock (_gate) return _text.Length; } }

    public void Clear()
    {
        lock (_gate) { _text.Clear(); _dropped = false; }
    }
}
