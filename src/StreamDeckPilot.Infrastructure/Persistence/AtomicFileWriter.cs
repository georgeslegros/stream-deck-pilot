using System.Text.Json;

namespace StreamDeckPilot.Infrastructure.Persistence;

internal static class AtomicFileWriter
{
    // Writes to <path>.tmp then renames over the target — crash-safe.
    public static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var tmp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);

        File.Move(tmp, path, overwrite: true);
    }
}
