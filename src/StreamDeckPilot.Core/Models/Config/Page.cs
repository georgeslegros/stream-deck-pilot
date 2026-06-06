using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "pageType")]
[JsonDerivedType(typeof(ButtonGridPage), "ButtonGrid")]
public abstract record Page(string PageId);

public record ButtonGridPage(
    string PageId,
    IReadOnlyList<ButtonDefinition> Buttons) : Page(PageId);
