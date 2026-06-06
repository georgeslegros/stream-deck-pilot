namespace StreamDeckPilot.Core.Migration;

public sealed class MissingMigrationException(int fromVersion)
    : Exception($"No migration registered for schema version {fromVersion} → {fromVersion + 1}.")
{
    public int FromVersion { get; } = fromVersion;
}
