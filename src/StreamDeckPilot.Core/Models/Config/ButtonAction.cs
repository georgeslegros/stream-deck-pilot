using System.Text.Json.Serialization;

namespace StreamDeckPilot.Core.Models.Config;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PublishAction), "Publish")]
[JsonDerivedType(typeof(NavigateAction), "Navigate")]
public abstract record ButtonAction;

public record PublishAction(string Topic, string Payload) : ButtonAction;

public record NavigateAction(string TargetPageId) : ButtonAction;
