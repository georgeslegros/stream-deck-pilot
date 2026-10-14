using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Rendering;
using StreamDeckPilot.Tests.Supervision;

namespace StreamDeckPilot.Tests.Rendering;

public sealed class DeviceRendererTests
{
    private static ButtonRenderState State(string colour, bool dimmed = false) =>
        new("b0", colour, null, IconPlacement.Corner, "42", null, IsDimmed: dimmed);

    [Fact]
    public void RenderLatest_PaintsTheStoresCurrentState_NotAnEarlierOne()
    {
        // The staleness monitor wins its compare-and-set with a dimmed state, then a fresh MQTT value
        // lands before the monitor paints. The monitor's paint must show the fresh value, not the dim.
        var renderer = new DeviceRenderer();
        var store = new DesiredStateStore();
        var board = new FakeMacroBoard();
        store.Set("SN1", "main", 0, State("#00FF00", dimmed: true));
        store.Set("SN1", "main", 0, State("#FF0000"));

        renderer.RenderLatest(board, "SN1", "main", 0, store);

        var (key, bitmap) = Assert.Single(board.RenderCalls);
        Assert.Equal(0, key);
        Assert.Equal(KeyBitmap.Create.FromRgb(0xFF, 0x00, 0x00), bitmap);
    }

    [Fact]
    public void RenderLatest_WithNoStoredState_PaintsNothing()
    {
        var board = new FakeMacroBoard();

        new DeviceRenderer().RenderLatest(board, "SN1", "main", 0, new DesiredStateStore());

        Assert.Empty(board.RenderCalls);
    }
}
