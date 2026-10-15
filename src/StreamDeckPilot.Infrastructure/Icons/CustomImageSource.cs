using Microsoft.Extensions.Options;
using StreamDeckPilot.Core.Validation;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Infrastructure.Icons;

public sealed class CustomImageSource(IOptions<StorageOptions> opts)
{
    private string Dir(string serial) =>
        Path.Combine(opts.Value.BaseDirectory, "images", serial);

    /// <summary>
    /// The full path of <paramref name="filename"/> in the device's images directory, or null when
    /// the serial or file name isn't a bare name or the result would land outside that directory.
    /// The config validator already rejects such references (#76); this is the last line of defence
    /// for configs saved before that check, or edited by hand.
    /// </summary>
    private string? SafePath(string serial, string filename)
    {
        if (!IconReference.IsBareFileName(serial) || !IconReference.IsBareFileName(filename))
            return null;
        var dir = Path.GetFullPath(Dir(serial)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(dir, filename));
        return path.StartsWith(dir, StringComparison.Ordinal) ? path : null;
    }

    public byte[]? Load(string serial, string filename)
    {
        var path = SafePath(serial, filename);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public async Task<string> SaveAsync(string serial, string filename, Stream content)
    {
        var path = SafePath(serial, filename)
            ?? throw new ArgumentException($"'{filename}' is not a bare file name.", nameof(filename));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file);
        return $"custom:{filename}";
    }

    public bool Delete(string serial, string filename)
    {
        var path = SafePath(serial, filename);
        if (path is null || !File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public IReadOnlyList<string> List(string serial)
    {
        if (!IconReference.IsBareFileName(serial)) return [];
        var dir = Dir(serial);
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir).Select(Path.GetFileName).Where(f => f is not null).Cast<string>().ToList()
            : [];
    }
}
