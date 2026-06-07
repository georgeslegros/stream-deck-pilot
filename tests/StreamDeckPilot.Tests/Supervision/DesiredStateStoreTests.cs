using StreamDeckPilot.Core.Models.Config;
using StreamDeckPilot.Core.Rendering;
using StreamDeckPilot.Infrastructure.Rendering;

namespace StreamDeckPilot.Tests.Supervision;

public class DesiredStateStoreTests
{
    private static ButtonRenderState MakeState(string id) =>
        new(id, "#FF0000", null, IconPlacement.Corner, "Test", null);

    [Fact]
    public void Set_ThenGet_ReturnsSameState()
    {
        var store = new DesiredStateStore();
        var state = MakeState("btn1");
        store.Set("SN1", "main", 0, state);
        Assert.Equal(state, store.Get("SN1", "main", 0));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        var store = new DesiredStateStore();
        Assert.Null(store.Get("SN1", "main", 99));
    }

    [Fact]
    public void GetPage_ReturnsAllButtonsForPage()
    {
        var store = new DesiredStateStore();
        store.Set("SN1", "main", 0, MakeState("b0"));
        store.Set("SN1", "main", 1, MakeState("b1"));
        store.Set("SN1", "other", 0, MakeState("b-other"));

        var page = store.GetPage("SN1", "main");
        Assert.Equal(2, page.Count);
        Assert.Contains(page, e => e.KeyIndex == 0);
        Assert.Contains(page, e => e.KeyIndex == 1);
    }

    [Fact]
    public void Clear_RemovesAllForSerial()
    {
        var store = new DesiredStateStore();
        store.Set("SN1", "main", 0, MakeState("b0"));
        store.Set("SN2", "main", 0, MakeState("b0"));
        store.Clear("SN1");

        Assert.Null(store.Get("SN1", "main", 0));
        Assert.NotNull(store.Get("SN2", "main", 0));
    }

    [Fact]
    public async Task ConcurrentWrites_NoCrash()
    {
        var store = new DesiredStateStore();
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() => store.Set("SN1", "main", i % 15, MakeState($"btn{i}"))));
        await Task.WhenAll(tasks);

        Assert.NotEmpty(store.GetPage("SN1", "main"));
    }
}
