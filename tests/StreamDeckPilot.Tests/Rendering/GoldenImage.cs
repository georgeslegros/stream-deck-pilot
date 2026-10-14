using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace StreamDeckPilot.Tests.Rendering;

/// <summary>
/// Compares a rendered key against a committed PNG baseline in Rendering/Golden/.
/// Text comes from system fonts (DejaVu Sans, as in the container), so anti-aliasing may differ
/// slightly between machines or ImageSharp versions: the comparison tolerates small per-pixel
/// differences and catches real regressions (missing text or icon, wrong colour or position).
/// Set STREAMDECK_UPDATE_GOLDEN=1 to (re)write the baselines. On a mismatch the actual image and
/// a diff are written to TestResults/golden/ (uploaded by CI when tests fail).
/// </summary>
public static class GoldenImage
{
    // A pixel "differs" when any channel is off by more than this.
    private const int PixelThreshold = 64;
    // Fraction of differing pixels tolerated (72x72 = 5184 px → 52 px).
    private const double MaxDifferingFraction = 0.01;
    // Mean absolute channel difference tolerated over the whole image.
    private const double MaxMeanDelta = 3.0;

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string BaselineDir = Path.Combine(RepoRoot, "tests", "StreamDeckPilot.Tests", "Rendering", "Golden");
    private static readonly string FailureDir = Path.Combine(RepoRoot, "TestResults", "golden");

    public static void AssertMatches(Image<Rgba32> actual, string name)
    {
        var baselinePath = Path.Combine(BaselineDir, name + ".png");
        if (Environment.GetEnvironmentVariable("STREAMDECK_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(BaselineDir);
            actual.SaveAsPng(baselinePath);
            return;
        }

        Assert.True(File.Exists(baselinePath),
            $"No baseline {baselinePath}. Run the tests with STREAMDECK_UPDATE_GOLDEN=1 to create it.");
        using var expected = Image.Load<Rgba32>(baselinePath);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        var (differing, meanDelta, diff) = Compare(expected, actual);
        using (diff)
        {
            var fraction = differing / (double)(actual.Width * actual.Height);
            if (fraction <= MaxDifferingFraction && meanDelta <= MaxMeanDelta) return;

            Directory.CreateDirectory(FailureDir);
            var actualPath = Path.Combine(FailureDir, name + ".actual.png");
            var diffPath = Path.Combine(FailureDir, name + ".diff.png");
            actual.SaveAsPng(actualPath);
            diff.SaveAsPng(diffPath);
            Assert.Fail($"'{name}' differs from its baseline: {differing} px ({fraction:P1}) beyond ±{PixelThreshold}, " +
                        $"mean delta {meanDelta:F2}. Actual: {actualPath}; diff: {diffPath}. " +
                        "If the change is intended, re-run with STREAMDECK_UPDATE_GOLDEN=1 and review the new PNGs.");
        }
    }

    private static (int Differing, double MeanDelta, Image<Rgba32> Diff) Compare(Image<Rgba32> expected, Image<Rgba32> actual)
    {
        var diff = new Image<Rgba32>(actual.Width, actual.Height);
        var differing = 0;
        long total = 0;
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < actual.Width; x++)
        {
            var e = expected[x, y];
            var a = actual[x, y];
            var d = Math.Max(Math.Max(Math.Abs(e.R - a.R), Math.Abs(e.G - a.G)), Math.Max(Math.Abs(e.B - a.B), Math.Abs(e.A - a.A)));
            total += Math.Abs(e.R - a.R) + Math.Abs(e.G - a.G) + Math.Abs(e.B - a.B) + Math.Abs(e.A - a.A);
            if (d > PixelThreshold) differing++;
            // Red where it differs beyond the threshold, faint grey copy of the expected image elsewhere.
            diff[x, y] = d > PixelThreshold ? new Rgba32(255, 0, 0) : new Rgba32((byte)(e.R / 4), (byte)(e.G / 4), (byte)(e.B / 4));
        }
        return (differing, total / (double)(actual.Width * actual.Height * 4), diff);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "StreamDeckPilot.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Repository root (StreamDeckPilot.slnx) not found.");
    }
}
