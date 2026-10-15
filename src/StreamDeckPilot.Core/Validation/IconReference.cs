namespace StreamDeckPilot.Core.Validation;

/// <summary>
/// The icon reference forms the renderer understands: <c>builtin:&lt;name&gt;</c> and
/// <c>custom:&lt;file name&gt;</c>. A custom reference names a file in the device's images
/// directory, so it must be a bare file name: anything else could resolve outside it (#76).
/// </summary>
public static class IconReference
{
    public const string BuiltinPrefix = "builtin:";
    public const string CustomPrefix = "custom:";

    /// <summary>
    /// True for a plain file name with no directory part: no separators, no <c>..</c>, not rooted.
    /// The same rule applies to uploads, deletes and <c>custom:</c> references.
    /// </summary>
    public static bool IsBareFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Contains("..", StringComparison.Ordinal)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !Path.IsPathRooted(name)
        && Path.GetFileName(name) == name;

    /// <summary>
    /// Returns an error message for an unusable reference, or null when it's fine. Null or empty
    /// means "no icon" and is fine.
    /// </summary>
    public static string? Validate(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        if (reference.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            return reference.Length > BuiltinPrefix.Length ? null : "builtin: needs an icon name";

        if (reference.StartsWith(CustomPrefix, StringComparison.Ordinal))
            return IsBareFileName(reference[CustomPrefix.Length..])
                ? null
                : "custom: must be followed by a bare file name (no path separators or '..')";

        return "must start with builtin: or custom:";
    }
}
