namespace StreamDeckPilot.Infrastructure.Persistence;

public record StorageOptions
{
    public string BaseDirectory { get; init; } = "/data";
}
