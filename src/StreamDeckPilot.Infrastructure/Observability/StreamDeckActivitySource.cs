using System.Diagnostics;

namespace StreamDeckPilot.Infrastructure.Observability;

public static class StreamDeckActivitySource
{
    public static readonly ActivitySource Pipeline = new("StreamDeckPilot.Pipeline");
}
