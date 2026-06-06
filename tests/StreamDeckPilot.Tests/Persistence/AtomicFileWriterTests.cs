using System.Text.Json;
using StreamDeckPilot.Core.Json;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Tests.Persistence;

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public AtomicFileWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WriteJsonAsync_CreatesFileAndRemovesTmp()
    {
        var path = Path.Combine(_dir, "test.json");
        await AtomicFileWriter_WriteJsonAsync(path, new { Value = 42 });

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteJsonAsync_ContentIsCorrect()
    {
        var path = Path.Combine(_dir, "data.json");
        var data = new { Name = "hello", Count = 7 };
        await AtomicFileWriter_WriteJsonAsync(path, data);

        var json = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("hello", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task WriteJsonAsync_OverwritesExistingFile()
    {
        var path = Path.Combine(_dir, "overwrite.json");
        await AtomicFileWriter_WriteJsonAsync(path, new { V = 1 });
        await AtomicFileWriter_WriteJsonAsync(path, new { V = 2 });

        var json = await File.ReadAllTextAsync(path);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task WriteJsonAsync_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "sub", "nested", "file.json");
        await AtomicFileWriter_WriteJsonAsync(path, new { X = 1 });
        Assert.True(File.Exists(path));
    }

    // Thin wrapper to call the internal static method via the same options used in production
    private static Task AtomicFileWriter_WriteJsonAsync<T>(string path, T value) =>
        AtomicFileWriterAccessor.WriteJsonAsync(path, value, JsonOptions.Default,
            TestContext.Current.CancellationToken);
}

// Accessor shim — AtomicFileWriter is internal; tests are in the same assembly via InternalsVisibleTo
// (added below), so this direct call works.
file static class AtomicFileWriterAccessor
{
    public static Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions opts,
        CancellationToken ct = default) =>
        AtomicFileWriter.WriteJsonAsync(path, value, opts, ct);
}
