using ClrVoyant.Core;

namespace ClrVoyant.Tests;

public class BreakpointLocatorTests
{
    static readonly string[] Lines =
    {
        "public void Foo()",          // 1
        "{",                          // 2
        "    var x = Compute();",     // 3
        "    Log(x);",                // 4
        "    var x = Compute();",     // 5 (duplicate of line 3)
        "}",                          // 6
    };

    [Fact]
    public void Exact_trimmed_match_wins()
        => Assert.Equal(4, BreakpointLocator.ResolveLine(Lines, "Log(x);"));

    [Fact]
    public void Ignores_surrounding_whitespace_in_query()
        => Assert.Equal(4, BreakpointLocator.ResolveLine(Lines, "   Log(x);   "));

    [Fact]
    public void Substring_match_when_no_exact_line()
        => Assert.Equal(1, BreakpointLocator.ResolveLine(Lines, "void Foo"));

    [Fact]
    public void Ambiguous_match_uses_nearest_to_hint()
    {
        Assert.Equal(3, BreakpointLocator.ResolveLine(Lines, "var x = Compute();", lineHint: 2));
        Assert.Equal(5, BreakpointLocator.ResolveLine(Lines, "var x = Compute();", lineHint: 6));
    }

    [Fact]
    public void Ambiguous_match_without_hint_throws_listing_candidates()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BreakpointLocator.ResolveLine(Lines, "var x = Compute();"));
        Assert.Contains("3", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void No_match_throws()
        => Assert.Throws<InvalidOperationException>(() => BreakpointLocator.ResolveLine(Lines, "nonexistent"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_content_throws(string content)
        => Assert.Throws<ArgumentException>(() => BreakpointLocator.ResolveLine(Lines, content));
}
