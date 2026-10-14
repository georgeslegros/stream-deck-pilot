using OpenMacroBoard.SDK;

namespace StreamDeckPilot.Tests.Supervision;

public sealed class FakeMacroBoard : IMacroBoard
{
    public string Serial { get; set; } = "TEST001";
    public string Path { get; init; } = "/fake/hid/0";
    public string DeviceName { get; init; } = "Stream Deck (Fake)";

    public bool IsConnected { get; set; } = true;
    public bool IsDisposed { get; private set; }
    public IKeyLayout Keys { get; } = new GridKeyLayout(5, 3, 72, 32);

    // Written by the supervisor's background work while tests read it: guard both sides.
    private readonly object _renderLock = new();
    public List<(int KeyIndex, KeyBitmap Bitmap)> RenderCalls { get; } = [];

    public int RenderCallCount() { lock (_renderLock) return RenderCalls.Count; }

    public int RenderedKeyCount() { lock (_renderLock) return RenderCalls.Select(c => c.KeyIndex).Distinct().Count(); }

    public event EventHandler<ConnectionEventArgs>? ConnectionStateChanged;
    public event EventHandler<KeyEventArgs>? KeyStateChanged;

    public void SimulateDisconnect()
    {
        IsConnected = false;
        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(false));
    }

    public void SimulateReconnect()
    {
        IsConnected = true;
        ConnectionStateChanged?.Invoke(this, new ConnectionEventArgs(true));
    }

    public void SimulateKeyPress(int key) =>
        KeyStateChanged?.Invoke(this, new KeyEventArgs(key, true));

    public void SetKeyBitmap(int keyIndex, KeyBitmap bitmap)
    {
        lock (_renderLock) RenderCalls.Add((keyIndex, bitmap));
    }

    public byte? Brightness { get; private set; }
    public void SetBrightness(byte percent) => Brightness = percent;
    public void ShowLogo() { }
    public string GetFirmwareVersion() => "fake-fw-1.0";
    public string GetSerialNumber() => Serial;
    public void Dispose() => IsDisposed = true;
}
