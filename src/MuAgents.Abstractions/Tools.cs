using System.Text.Json;

namespace MuAgents.Abstractions;

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    bool IsMutating = false);

public sealed record ToolResult(string Content, bool IsError = false, bool IsTruncated = false);

public sealed record ToolExecutionContext(
    string TenantId,
    string ConversationId,
    string? UserId = null,
    IServiceProvider? Services = null);

public interface IAgentTool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ToolInvocation(string CallId, string Name, string ArgumentsJson);
public sealed record ToolInvocationResult(string CallId, string Name, ToolResult Result, TimeSpan Duration);

public interface IToolGateway
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    Task<IReadOnlyList<ToolInvocationResult>> InvokeAsync(
        IReadOnlyList<ToolInvocation> calls,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
