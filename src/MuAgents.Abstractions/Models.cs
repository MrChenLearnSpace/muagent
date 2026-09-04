using System.Runtime.CompilerServices;

namespace MuAgents.Abstractions;

/// <summary>发送给模型适配器的完整请求，包含上下文消息、可用工具和本次生成参数。</summary>
public sealed record AgentRequest(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    ModelParameters Parameters);

/// <summary>与单次生成相关的模型参数；可覆盖宿主配置中的默认值。</summary>
public sealed record ModelParameters(
    string Model,
    int MaxOutputTokens = 4096,
    double? Temperature = null,
    string? SystemInstruction = null);

/// <summary>模型服务能力声明，上层据此决定是否发送图片、工具或推理内容。</summary>
public sealed record ProviderCapabilities(
    bool SupportsVision = true,
    bool SupportsTools = true,
    bool SupportsReasoning = false);

/// <summary>模型流式事件基类，屏蔽不同供应商的 SSE 事件格式。</summary>
public abstract record ModelEvent;
/// <summary>模型正文的增量文本。</summary>
public sealed record ModelTextDelta(string Delta) : ModelEvent;
/// <summary>模型推理内容的增量文本；是否提供取决于供应商。</summary>
public sealed record ModelReasoningDelta(string Delta) : ModelEvent;
/// <summary>模型完成组装后的工具调用。</summary>
public sealed record ModelToolCall(string CallId, string Name, string ArgumentsJson) : ModelEvent;
/// <summary>供应商报告的输入与输出 Token 用量。</summary>
public sealed record ModelUsage(int InputTokens, int OutputTokens) : ModelEvent;
/// <summary>不终止本轮运行、但应通知调用方的兼容性或内容警告。</summary>
public sealed record ModelWarning(string Message) : ModelEvent;
/// <summary>模型流结束事件及其可选停止原因。</summary>
public sealed record ModelCompleted(string? FinishReason = null) : ModelEvent;

/// <summary>流式聊天模型统一接口。</summary>
public interface IChatModel
{
    IAsyncEnumerable<ModelEvent> CompleteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>用委托构造模型实现，主要用于测试和宿主按需注入。</summary>
public sealed class DelegateChatModel(
    Func<AgentRequest, CancellationToken, IAsyncEnumerable<ModelEvent>> completion) : IChatModel
{
    public IAsyncEnumerable<ModelEvent> CompleteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default) => completion(request, cancellationToken);

    /// <summary>把同步事件序列转换为尊重取消信号的异步事件流。</summary>
    public static async IAsyncEnumerable<ModelEvent> FromEvents(
        IEnumerable<ModelEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}
