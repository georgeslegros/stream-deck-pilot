using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;

namespace StreamDeckPilot.Tests.Mqtt;

public class InboundPipelineTests
{
    // ── Extract ───────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_JsonPayload_ReturnsFieldValue()
    {
        var (value, unit) = InboundPipeline.Extract("""{"value":1023,"unit":"ppm"}""", "value", "unit");
        Assert.Equal("1023", value);
        Assert.Equal("ppm", unit);
    }

    [Fact]
    public void Extract_NestedJsonPath_ReturnsDeepValue()
    {
        var (value, _) = InboundPipeline.Extract("""{"sensor":{"value":21.5}}""", "sensor.value", null);
        Assert.Equal("21.5", value);
    }

    [Fact]
    public void Extract_BareString_ReturnsPayloadAsValue()
    {
        var (value, unit) = InboundPipeline.Extract("42.7", null, null);
        Assert.Equal("42.7", value);
        Assert.Null(unit);
    }

    [Fact]
    public void Extract_InvalidJson_FallsBackToRawPayload()
    {
        var (value, _) = InboundPipeline.Extract("not-json", "value", null);
        Assert.Equal("not-json", value);
    }

    // ── EvaluateRules ─────────────────────────────────────────────────────────

    private static IReadOnlyList<ConditionalRule> Co2Rules =>
    [
        new(">1000", "#FF0000", null),
        new(">800",  "#FF8800", null),
        new(">=0",   "#00AA00", null),
    ];

    [Theory]
    [InlineData("1200", "#FF0000")]
    [InlineData("900",  "#FF8800")]
    [InlineData("400",  "#00AA00")]
    public void EvaluateRules_FirstMatchWins(string value, string expectedColour)
    {
        var (colour, _) = InboundPipeline.EvaluateRules(value, Co2Rules);
        Assert.Equal(expectedColour, colour);
    }

    [Fact]
    public void EvaluateRules_NoMatch_ReturnsNulls()
    {
        var rules = new[] { new ConditionalRule(">100", "#FF0000", null) };
        var (colour, icon) = InboundPipeline.EvaluateRules("50", rules);
        Assert.Null(colour);
        Assert.Null(icon);
    }

    [Fact]
    public void EvaluateRules_BetweenCondition_Matches()
    {
        var rules = new[] { new ConditionalRule("between:20:25", "#0000FF", null) };
        var (colour, _) = InboundPipeline.EvaluateRules("22", rules);
        Assert.Equal("#0000FF", colour);
    }

    [Fact]
    public void EvaluateRules_BetweenCondition_OutOfRange_NoMatch()
    {
        var rules = new[] { new ConditionalRule("between:20:25", "#0000FF", null) };
        var (colour, _) = InboundPipeline.EvaluateRules("30", rules);
        Assert.Null(colour);
    }

    [Fact]
    public void EvaluateRules_NonNumericValue_ReturnsNulls()
    {
        var (colour, _) = InboundPipeline.EvaluateRules("n/a", Co2Rules);
        Assert.Null(colour);
    }

    // ── FormatValue ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1023", 0, "1023")]
    [InlineData("21.567", 1, "21.6")]
    [InlineData("21.567", 2, "21.57")]
    [InlineData("not-a-number", 1, "not-a-number")]
    public void FormatValue_VariousPrecisions(string input, int precision, string expected)
    {
        Assert.Equal(expected, InboundPipeline.FormatValue(input, precision));
    }

    // ── ComposeLabel ──────────────────────────────────────────────────────────

    [Fact]
    public void ComposeLabel_TemplateWithTokens()
    {
        var result = InboundPipeline.ComposeLabel("{value} {unit}", "21.6", "°C", null);
        Assert.Equal("21.6 °C", result);
    }

    [Fact]
    public void ComposeLabel_NullTemplate_FallsBackToStaticLabel()
    {
        var result = InboundPipeline.ComposeLabel(null, "21.6", "°C", "Temperature");
        Assert.Equal("Temperature", result);
    }

    [Fact]
    public void ComposeLabel_NullTemplate_NullStaticLabel_UsesValue()
    {
        var result = InboundPipeline.ComposeLabel(null, "21.6", null, null);
        Assert.Equal("21.6", result);
    }
}
