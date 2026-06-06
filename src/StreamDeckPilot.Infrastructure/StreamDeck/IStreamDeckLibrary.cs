using OpenMacroBoard.SDK;

namespace StreamDeckPilot.Infrastructure.StreamDeck;

public interface IStreamDeckDeviceRef
{
    string Path { get; }
    string DeviceName { get; }
    IMacroBoard Open();
}

public interface IStreamDeckLibrary
{
    IReadOnlyList<IStreamDeckDeviceRef> Enumerate();
}
