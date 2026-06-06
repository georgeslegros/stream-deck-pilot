using OpenMacroBoard.SDK;
using StreamDeckPilot.Core.Rendering;

namespace StreamDeckPilot.Infrastructure.Rendering;

public interface IDeviceRenderer
{
    void RenderButton(IMacroBoard board, string serial, int keyIndex, ButtonRenderState state);
    void RenderAll(IMacroBoard board, string serial, string pageId, DesiredStateStore stateStore);
}
