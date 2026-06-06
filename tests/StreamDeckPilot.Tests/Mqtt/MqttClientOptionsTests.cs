using MQTTnet;
using StreamDeckPilot.Infrastructure.Mqtt;

namespace StreamDeckPilot.Tests.Mqtt;

/// <summary>
/// Fast (no broker) regression guard for the BuildClientOptions endpoint bug. The Action&lt;MqttClientTcpOptions&gt;
/// overload alone leaves the builder's private _remoteEndPoint null, so Build() throws "No endpoint is set."
/// These tests lock in that the endpoint is registered AND NoDelay stays enabled.
/// </summary>
public sealed class MqttClientOptionsTests
{
    private static MqttOptions Opts() => new()
    {
        Host = "broker.example.test",
        Port = 1883,
        Username = "guest",
        Password = "guest",
        ClientId = "test-client",
    };

    [Fact]
    public void BuildClientOptions_DoesNotThrow_NoEndpointSet()
    {
        // The regression: this used to throw System.ArgumentException "No endpoint is set."
        var ex = Record.Exception(() => MqttClientService.BuildClientOptions(Opts()));
        Assert.Null(ex);
    }

    [Fact]
    public void BuildClientOptions_RegistersTcpEndpoint_AndKeepsNoDelay()
    {
        var options = MqttClientService.BuildClientOptions(Opts());

        var tcp = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        // Endpoint must be populated (proves the string/port overload ran), and NoDelay must survive
        // the chained Action overload.
        Assert.NotNull(tcp.RemoteEndpoint);
        Assert.True(tcp.NoDelay);
    }
}
