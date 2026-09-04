using System.Text.Json.Serialization;

namespace MuAgents.Abstractions;

/// <summary>统一消息角色，不直接绑定任何一家模型服务的字段名称。</summary>
public enum AgentRole
{
    System,
    User,
    Assistant,
    Tool
}

/// <summary>一条可持久化的智能体消息，由一个或多个多态消息片段组成。</summary>
public sealed record AgentMessage(
    string Id,
    AgentRole Role,
    IReadOnlyList<MessagePart> Parts,
    DateTimeOffset CreatedAt,
    MessageMetadata? Metadata = null)
{
    /// <summary>快捷创建只包含文本片段并带有新 ID 和 UTC 时间的消息。</summary>
    public static AgentMessage Text(AgentRole role, string text) =>
        new(Guid.NewGuid().ToString("N"), role, [new TextPart(text)], DateTimeOffset.UtcNow);
}

/// <summary>记录生成消息的模型、Token 用量和实现自定义属性。</summary>
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
/// <summary>消息片段基类；JSON 判别字段 type 保证 SQLite 序列化后仍能还原具体类型。</summary>
public abstract record MessagePart;

/// <summary>普通文本片段。</summary>
public sealed record TextPart(string Text) : MessagePart;

/// <summary>经过安全处理的图片片段和媒体类型。</summary>
public sealed record ImagePart(ImageSource Source, string? MediaType = null) : MessagePart;

/// <summary>模型请求执行工具的片段，ArgumentsJson 保存模型给出的原始 JSON 参数。</summary>
public sealed record ToolCallPart(string CallId, string Name, string ArgumentsJson) : MessagePart;

/// <summary>工具执行结果片段，通过 CallId 与原工具调用配对。</summary>
public sealed record ToolResultPart(string CallId, string Content, bool IsError = false) : MessagePart;

/// <summary>图片来源类型；本地文件、远程地址和 Data URL 使用不同的安全校验流程。</summary>
public enum ImageSourceKind
{
    HttpsUrl,
    FileReference,
    DataUrl
}

/// <summary>图片来源及其原始值，进入模型适配器前必须由 IImageInputProcessor 处理。</summary>
public sealed record ImageSource(ImageSourceKind Kind, string Value);
