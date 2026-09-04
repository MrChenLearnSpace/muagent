using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Core;

/// <summary>控制单轮智能体允许的最大模型—工具往返次数。</summary>
public sealed class AgentOptions
{
    public int MaxToolIterations { get; set; } = 12;
}

/// <summary>启动一轮智能体运行所需的可信身份、会话、用户输入和模型参数。</summary>
public sealed record AgentRunRequest(
    string TenantId,
    string UserId,
    string ConversationId,
    string? Text,
    ModelParameters Parameters,
    IReadOnlyList<ImagePart>? Images = null);

/// <summary>向 HTTP/CLI 调用方公开的智能体事件基类。</summary>
public abstract record AgentEvent;
/// <summary>最终回答正文增量。</summary>
public sealed record TextDeltaEvent(string Delta) : AgentEvent;
/// <summary>供应商公开的推理增量。</summary>
public sealed record ReasoningDeltaEvent(string Delta) : AgentEvent;
/// <summary>工具即将执行。</summary>
public sealed record ToolCallStartedEvent(string CallId, string Name) : AgentEvent;
/// <summary>工具执行结束及结果状态。</summary>
public sealed record ToolCallCompletedEvent(string CallId, string Name, bool IsError, long DurationMilliseconds) : AgentEvent;
/// <summary>上下文压缩开始，携带压缩前估算量。</summary>
public sealed record CompactionStartedEvent(int EstimatedTokens) : AgentEvent;
/// <summary>上下文压缩完成，携带前后估算量。</summary>
public sealed record CompactionCompletedEvent(int BeforeTokens, int AfterTokens) : AgentEvent;
/// <summary>模型报告了新的 Token 用量。</summary>
public sealed record UsageUpdatedEvent(int InputTokens, int OutputTokens) : AgentEvent;
/// <summary>不终止运行的警告。</summary>
public sealed record WarningEvent(string Message) : AgentEvent;
/// <summary>本轮正常结束及停止原因。</summary>
public sealed record CompletedEvent(string? FinishReason = null) : AgentEvent;

/// <summary>会话当前上下文估算、上限和手动压缩目标。</summary>
public sealed record AgentContextStatus(int CurrentTokens, int MaxContextTokens, int CompactTargetTokens);

/// <summary>
/// 智能体编排核心：持久化输入、准备上下文、消费模型流、执行工具并继续下一次模型调用。
/// </summary>
public sealed class AgentRuntime(
    IChatModel model,
    IToolGateway tools,
    IConversationStore conversations,
    IContextManager contextManager,
    IOptions<AgentOptions> options,
    IOptions<ContextOptions> contextOptions,
    ILogger<AgentRuntime> logger)
{
    // 同一租户同一会话只允许一轮写入，避免两次请求交叉追加消息而破坏上下文顺序。
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConversationLocks = new();
    private readonly AgentOptions _options = options.Value;
    private readonly ContextOptions _contextOptions = contextOptions.Value;

    /// <summary>执行并流式返回一轮智能体事件；枚举结束即代表本轮运行结束。</summary>
    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && request.Images is not { Count: > 0 })
        {
            throw new ArgumentException("Message text or an image is required.", nameof(request));
        }

        var startedAt = Stopwatch.GetTimestamp();
        var modelTag = new KeyValuePair<string, object?>("model", request.Parameters.Model);
        MuAgentsTelemetry.AgentRuns.Add(1, modelTag);
        using var activity = MuAgentsTelemetry.Activities.StartActivity("agent.run", ActivityKind.Internal);
        activity?.SetTag("gen_ai.request.model", request.Parameters.Model);
        activity?.SetTag("muagents.has_images", request.Images is { Count: > 0 });
        var runCompleted = false;
        var gateKey = $"{request.TenantId}\n{request.ConversationId}";
        var gate = ConversationLocks.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        var gateEntered = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            var conversation = await conversations.GetAsync(
                request.TenantId,
                request.ConversationId,
                cancellationToken).ConfigureAwait(false);
            if (conversation is null)
            {
                throw new KeyNotFoundException("Conversation was not found in this tenant.");
            }

            var userParts = new List<MessagePart>();
            if (!string.IsNullOrWhiteSpace(request.Text)) userParts.Add(new TextPart(request.Text));
            if (request.Images is not null) userParts.AddRange(request.Images);
            await conversations.AppendMessageAsync(
                request.TenantId,
                request.ConversationId,
                new AgentMessage(Guid.NewGuid().ToString("N"), AgentRole.User, userParts, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            // 每次工具结果落库后重新读取历史，使下一次模型调用看到完整且权威的消息顺序。
            for (var iteration = 0; iteration <= _options.MaxToolIterations; iteration++)
            {
                var history = await conversations.GetMessagesAsync(
                    request.TenantId,
                    request.ConversationId,
                    cancellationToken).ConfigureAwait(false);
                var plan = contextManager.Prepare(history, tools.Definitions, request.Parameters);
                if (plan.WasCompacted)
                {
                    // 自动压缩后的检查点必须落库，否则下一轮仍会重复携带并压缩同一批旧消息。
                    await conversations.ReplaceMessagesAsync(
                        request.TenantId,
                        request.ConversationId,
                        plan.Messages,
                        cancellationToken).ConfigureAwait(false);
                    MuAgentsTelemetry.Compactions.Add(1, modelTag);
                    activity?.AddEvent(new ActivityEvent(
                        "context.compacted",
                        tags: new ActivityTagsCollection
                        {
                            ["muagents.context.before_tokens"] = plan.OriginalEstimatedTokens,
                            ["muagents.context.after_tokens"] = plan.EstimatedTokens
                        }));
                    yield return new CompactionStartedEvent(plan.OriginalEstimatedTokens);
                    yield return new CompactionCompletedEvent(plan.OriginalEstimatedTokens, plan.EstimatedTokens);
                }

                // 流式增量立即转发给客户端，同时聚合一份完整消息用于持久化。
                var text = new StringBuilder();
                var calls = new List<ToolInvocation>();
                var finishReason = default(string);
                var usage = default(ModelUsage);
                await foreach (var modelEvent in model.CompleteAsync(
                                   new AgentRequest(plan.Messages, tools.Definitions, request.Parameters),
                                   cancellationToken).ConfigureAwait(false))
                {
                    switch (modelEvent)
                    {
                        case ModelTextDelta delta:
                            text.Append(delta.Delta);
                            yield return new TextDeltaEvent(delta.Delta);
                            break;
                        case ModelReasoningDelta delta:
                            yield return new ReasoningDeltaEvent(delta.Delta);
                            break;
                        case ModelToolCall call:
                            calls.Add(new ToolInvocation(call.CallId, call.Name, call.ArgumentsJson));
                            break;
                        case ModelUsage modelUsage:
                            usage = modelUsage;
                            yield return new UsageUpdatedEvent(modelUsage.InputTokens, modelUsage.OutputTokens);
                            break;
                        case ModelWarning warning:
                            yield return new WarningEvent(warning.Message);
                            break;
                        case ModelCompleted completed:
                            finishReason = completed.FinishReason;
                            break;
                    }
                }

                var parts = new List<MessagePart>();
                if (text.Length > 0)
                {
                    parts.Add(new TextPart(text.ToString()));
                }

                parts.AddRange(calls.Select(call => new ToolCallPart(call.CallId, call.Name, call.ArgumentsJson)));
                if (parts.Count > 0)
                {
                    await conversations.AppendMessageAsync(
                        request.TenantId,
                        request.ConversationId,
                        new AgentMessage(
                            Guid.NewGuid().ToString("N"),
                            AgentRole.Assistant,
                            parts,
                            DateTimeOffset.UtcNow,
                            usage is null ? null : new MessageMetadata(request.Parameters.Model, usage.InputTokens, usage.OutputTokens)),
                        cancellationToken).ConfigureAwait(false);
                }

                // 没有工具调用即表示模型已给出最终回答，不再进入下一次迭代。
                if (calls.Count == 0)
                {
                    runCompleted = true;
                    yield return new CompletedEvent(finishReason);
                    yield break;
                }

                if (iteration == _options.MaxToolIterations)
                {
                    yield return new WarningEvent("Maximum tool iterations reached.");
                    runCompleted = true;
                    yield return new CompletedEvent("max_tool_iterations");
                    yield break;
                }

                foreach (var call in calls)
                {
                    yield return new ToolCallStartedEvent(call.CallId, call.Name);
                }

                // 网关内部负责并发上限与超时；这里按返回顺序逐条落库并发出完成事件。
                var toolResults = await tools.InvokeAsync(
                    calls,
                    new ToolExecutionContext(request.TenantId, request.ConversationId, request.UserId),
                    cancellationToken).ConfigureAwait(false);
                foreach (var result in toolResults)
                {
                    await conversations.AppendMessageAsync(
                        request.TenantId,
                        request.ConversationId,
                        new AgentMessage(
                            Guid.NewGuid().ToString("N"),
                            AgentRole.Tool,
                            [new ToolResultPart(result.CallId, result.Result.Content, result.Result.IsError)],
                            DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);
                    yield return new ToolCallCompletedEvent(
                        result.CallId,
                        result.Name,
                        result.Result.IsError,
                        (long)result.Duration.TotalMilliseconds);
                }
            }
        }
        finally
        {
            if (gateEntered) gate.Release();
            var outcome = runCompleted
                ? "success"
                : cancellationToken.IsCancellationRequested ? "cancelled" : "error";
            activity?.SetTag("muagents.outcome", outcome);
            if (outcome == "error")
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                MuAgentsTelemetry.AgentFailures.Add(1, modelTag);
            }
            MuAgentsTelemetry.AgentDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                modelTag,
                new KeyValuePair<string, object?>("outcome", outcome));
            logger.LogDebug("Agent run finished for conversation {ConversationId}", request.ConversationId);
        }
    }

    /// <summary>按与模型请求相同的估算方式计算当前持久化会话大小。</summary>
    public async Task<AgentContextStatus> GetContextStatusAsync(
        string tenantId,
        string conversationId,
        ModelParameters parameters,
        CancellationToken cancellationToken = default)
    {
        if (await conversations.GetAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false) is null)
            throw new KeyNotFoundException("Conversation was not found in this tenant.");
        var history = await conversations.GetMessagesAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
        return new AgentContextStatus(
            contextManager.Estimate(history, tools.Definitions, parameters),
            _contextOptions.MaxContextTokens,
            Math.Max(1, _contextOptions.MaxContextTokens / 3));
    }

    /// <summary>在会话独占锁内把上下文持久化压缩到最大窗口的三分之一以内。</summary>
    public async Task<AgentContextStatus> CompactAsync(
        string tenantId,
        string conversationId,
        ModelParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var gateKey = $"{tenantId}\n{conversationId}";
        var gate = ConversationLocks.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await conversations.GetAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false) is null)
                throw new KeyNotFoundException("Conversation was not found in this tenant.");
            var history = await conversations.GetMessagesAsync(tenantId, conversationId, cancellationToken).ConfigureAwait(false);
            var target = Math.Max(1, _contextOptions.MaxContextTokens / 3);
            var plan = contextManager.CompactToTarget(history, tools.Definitions, parameters, target);
            if (plan.WasCompacted)
            {
                await conversations.ReplaceMessagesAsync(
                    tenantId,
                    conversationId,
                    plan.Messages,
                    cancellationToken).ConfigureAwait(false);
                MuAgentsTelemetry.Compactions.Add(1, new KeyValuePair<string, object?>("model", parameters.Model));
            }
            return new AgentContextStatus(plan.EstimatedTokens, _contextOptions.MaxContextTokens, target);
        }
        finally
        {
            gate.Release();
        }
    }
}
