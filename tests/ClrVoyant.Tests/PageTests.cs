using ClrVoyant.Core;

namespace ClrVoyant.Tests;

/// <summary>The window every bounded tool returns: what it keeps, what it counts, what it says is left.</summary>
public class PageTests
{
    static PageBuilder<int> Fill(int count, int offset, int max)
    {
        var b = new PageBuilder<int>(offset, max);
        for (int i = 0; i < count; i++) b.Add(i);
        return b;
    }

    [Fact]
    public void A_source_larger_than_the_window_keeps_the_window_counts_everything_and_points_at_the_rest()
    {
        var page = Fill(count: 10, offset: 2, max: 3).Build();

        Assert.Equal([2, 3, 4], page.Items);
        Assert.Equal(10, page.Total);
        Assert.True(page.Truncated);
        Assert.Equal(5, page.NextOffset);
    }

    [Fact]
    public void The_last_window_is_not_truncated_and_has_no_next_offset()
    {
        var page = Fill(count: 10, offset: 8, max: 5).Build();

        Assert.Equal([8, 9], page.Items);
        Assert.Equal(10, page.Total);
        Assert.False(page.Truncated);
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public void A_window_that_exactly_reaches_the_end_is_not_truncated()
    {
        var page = Fill(count: 4, offset: 0, max: 4).Build();

        Assert.Equal(4, page.Items.Count);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void An_item_outside_the_window_is_counted_but_never_built()
    {
        var built = new List<int>();
        var b = new PageBuilder<int>(offset: 1, max: 1);
        for (int i = 0; i < 5; i++) { int n = i; b.Add(() => { built.Add(n); return n; }); }

        Assert.Equal([1], built); // the expensive part runs for the window only
        Assert.Equal(5, b.Build().Total);
    }

    [Fact]
    public void A_lookahead_item_means_more_exist_but_the_total_stays_unknown()
    {
        var page = Page<string>.FromLookahead(["a", "b", "c"], offset: 4, max: 2);

        Assert.Equal(["a", "b"], page.Items);
        Assert.Null(page.Total);
        Assert.True(page.Truncated);
        Assert.Equal(6, page.NextOffset);
    }

    [Fact]
    public void Without_a_lookahead_item_the_page_is_complete()
    {
        var page = Page<string>.FromLookahead(["a", "b"], offset: 0, max: 2);

        Assert.Equal(["a", "b"], page.Items);
        Assert.False(page.Truncated);
        Assert.Null(page.NextOffset);
    }

    [Theory]
    [InlineData(-1, 10, "'offset'")]
    [InlineData(0, 0, "'max'")]
    [InlineData(0, -5, "'max'")]
    [InlineData(0, Paging.MaxPageSize + 1, "'max'")]
    public void An_out_of_range_window_is_refused_with_the_limit_stated(int offset, int max, string named)
    {
        var fromBuilder = Assert.Throws<ArgumentException>(() => new PageBuilder<int>(offset, max));
        Assert.Contains(named, fromBuilder.Message);
        Assert.Throws<ArgumentException>(() => Page<int>.FromLookahead([], offset, max));
    }

    [Fact]
    public void The_largest_allowed_window_is_accepted()
        => Assert.Empty(new PageBuilder<int>(0, Paging.MaxPageSize).Build().Items);
}
