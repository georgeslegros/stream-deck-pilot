using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "pageType")]
[JsonDerivedType(typeof(ButtonGridPage), "ButtonGrid")]
public abstract record Page(string PageId);

// Controls (#132) are multi-key units next to the buttons. Omitted from the JSON when unset, so
// existing configs round-trip unchanged.
public record ButtonGridPage(
    string PageId,
    IReadOnlyList<ButtonDefinition> Buttons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Control>? Controls = null) : Page(PageId);
