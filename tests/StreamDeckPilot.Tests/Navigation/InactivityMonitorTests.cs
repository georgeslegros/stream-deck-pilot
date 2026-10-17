using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Navigation;
using StreamDeckPilot.Tests.Brightness;

namespace StreamDeckPilot.Tests.Navigation;

public class InactivityMonitorTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _expired = [];

    private InactivityMonitor Monitor(int globalSeconds = 0)
    {
        var monitor = new InactivityMonitor(
            Options.Create(new NavigationOptions { HomeAfterInactivitySeconds = globalSeconds }), _time);
        monitor.Expired += (serial, _) => _expired.Add(serial);
        return monitor;
    }

    [Fact]
    public void OffByDefault_NoTimerIsArmed()
    {
        Assert.Equal(0, new NavigationOptions().HomeAfterInactivitySeconds);
        using var monitor = Monitor();

        monitor.Touch("SN1");
        _time.FireAll();

        Assert.Empty(_time.Timers);
        Assert.Empty(_expired);
        Assert.Null(monitor.TimeoutFor("SN1"));
    }

    [Fact]
    public void Touch_ArmsTheTimeout_AndExpiryReportsTheSerial()
    {
        using var monitor = Monitor(30);

        monitor.Touch("SN1");

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(_time.Timers).DueTime);
        _time.FireAll();
        Assert.Equal(["SN1"], _expired);
    }

    [Fact]
    public void Expiry_IsOneShot_UntilTheNextActivity()
    {
        using var monitor = Monitor(30);
        monitor.Touch("SN1");
        _time.FireAll();
        _time.FireAll();
        Assert.Single(_expired);

        monitor.Touch("SN1");
        _time.FireAll();
        Assert.Equal(2, _expired.Count);
    }

    [Fact]
    public void Touch_RestartsTheTimeout_AndAStaleCallbackIsIgnored()
    {
        using var monitor = Monitor(30);
        monitor.Touch("SN1");
        var first = _time.Timers[0];

        monitor.Touch("SN1");

        Assert.Null(first.DueTime); // cancelled
        first.FireStale();          // a callback already dispatched before the press
        Assert.Empty(_expired);

        _time.FireAll();
        Assert.Equal(["SN1"], _expired);
    }

    [Fact]
    public void DevicesAreTimedIndependently()
    {
        using var monitor = Monitor(30);
        monitor.Touch("SN1");
        monitor.Touch("SN2");

        _time.Timers[0].FireStale();

        Assert.Equal(["SN1"], _expired);
    }

    [Fact]
    public void DeviceOverride_ReplacesTheGlobalTimeout()
    {
        using var monitor = Monitor(0);

        monitor.SetDeviceTimeout("SN1", 10);

        Assert.Equal(TimeSpan.FromSeconds(10), monitor.TimeoutFor("SN1"));
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(_time.Timers).DueTime);
        Assert.Null(monitor.TimeoutFor("SN2"));
    }

    [Fact]
    public void DeviceOverrideOfZero_TurnsItOffForThatDevice()
    {
        using var monitor = Monitor(30);
        monitor.Touch("SN1");

        monitor.SetDeviceTimeout("SN1", 0);
        _time.FireAll();

        Assert.Null(monitor.TimeoutFor("SN1"));
        Assert.Empty(_expired);
    }

    [Fact]
    public void NullOverride_FallsBackToTheGlobalTimeout()
    {
        using var monitor = Monitor(30);
        monitor.SetDeviceTimeout("SN1", 10);

        monitor.SetDeviceTimeout("SN1", null);

        Assert.Equal(TimeSpan.FromSeconds(30), monitor.TimeoutFor("SN1"));
    }

    [Fact]
    public void AThrowingHandler_DoesNotEscapeTheTimer()
    {
        using var monitor = Monitor(30);
        monitor.Expired += (_, _) => throw new InvalidOperationException("boom");
        monitor.Touch("SN1");

        _time.FireAll(); // must not throw

        Assert.Equal(["SN1"], _expired);
    }

    [Fact]
    public void RunIfCurrent_RunsOnlyWhenNothingHappenedSinceExpiry()
    {
        using var monitor = Monitor(30);
        long generation = -1;
        monitor.Expired += (_, g) => generation = g;
        monitor.Touch("SN1");
        _time.FireAll();

        var ran = 0;
        Assert.True(monitor.RunIfCurrent("SN1", generation, () => ran++));

        monitor.Touch("SN1"); // a press lands between the expiry and the switch
        Assert.False(monitor.RunIfCurrent("SN1", generation, () => ran++));
        Assert.Equal(1, ran);
    }
}
