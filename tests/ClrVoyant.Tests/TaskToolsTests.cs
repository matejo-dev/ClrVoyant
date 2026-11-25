using ClrVoyant.Server.Tools;

namespace ClrVoyant.Tests;

public class TaskToolsTests
{
    [Theory]
    [InlineData("0x1f", 0x1fUL)]
    [InlineData("0X1F", 0x1fUL)]
    [InlineData("0x1c3a8f0", 0x1c3a8f0UL)]
    [InlineData("31", 31UL)]
    [InlineData("0", 0UL)]
    [InlineData("0xffffffffffffffff", ulong.MaxValue)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void A_hex_or_decimal_heap_address_is_parsed(string text, ulong expected)
        => Assert.Equal(expected, TaskTools.ParseAddress(text));

    [Theory]
    [InlineData("")]
    [InlineData("0x")]
    [InlineData("xyz")]
    [InlineData("0xzz")]
    [InlineData("-1")]
    [InlineData("1f")]                     // hex digits without the 0x prefix are not decimal
    [InlineData(" 0x1f")]
    [InlineData("0x10000000000000000")]    // one past ulong
    [InlineData("18446744073709551616")]
    public void Anything_else_is_refused_with_the_accepted_formats(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => TaskTools.ParseAddress(text));
        Assert.Equal($"'{text}' is not a heap address: pass hex (0x1c3...) or decimal, as returned by list_tasks.", ex.Message);
    }
}
