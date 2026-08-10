using ExecMcp.Core;
namespace ExecMcp.Tests;

public sealed class DurationTests
{
    [Theory]
    [InlineData("15", 15)]
    [InlineData("250ms", 250)]
    [InlineData("1.5s", 1500)]
    [InlineData("2m", 120000)]
    [InlineData("1h", 3600000)]
    [InlineData("1d", 86400000)]
    public void Parse_AcceptsSupportedDurations(string text, int expected) => Assert.Equal(expected, DurationParser.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("1w")]
    [InlineData("999999999d")]
    public void Parse_RejectsInvalidOrOverflowingDurations(string text) => Assert.ThrowsAny<Exception>(() => DurationParser.Parse(text));
}
