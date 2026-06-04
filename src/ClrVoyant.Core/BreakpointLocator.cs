namespace ClrVoyant.Core;

/// <summary>
/// Resolves a source line by its text content rather than a raw line number, so a
/// breakpoint survives the agent miscounting lines or the file shifting since it
/// was last read. Pure (no file IO) to stay unit-testable; the caller reads the
/// file and passes the lines in.
/// </summary>
public static class BreakpointLocator
{
    /// <summary>Find the 1-based line whose content matches <paramref name="content"/>.
    /// Matching is tried in order of decreasing strictness: exact trimmed equality,
    /// then trimmed substring, then raw substring. When several lines match and a
    /// <paramref name="lineHint"/> is given, the nearest to the hint wins; otherwise
    /// an ambiguous match throws with the candidate lines so the caller can refine.</summary>
    /// <exception cref="InvalidOperationException">No line matches, or several do and no hint disambiguates.</exception>
    public static int ResolveLine(IReadOnlyList<string> lines, string content, int? lineHint = null)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("Breakpoint content to match must be non-empty.", nameof(content));

        string target = content.Trim();

        var matches = Find(lines, l => l.Trim() == target);
        if (matches.Count == 0) matches = Find(lines, l => l.Trim().Contains(target, StringComparison.Ordinal));
        if (matches.Count == 0) matches = Find(lines, l => l.Contains(target, StringComparison.Ordinal));

        if (matches.Count == 0)
            throw new InvalidOperationException($"No source line matches content \"{target}\".");
        if (matches.Count == 1)
            return matches[0];

        if (lineHint is int hint)
            return matches.OrderBy(m => Math.Abs(m - hint)).First();

        throw new InvalidOperationException(
            $"Content \"{target}\" matches {matches.Count} lines ({string.Join(", ", matches)}); " +
            "pass a 'line' hint to pick the intended one.");
    }

    static List<int> Find(IReadOnlyList<string> lines, Func<string, bool> predicate)
    {
        var hits = new List<int>();
        for (int i = 0; i < lines.Count; i++)
            if (predicate(lines[i]))
                hits.Add(i + 1); // 1-based
        return hits;
    }
}
