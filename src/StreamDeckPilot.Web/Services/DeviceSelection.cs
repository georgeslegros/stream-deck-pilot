namespace StreamDeckPilot.Web.Services;

/// <summary>
/// The deck the user is looking at, per browser circuit (scoped). The device switcher sets it; pages
/// read it and re-render on <see cref="Changed"/>.
/// </summary>
public sealed class DeviceSelection
{
    public string? Serial { get; private set; }

    public event Action? Changed;

    public void Select(string? serial)
    {
        if (string.Equals(Serial, serial, StringComparison.Ordinal)) return;
        Serial = serial;
        Changed?.Invoke();
    }
}
