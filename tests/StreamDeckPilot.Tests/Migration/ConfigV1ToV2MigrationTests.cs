using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Migration;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Migration;

public sealed class ConfigV1ToV2MigrationTests
{
    private static JsonObject V1ConfigWithDisplay(string? baseIcon, string? staticLabel, string? formatTemplate) =>
        new()
        {
            ["schemaVersion"] = 1,
            ["serial"] = "SN1",
            ["pages"] = new JsonArray
            {
                new JsonObject
                {
                    ["pageType"] = "ButtonGrid",
                    ["pageId"] = "main",
                    ["buttons"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["buttonId"] = "b0",
                            ["keyIndex"] = 0,
                            ["pageId"] = "main",
                            ["display"] = new JsonObject
                            {
                                ["baseIcon"] = baseIcon,
                                ["staticLabel"] = staticLabel,
                                ["formatTemplate"] = formatTemplate,
                            },
                        },
                    },
                },
            },
        };

    private static JsonObject Display(JsonObject migrated) =>
        migrated["pages"]![0]!["buttons"]![0]!["display"]!.AsObject();

    [Fact]
    public void Sensor_FormatTemplate_BecomesCenterTemplate_WithCornerIcon()
    {
        var result = new ConfigV1ToV2Migration().Apply(
            V1ConfigWithDisplay("builtin:thermometer", "Bureau", "{value} {unit}"));

        var display = Display(result);
        Assert.Equal("corner", display["iconPlacement"]!.GetValue<string>());
        Assert.Equal("{value} {unit}", display["center"]!["template"]!.GetValue<string>());
        Assert.Equal("Bureau", display["bottom"]!["label"]!.GetValue<string>());
        Assert.Equal("builtin:thermometer", display["baseIcon"]!.GetValue<string>());
        Assert.False(display.ContainsKey("formatTemplate"));
        Assert.False(display.ContainsKey("staticLabel"));
    }

    [Fact]
    public void NoFormatTemplate_GetsCentreIcon_AndBottomLabel()
    {
        var result = new ConfigV1ToV2Migration().Apply(
            V1ConfigWithDisplay("builtin:lightbulb", "Lamp", null));

        var display = Display(result);
        Assert.Equal("center", display["iconPlacement"]!.GetValue<string>());
        Assert.False(display.ContainsKey("center")); // no template → no center zone
        Assert.Equal("Lamp", display["bottom"]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConfigStore_LoadsAndMigratesV1FileToV2Model()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        try
        {
            var v1 = V1ConfigWithDisplay("builtin:thermometer", "Bureau", "{value} {unit}");
            await File.WriteAllTextAsync(Path.Combine(dir, "config", "SN1.json"), v1.ToJsonString());

            var store = new ConfigStore(
                Options.Create(new StorageOptions { BaseDirectory = dir }),
                new MigrationRunner([new ConfigV1ToV2Migration()]));

            var config = await store.LoadAsync("SN1");

            Assert.NotNull(config);
            Assert.Equal(2, config!.SchemaVersion);
            var grid = Assert.IsType<ButtonGridPage>(config.Pages[0]);
            var display = grid.Buttons[0].Display;
            Assert.Equal(IconPlacement.Corner, display.IconPlacement);
            Assert.Equal("{value} {unit}", display.Center!.Template);
            Assert.Equal("Bureau", display.Bottom!.Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
