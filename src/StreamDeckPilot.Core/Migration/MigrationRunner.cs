using System.Text.Json.Nodes;

namespace StreamDeckPilot.Core.Migration;

public sealed class MigrationRunner
{
    private readonly IReadOnlyList<IMigration> _migrations;

    public MigrationRunner(IEnumerable<IMigration> migrations) =>
        _migrations = migrations.OrderBy(m => m.FromVersion).ToList();

    /// <summary>
    /// Migrates <paramref name="doc"/> from its <c>schemaVersion</c> up to
    /// <paramref name="targetVersion"/>. Returns the doc unchanged if already current.
    /// </summary>
    /// <exception cref="UnsupportedSchemaVersionException">
    /// Thrown when the document version is below <paramref name="minimumSupported"/>.
    /// </exception>
    /// <exception cref="InvalidSchemaVersionException">
    /// Thrown when <c>schemaVersion</c> is missing or not an integer.
    /// </exception>
    /// <exception cref="MissingMigrationException">
    /// Thrown when a required migration step is absent.
    /// </exception>
    public JsonObject Migrate(JsonObject doc, int minimumSupported, int targetVersion,
        string? source = null)
    {
        var version = doc["schemaVersion"]
                      ?? throw new InvalidSchemaVersionException("Document has no 'schemaVersion' field.");
        if (version is not JsonValue value || !value.TryGetValue<int>(out var current))
            throw new InvalidSchemaVersionException(
                $"'schemaVersion' must be an integer (got {version.ToJsonString()}).");

        if (current < minimumSupported)
            throw new UnsupportedSchemaVersionException(current, minimumSupported, source);

        while (current < targetVersion)
        {
            var migration = _migrations.FirstOrDefault(m => m.FromVersion == current)
                            ?? throw new MissingMigrationException(current);
            doc = migration.Apply(doc);
            current++;
            // Update schemaVersion in the document after each step
            doc["schemaVersion"] = current;
        }

        return doc;
    }
}
