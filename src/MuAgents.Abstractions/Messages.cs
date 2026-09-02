using System.Text.Json.Serialization;

namespace MuAgents.Abstractions;

public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record AgentMessage(
    string Id,
    AgentRole Role,
    IReadOnlyList<MessagePart> Parts,
    DateTimeOffset CreatedAt,
    MessageMetadata? Metadata = null)
{
    public static AgentMessage Text(AgentRole role, string text) =>
        new(Guid.NewGuid().ToString("N"), role, [new TextPart(text)], DateTimeOffset.UtcNow);
}

public sealed record MessageMetadata(
    string? Model = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    IReadOnlyDictionary<string, string>? Properties = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
[JsonDerivedType(typeof(ToolCallPart), "tool_call")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
public abstract record MessagePart;

public sealed record TextPart(string Text) : MessagePart;

public sealed record ImagePart(ImageSource Source, string? MediaType = null) : MessagePart;

public sealed record ToolCallPart(string CallId, string Name, string ArgumentsJson) : MessagePart;

public sealed record ToolResultPart(string CallId, string Content, bool IsError = false) : MessagePart;

public enum ImageSourceKind
{
    HttpsUrl,
    FileReference,
    DataUrl
}

public sealed record ImageSource(ImageSourceKind Kind, string Value);
