using System.Text.Json.Nodes;

namespace StreamDeckPilot.Core.Migration;

public interface IMigration
{
    // Transforms a document at FromVersion to FromVersion+1
    int FromVersion { get; }
    JsonObject Apply(JsonObject doc);
}
