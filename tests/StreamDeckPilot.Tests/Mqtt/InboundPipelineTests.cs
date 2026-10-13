using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Mqtt.Pipeline;

namespace StreamDeckPilot.Tests.Mqtt;

public class InboundPipelineTests
{
    // ── Extract ───────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_JsonPayload_ReturnsFieldValue()
    {
        var (value, unit, _) = InboundPipeline.Extract("""{"value":1023,"unit":"ppm"}""", "value", "unit", null);
        Assert.Equal("1023", value);
        Assert.Equal("ppm", unit);
    }

    [Fact]
    public void Extract_NestedJsonPath_ReturnsDeepValue()
    {
        var (value, _, _) = InboundPipeline.Extract("""{"sensor":{"value":21.5}}""", "sensor.value", null, null);
        Assert.Equal("21.5", value);
    }

    [Fact]
    public void Extract_BareString_ReturnsPayloadAsValue()
    {
        var (value, unit, _) = InboundPipeline.Extract("42.7", null, null, null);
        Assert.Equal("42.7", value);
        Assert.Null(unit);
    }

    [Fact]
    public void Extract_InvalidJson_FallsBackToRawPayload()
    {
        var (value, _, _) = InboundPipeline.Extract("not-json", "value", null, null);
        Assert.Equal("not-json", value);
    }

    [Fact]
    public void Extract_ReadsLabelField()
    {
        var (value, _, label) = InboundPipeline.Extract(
            """{"value":22.5,"label":"22.5/18.0"}""", "value", null, "label");
        Assert.Equal("22.5", value);
        Assert.Equal("22.5/18.0", label);
    }

    // ── Extract: payload auto-detection ──────────────────────────────────────

    [Theory]
    // legacy shape, explicit and implicit "value" key
    [InlineData("""{"value":1023,"unit":"ppm"}""", "value", "1023")]
    [InlineData("""{"value":1023,"unit":"ppm"}""", null, "1023")]
    [InlineData("""{"value":"heat"}""", null, "heat")]
    // plain number / plain text (e.g. Home Assistant mqtt_statestream state topics)
    [InlineData("23.5", null, "23.5")]
    [InlineData("-4", "value", "-4")]          // a path is ignored for scalars
    [InlineData("on", null, "on")]
    [InlineData("  unavailable \n", null, "unavailable")]
    // JSON scalars (statestream attribute topics are JSON-encoded)
    [InlineData("\"heat\"", null, "heat")]
    [InlineData("true", null, "true")]
    // other JSON with a path, including dotted
    [InlineData("""{"temperature":21.5,"humidity":40}""", "temperature", "21.5")]
    [InlineData("""{"state":{"mode":"cool"}}""", "state.mode", "cool")]
    [InlineData("""{"state":"ON","brightness":120}""", "state", "ON")]
    public void Extract_AutoDetectsPayloadFormat(string payload, string? valueField, string expected)
    {
        var (value, _, _) = InboundPipeline.Extract(payload, valueField, null, null);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("", null)]                                         // cleared retained message
    [InlineData("null", null)]
    [InlineData("[1,2,3]", null)]
    [InlineData("""{"temperature":21.5}""", null)]                 // object, no path, no "value"
    [InlineData("""{"temperature":21.5}""", "humidity")]          // path not present
    [InlineData("""{"state":{"mode":"cool"}}""", "state")]        // path points at an object
    [InlineData("""{"value":null}""", null)]
    public void Extract_NoUsableValue_ReturnsNull(string payload, string? valueField)
    {
        var (value, _, _) = InboundPipeline.Extract(payload, valueField, null, null);
        Assert.Null(value);
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

    private static IReadOnlyList<ConditionalRule> ModeRules =>
    [
        new("eq:unavailable", "#555555", "builtin:alert"),
        new("in:heat,cool",   "#FF8800", null),
        new(">=25",           "#FF0000", null),
        new("any",            "#222222", null),
    ];

    [Theory]
    [InlineData("Unavailable", "#555555")]
    [InlineData("cool",        "#FF8800")]
    [InlineData("30",          "#FF0000")]   // numeric and text rules mix in one list
    [InlineData("20",          "#222222")]
    [InlineData("off",         "#222222")]   // "any" is the fallback
    public void EvaluateRules_TextAndNumericRules_FirstMatchWins(string value, string expectedColour)
    {
        var (colour, _) = InboundPipeline.EvaluateRules(value, ModeRules);
        Assert.Equal(expectedColour, colour);
    }

    [Fact]
    public void EvaluateRules_InvalidCondition_IsSkipped()
    {
        IReadOnlyList<ConditionalRule> rules = [new(">abc", "#FF0000", null), new(">=0", "#00AA00", null)];
        var (colour, _) = InboundPipeline.EvaluateRules("5", rules);
        Assert.Equal("#00AA00", colour);
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

    // ── ResolveZone ───────────────────────────────────────────────────────────

    [Fact]
    public void ResolveZone_NullZone_ReturnsNull()
    {
        Assert.Null(InboundPipeline.ResolveZone(null, hasData: true, "21.6", "°C", null));
    }

    [Fact]
    public void ResolveZone_TemplateWithData_Resolves()
    {
        var zone = new TextZone(Label: "Bureau", Template: "{value} {unit}");
        Assert.Equal("21.6 °C", InboundPipeline.ResolveZone(zone, hasData: true, "21.6", "°C", null));
    }

    [Fact]
    public void ResolveZone_TemplateButNoData_FallsBackToLabel()
    {
        var zone = new TextZone(Label: "Bureau", Template: "{value}");
        Assert.Equal("Bureau", InboundPipeline.ResolveZone(zone, hasData: false, null, null, null));
    }

    [Fact]
    public void ResolveZone_LabelOnly_AlwaysShowsLabel()
    {
        var zone = new TextZone(Label: "Salon", Template: null);
        Assert.Equal("Salon", InboundPipeline.ResolveZone(zone, hasData: true, "21.6", "°C", null));
    }

    [Fact]
    public void ResolveZone_LabelToken_ResolvesToLiveMqttLabel()
    {
        var zone = new TextZone(Label: null, Template: "{label}");
        Assert.Equal("22.5/18.0", InboundPipeline.ResolveZone(zone, hasData: true, "22.5", "°C", "22.5/18.0"));
    }
}
