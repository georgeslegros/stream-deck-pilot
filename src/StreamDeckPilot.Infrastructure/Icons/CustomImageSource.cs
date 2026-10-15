using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Infrastructure.Icons;

public sealed class CustomImageSource(IOptions<StorageOptions> opts)
{
    private string Dir(string serial) =>
        Path.Combine(opts.Value.BaseDirectory, "images", serial);

    public byte[]? Load(string serial, string filename)
    {
        var path = Path.Combine(Dir(serial), filename);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>The file's metadata (size, last write), or null when it doesn't exist. No content is read.</summary>
    public FileInfo? Stat(string serial, string filename)
    {
        var info = new FileInfo(Path.Combine(Dir(serial), filename));
        return info.Exists ? info : null;
    }

    public async Task<string> SaveAsync(string serial, string filename, Stream content)
    {
        var dir = Dir(serial);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, filename);
        await using var file = File.Create(path);
        await content.CopyToAsync(file);
        return $"custom:{filename}";
    }

    public bool Delete(string serial, string filename)
    {
        var path = Path.Combine(Dir(serial), filename);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public IReadOnlyList<string> List(string serial)
    {
        var dir = Dir(serial);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).Select(Path.GetFileName).Where(f => f is not null).Cast<string>().ToList()
            : [];
    }
}
