namespace StreamDeckPilot.Core.Migration;

public sealed class UnsupportedSchemaVersionException(int version, int minimumSupported, string? source = null)
    : Exception($"Schema version {version} is below the minimum supported version {minimumSupported}" +
                (source is null ? "" : $" (file: {source})") +
                ". Use an older release to upgrade the file first.")
{
    public int Version { get; } = version;
    public int MinimumSupported { get; } = minimumSupported;
}
