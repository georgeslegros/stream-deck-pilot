using System.Text.Json.Nodes;
using StreamDeckPilot.Core.Migration;

namespace StreamDeckPilot.Tests.Migration;

public class MigrationRunnerTests
{
    private static JsonObject DocAt(int version) =>
        new() { ["schemaVersion"] = version, ["data"] = "test" };

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void Migrate_AlreadyAtTarget_ReturnsUnchanged()
    {
        var runner = new MigrationRunner([]);
        var doc = DocAt(1);
        var result = runner.Migrate(doc, minimumSupported: 1, targetVersion: 1);
        Assert.Equal(1, result["schemaVersion"]!.GetValue<int>());
        Assert.Equal("test", result["data"]!.GetValue<string>());
    }

    // ── Chain ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Migrate_TwoStepChain_AppliesBothInOrder()
    {
        var m1 = new LambdaMigration(1, doc => { doc["step1"] = true; return doc; });
        var m2 = new LambdaMigration(2, doc => { doc["step2"] = true; return doc; });
        var runner = new MigrationRunner([m2, m1]); // intentionally unsorted

        var result = runner.Migrate(DocAt(1), minimumSupported: 1, targetVersion: 3);

        Assert.Equal(3, result["schemaVersion"]!.GetValue<int>());
        Assert.True(result["step1"]!.GetValue<bool>());
        Assert.True(result["step2"]!.GetValue<bool>());
    }

    [Fact]
    public void Migrate_PartialChain_StopsAtTarget()
    {
        var m1 = new LambdaMigration(1, doc => { doc["step1"] = true; return doc; });
        var m2 = new LambdaMigration(2, doc => { doc["step2"] = true; return doc; });
        var runner = new MigrationRunner([m1, m2]);

        var result = runner.Migrate(DocAt(1), minimumSupported: 1, targetVersion: 2);

        Assert.Equal(2, result["schemaVersion"]!.GetValue<int>());
        Assert.True(result["step1"]!.GetValue<bool>());
        Assert.False(result.ContainsKey("step2")); // step2 not applied
    }

    // ── Floor rejection ───────────────────────────────────────────────────────

    [Fact]
    public void Migrate_BelowFloor_Throws()
    {
        var runner = new MigrationRunner([]);
        var ex = Assert.Throws<UnsupportedSchemaVersionException>(
            () => runner.Migrate(DocAt(0), minimumSupported: 1, targetVersion: 1));
        Assert.Equal(0, ex.Version);
        Assert.Equal(1, ex.MinimumSupported);
    }

    [Fact]
    public void Migrate_AtFloor_Succeeds()
    {
        var runner = new MigrationRunner([]);
        var result = runner.Migrate(DocAt(1), minimumSupported: 1, targetVersion: 1);
        Assert.Equal(1, result["schemaVersion"]!.GetValue<int>());
    }

    // ── Missing migration ─────────────────────────────────────────────────────

    [Fact]
    public void Migrate_MissingStep_Throws()
    {
        var runner = new MigrationRunner([]); // no v1→v2 migration registered
        Assert.Throws<MissingMigrationException>(
            () => runner.Migrate(DocAt(1), minimumSupported: 1, targetVersion: 2));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class LambdaMigration(int fromVersion, Func<JsonObject, JsonObject> apply) : IMigration
    {
        public int FromVersion => fromVersion;
        public JsonObject Apply(JsonObject doc) => apply(doc);
    }
}
