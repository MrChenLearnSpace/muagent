namespace MuAgents.Abstractions;

public sealed record Conversation(
    string Id,
    string TenantId,
    string CreatedByUserId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version = 0);

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
}

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

public sealed class MuAgentException(
    MuAgentErrorCategory category,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public MuAgentErrorCategory Category { get; } = category;
}
