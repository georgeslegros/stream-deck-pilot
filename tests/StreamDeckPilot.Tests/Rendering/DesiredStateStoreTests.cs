using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Tests.Rendering;

public sealed class DesiredStateStoreTests
{
    private static ButtonRenderState State(string value) =>
        new("b0", "#00FF00", null, IconPlacement.Corner, value, null);

    [Fact]
    public void TryUpdate_WhenStateUnchanged_Wins()
    {
        var store = new DesiredStateStore();
        var read = State("42");
        store.Set("SN1", "main", 0, read);

        var dimmed = read with { IsDimmed = true };
        Assert.True(store.TryUpdate("SN1", "main", 0, read, dimmed));
        Assert.Same(dimmed, store.Get("SN1", "main", 0));
    }

    [Fact]
    public void TryUpdate_WhenStateReplaced_LosesAndKeepsNewState()
    {
        var store = new DesiredStateStore();
        var read = State("42");
        store.Set("SN1", "main", 0, read);
        var fresh = State("43");
        store.Set("SN1", "main", 0, fresh);

        Assert.False(store.TryUpdate("SN1", "main", 0, read, read with { IsDimmed = true }));
        Assert.Same(fresh, store.Get("SN1", "main", 0));
    }

    [Fact]
    public void TryUpdate_WhenReplacedByEqualLookingState_StillLoses()
    {
        // A fresh message with the same value is a new instance: it must not be dimmed by a stale read.
        var store = new DesiredStateStore();
        var read = State("42");
        store.Set("SN1", "main", 0, read);
        var fresh = State("42");
        store.Set("SN1", "main", 0, fresh);

        Assert.False(store.TryUpdate("SN1", "main", 0, read, read with { IsDimmed = true }));
        Assert.Same(fresh, store.Get("SN1", "main", 0));
    }

    [Fact]
    public void TryUpdate_WhenKeyMissing_Loses()
    {
        var store = new DesiredStateStore();
        var read = State("42");
        store.Set("SN1", "main", 0, read);
        store.Clear("SN1");

        Assert.False(store.TryUpdate("SN1", "main", 0, read, read with { IsDimmed = true }));
        Assert.Null(store.Get("SN1", "main", 0));
    }
}
