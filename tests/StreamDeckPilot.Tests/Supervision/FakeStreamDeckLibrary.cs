using OpenMacroBoard.SDK;
using StreamDeckPilot.Infrastructure.StreamDeck;

namespace StreamDeckPilot.Tests.Supervision;

public sealed class FakeStreamDeckLibrary : IStreamDeckLibrary
{
    private readonly List<FakeMacroBoard> _boards;

    public FakeStreamDeckLibrary(params FakeMacroBoard[] boards) =>
        _boards = [..boards];

    public IReadOnlyList<IStreamDeckDeviceRef> Enumerate() =>
        _boards.Select(b => (IStreamDeckDeviceRef)new FakeDeviceRef(b)).ToList();

    private sealed class FakeDeviceRef(FakeMacroBoard board) : IStreamDeckDeviceRef
    {
        public string Path => board.Path;
        public string DeviceName => board.DeviceName;
        public IMacroBoard Open() => board;
    }
}
