using System.Text.Json;

namespace MuAgents.Abstractions;

/// <summary>暴露给模型的工具说明和 JSON Schema；IsMutating 标记会改变外部状态的工具。</summary>
public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement ParametersSchema,
    bool IsMutating = false);

/// <summary>标准化工具结果，可表示业务错误或因安全上限发生的截断。</summary>
public sealed record ToolResult(string Content, bool IsError = false, bool IsTruncated = false);

/// <summary>工具执行上下文，携带租户、会话、用户和可选宿主服务。</summary>
public sealed record ToolExecutionContext(
    string TenantId,
    string ConversationId,
    string? UserId = null,
    IServiceProvider? Services = null,
    string? ToolCallId = null);

/// <summary>单个模型可调用工具的实现契约。</summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }

    Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>标记由工具自身分别管理审批等待和执行超时，网关不再叠加统一工具超时。</summary>
public interface IManagesOwnToolTimeout { }

/// <summary>模型生成并等待网关执行的一次工具调用。</summary>
public sealed record ToolInvocation(string CallId, string Name, string ArgumentsJson);
/// <summary>工具调用完成后的结果和墙钟耗时。</summary>
public sealed record ToolInvocationResult(string CallId, string Name, ToolResult Result, TimeSpan Duration);

/// <summary>工具聚合网关，负责名称解析、并发、超时、截断和统一错误处理。</summary>
public interface IToolGateway
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    Task<IReadOnlyList<ToolInvocationResult>> InvokeAsync(
        IReadOnlyList<ToolInvocation> calls,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default);
}
