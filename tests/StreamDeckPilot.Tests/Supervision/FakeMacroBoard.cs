using OpenMacroBoard.SDK;

namespace StreamDeckPilot.Tests.Supervision;

public sealed class FakeMacroBoard : IMacroBoard
{
    public string Serial { get; init; } = "TEST001";
    public string Path { get; init; } = "/fake/hid/0";
    public string DeviceName { get; init; } = "Stream Deck (Fake)";

    public bool IsConnected { get; private set; } = true;
    public IKeyLayout Keys { get; } = new GridKeyLayout(5, 3, 72, 32);

    public List<(int KeyIndex, KeyBitmap Bitmap)> RenderCalls { get; } = [];

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

    public void SetKeyBitmap(int keyIndex, KeyBitmap bitmap) =>
        RenderCalls.Add((keyIndex, bitmap));

    public void SetBrightness(byte percent) { }
    public void ShowLogo() { }
    public string GetFirmwareVersion() => "fake-fw-1.0";
    public string GetSerialNumber() => Serial;
    public void Dispose() { }
}
