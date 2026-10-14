using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Rendering;

namespace StreamDeckPilot.Infrastructure.Rendering;

public interface IDeviceRenderer
{
    void RenderButton(IMacroBoard board, string serial, int keyIndex, ButtonRenderState state);

    /// <summary>
    /// Paints whatever <paramref name="stateStore"/> holds for the key right now, serialised per
    /// physical key. Writers that update the store and then call this can't leave an older state
    /// on screen: whichever paint runs last reads the newest state (#62).
    /// </summary>
    void RenderLatest(IMacroBoard board, string serial, string pageId, int keyIndex, DesiredStateStore stateStore);

    void RenderAll(IMacroBoard board, string serial, string pageId, DesiredStateStore stateStore);
}
