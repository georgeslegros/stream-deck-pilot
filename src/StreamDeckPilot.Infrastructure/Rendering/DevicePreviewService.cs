using System.Security.Cryptography;
using OpenMacroBoard.SDK;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using StreamDeckPilot.Core.Models.Catalogue;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Supervision;

namespace StreamDeckPilot.Infrastructure.Rendering;

/// <summary>Where a preview's pixels came from.</summary>
public enum PreviewSource
{
    /// <summary>The frames last sent to the connected deck: exactly what it shows now.</summary>
    Device,
    /// <summary>Composed from desired state: a page that isn't on screen, or a deck that isn't connected.</summary>
    DesiredState,
}

public enum PreviewStatus { Ok, UnknownDevice, UnknownPage, KeyOutOfRange }

/// <summary>A rendered preview as PNG bytes, with an ETag over those bytes.</summary>
public sealed record PreviewImage(byte[] Png, string ETag, string? PageId, PreviewSource Source);

public sealed record PreviewResult(PreviewStatus Status, PreviewImage? Image = null, string? Error = null)
{
    public static PreviewResult Fail(PreviewStatus status, string error) => new(status, null, error);
}

/// <summary>
/// Renders a device's keys as images without touching the hardware (#27), so checking the deck
/// remotely can never wake a dark deck or slow the USB link.
/// <list type="bullet">
/// <item>The page on screen of a connected deck comes from <see cref="PaintedFrameStore"/>: the
/// frames the renderer last sent, paint-time looks included (offline overlay, press feedback,
/// the current blink frame, setpoint controls).</item>
/// <item>Any other page, or a deck that isn't connected, is composed from desired state with the
/// same non-animated looks the renderer would apply now (<see cref="DeviceRenderer.WithPaintOverlays"/>):
/// the broker-offline overlay and press feedback; a blinking key shows its "on" look.</item>
/// </list>
/// Brightness is never applied: the preview shows what the deck shows at full brightness.
/// </summary>
public sealed class DevicePreviewService(
    CatalogueStore catalogue,
    ConfigStore configs,
    DesiredStateStore desiredState,
    PaintedFrameStore frames,
    KeyBitmapComposer composer,
    DeviceRenderer renderer,
    DeviceSupervisorService supervisor)
{
    public const int MaxScale = 2;

    /// <summary>
    /// The whole board for <paramref name="pageId"/>, or the page on screen when null. A deck that has
    /// never shown a page (no config) previews as an all-black board.
    /// </summary>
    public async Task<PreviewResult> RenderBoardAsync(string serial, string? pageId = null, int scale = 1)
    {
        var (device, page, error) = await ResolveAsync(serial, pageId);
        if (error is not null) return error;

        var source = SourceFor(serial, page);
        using var board = BoardPreviewRenderer.Compose(device!.KeyColumns, device.KeyRows, scale,
            key => KeyImage(serial, page, key, source));
        return Ok(board, page, source);
    }

    /// <summary>One key of <paramref name="pageId"/> (or of the page on screen), at 72×72 × <paramref name="scale"/>.</summary>
    public async Task<PreviewResult> RenderKeyAsync(string serial, int keyIndex, string? pageId = null, int scale = 1)
    {
        var (device, page, error) = await ResolveAsync(serial, pageId);
        if (error is not null) return error;
        if (keyIndex < 0 || keyIndex >= device!.KeyCount)
            return PreviewResult.Fail(PreviewStatus.KeyOutOfRange,
                $"Key {keyIndex} is out of range for device '{serial}' with {device.KeyCount} keys.");

        var source = SourceFor(serial, page);
        using var key = BoardPreviewRenderer.Scale(
            KeyImage(serial, page, keyIndex, source) ?? BlackKey(), scale);
        return Ok(key, page, source);
    }

    private async Task<(DeviceEntry? Device, string? Page, PreviewResult? Error)> ResolveAsync(string serial, string? pageId)
    {
        var device = (await catalogue.LoadAsync()).Devices.FirstOrDefault(d => d.Serial == serial);
        if (device is null)
            return (null, null, PreviewResult.Fail(PreviewStatus.UnknownDevice, $"Device '{serial}' is not in the catalogue."));

        var config = await configs.LoadAsync(serial);
        var pageIds = config?.Pages.Select(p => p.PageId).ToList() ?? [];
        if (pageId is not null)
            return pageIds.Contains(pageId)
                ? (device, pageId, null)
                : (null, null, PreviewResult.Fail(PreviewStatus.UnknownPage, $"Page '{pageId}' does not exist for device '{serial}'."));

        var active = supervisor.GetActivePage(serial);
        return (device, active is not null && pageIds.Contains(active) ? active : pageIds.FirstOrDefault(), null);
    }

    private PreviewSource SourceFor(string serial, string? page) =>
        page is not null && page == supervisor.GetActivePage(serial) && supervisor.GetBoard(serial)?.IsConnected == true
            ? PreviewSource.Device
            : PreviewSource.DesiredState;

    // Null means a black key. Caller owns the image.
    private Image<Rgba32>? KeyImage(string serial, string? page, int keyIndex, PreviewSource source)
    {
        // A key the renderer hasn't painted yet (just connected) falls back to desired state.
        if (source == PreviewSource.Device && frames.Get(serial, keyIndex) is { } frame)
            return ToImage(frame.Bitmap);

        if (page is null || desiredState.Get(serial, page, keyIndex) is not { } state) return null;
        // A blinking key previews in its "on" look: the blink phase is only defined on screen.
        return composer.ComposeImage(renderer.WithPaintOverlays(serial, state) with { BlinkLevel = 1f }, serial);
    }

    private static Image<Rgba32>? ToImage(KeyBitmap bitmap)
    {
        var access = (IKeyBitmapDataAccess)bitmap;
        if (access.IsEmpty) return null;   // KeyBitmap.Black
        return Image.LoadPixelData<Bgr24>(access.GetData(), access.Width, access.Height).CloneAs<Rgba32>();
    }

    private static Image<Rgba32> BlackKey() =>
        new(BoardPreviewRenderer.KeySize, BoardPreviewRenderer.KeySize, new Rgba32(0, 0, 0));

    private static PreviewResult Ok(Image<Rgba32> image, string? page, PreviewSource source)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        var png = stream.ToArray();
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(png), 0, 8).ToLowerInvariant()}\"";
        return new PreviewResult(PreviewStatus.Ok, new PreviewImage(png, etag, page, source));
    }
}
