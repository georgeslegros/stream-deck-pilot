using OpenMacroBoard.SDK;
using StreamDeckPilot.Infrastructure.StreamDeck;

namespace StreamDeckPilot.Tests.Supervision;

public sealed class FakeStreamDeckLibrary : IStreamDeckLibrary
{
    private readonly List<FakeMacroBoard> _boards;
    private readonly Lock _lock = new();

    public FakeStreamDeckLibrary(params FakeMacroBoard[] boards) =>
        _boards = [..boards];

    public int EnumerateCalls { get; private set; }

    // Simulates the kernel creating / removing the hidraw node.
    public void Plug(FakeMacroBoard board) { lock (_lock) _boards.Add(board); }
    public void Unplug(FakeMacroBoard board) { lock (_lock) _boards.Remove(board); }

    public IReadOnlyList<IStreamDeckDeviceRef> Enumerate()
    {
        lock (_lock)
        {
            EnumerateCalls++;
            return _boards.Select(b => (IStreamDeckDeviceRef)new FakeDeviceRef(b)).ToList();
        }
    }

    private sealed class FakeDeviceRef(FakeMacroBoard board) : IStreamDeckDeviceRef
    {
        public string Path => board.Path;
        public string DeviceName => board.DeviceName;
        public IMacroBoard Open() => board;
    }
}
