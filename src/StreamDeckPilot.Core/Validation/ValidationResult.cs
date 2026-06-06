namespace StreamDeckPilot.Core.Validation;

public record ValidationResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success { get; } = new([], []);
}
