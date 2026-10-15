namespace StreamDeckPilot.Core.Migration;

/// <summary>The document's <c>schemaVersion</c> is missing or isn't an integer.</summary>
public sealed class InvalidSchemaVersionException(string message) : Exception(message);
