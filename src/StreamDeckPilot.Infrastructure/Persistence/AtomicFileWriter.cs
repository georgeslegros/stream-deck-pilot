using System.Text.Json;

namespace StreamDeckPilot.Infrastructure.Persistence;

public static class AtomicFileWriter
{
    // Writes to a temp file next to the target, then renames over it — crash-safe. The temp name is
    // unique per write: a shared <path>.tmp made two concurrent writes of the same file collide on
    // it (IOException, #79). The last rename wins, so the target is always one complete document.
    public static async Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options,
        CancellationToken cancellationToken = default)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken);

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Don't leave a half-written temp file behind.
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
