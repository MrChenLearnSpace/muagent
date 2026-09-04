namespace MuAgents.Abstractions;

/// <summary>会话元数据。TenantId 是每次持久化查询都必须携带的隔离键。</summary>
public sealed record Conversation(
    string Id,
    string TenantId,
    string CreatedByUserId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version = 0);

/// <summary>会话与消息存储契约；所有读取和写入都要求显式提供 tenantId。</summary>
public interface IConversationStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Conversation> CreateAsync(
        string tenantId,
        string userId,
        string? title,
        CancellationToken cancellationToken = default);

    Task<Conversation?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task AppendMessageAsync(
        string tenantId,
        string conversationId,
        AgentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>以一个原子事务替换会话消息，供持久化上下文压缩使用。</summary>
    Task ReplaceMessagesAsync(
        string tenantId,
        string conversationId,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken cancellationToken = default);
}

/// <summary>可安全暴露给 API 客户端的领域错误类别。</summary>
public enum MuAgentErrorCategory
{
    Configuration,
    Authentication,
    RateLimit,
    Timeout,
    Cancelled,
    InvalidModelResponse,
    ToolFailure,
    ContentFailure,
    SecurityDenied
}

/// <summary>MuAgents 领域异常，使用 Category 保留跨模块一致的错误语义。</summary>
public sealed class MuAgentException(
    MuAgentErrorCategory category,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>错误所属类别，API 流会把它序列化到 error 事件。</summary>
    public MuAgentErrorCategory Category { get; } = category;
}
