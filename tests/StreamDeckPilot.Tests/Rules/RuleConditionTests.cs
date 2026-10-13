using StreamDeckPilot.Core.Rules;

namespace StreamDeckPilot.Tests.Rules;

public class RuleConditionTests
{
    private static bool Matches(string condition, string? value)
    {
        Assert.True(RuleCondition.TryParse(condition, out var parsed, out var error), error);
        return parsed!.Matches(value);
    }

    [Theory]
    [InlineData(">10", "11", true)]
    [InlineData(">10", "10", false)]
    [InlineData(">=10", "10", true)]
    [InlineData("<10", "9.5", true)]
    [InlineData("<=10", "10", true)]
    [InlineData("==10", "10.0", true)]
    [InlineData("> 10", "11", true)]
    [InlineData(">-5", "-4", true)]
    [InlineData("between:18:24", "18", true)]
    [InlineData("between:18:24", "24.1", false)]
    public void Numeric(string condition, string value, bool expected) =>
        Assert.Equal(expected, Matches(condition, value));

    [Theory]
    [InlineData(">10")]
    [InlineData("==1")]
    [InlineData("between:0:100")]
    public void Numeric_NeverMatchesText(string condition) =>
        Assert.False(Matches(condition, "unavailable"));

    [Theory]
    [InlineData("eq:on", "on", true)]
    [InlineData("eq:on", "ON", true)]           // case-insensitive
    [InlineData("eq:on", " on \n", true)]       // trimmed
    [InlineData("EQ: on", "on", true)]
    [InlineData("eq:on", "off", false)]
    [InlineData("eq:1", "1", true)]             // numbers compare as their text
    [InlineData("eq:1", "1.0", false)]
    [InlineData("eq:a:b", "a:b", true)]         // everything after the prefix is the value
    [InlineData("ne:unavailable", "on", true)]
    [InlineData("ne:unavailable", "Unavailable", false)]
    [InlineData("in:heat,cool", "cool", true)]
    [InlineData("in: heat , cool ", "HEAT", true)]
    [InlineData("in:heat,cool", "off", false)]
    public void Text(string condition, string value, bool expected) =>
        Assert.Equal(expected, Matches(condition, value));

    [Theory]
    [InlineData("on")]
    [InlineData("42")]
    [InlineData("")]
    public void Any_MatchesEveryValue(string value) =>
        Assert.True(Matches("any", value));

    [Theory]
    [InlineData("any")]
    [InlineData("ne:off")]
    [InlineData(">=0")]
    public void NoValue_NeverMatches(string condition) =>
        Assert.False(Matches(condition, null));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("on", "unknown condition")]
    [InlineData(">abc", "needs a number")]
    [InlineData("==on", "eq:on")]               // points at the text form
    [InlineData("between:5", "between:A:B")]
    [InlineData("between:9:1", "never match")]
    [InlineData("eq:", "needs a value")]
    [InlineData("in: , ", "at least one value")]
    public void Invalid_ReportsWhy(string condition, string expectedFragment)
    {
        Assert.False(RuleCondition.TryParse(condition, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }
}
