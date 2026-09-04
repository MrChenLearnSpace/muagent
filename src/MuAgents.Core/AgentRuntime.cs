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
    public int MaxToolIterations { get; set; } = 24;
    /// <summary>模型成功响应但没有正文或工具调用时的重试次数，避免把空流误报为正常回答。</summary>
    public int MaxEmptyResponseRetries { get; set; } = 2;
    /// <summary>
    /// 所有会话都会收到的可信代理规则。调用方的自定义 SystemInstruction 会附加在其后，
    /// 不能意外移除“编码任务必须实际落地”的默认行为。
    /// </summary>
    public string DefaultSystemInstruction { get; set; } = """
        You are MuAgent, an agent that works directly in the current project. Treat the supplied conversation history as authoritative: resolve references such as "刚才", "继续", "那个文件", and "原来的功能" from earlier user messages, assistant actions, and tool results instead of treating each message as a new conversation.
        When the user asks you to create, modify, fix, build, or test software, use the available tools to perform the work in the project. First inspect relevant files with local.list_files and local.read_file, write complete file contents with local.write_file, and validate the result with local.execute_command when execution is permitted. Do not merely paste proposed code into chat when the user asked for a working project. Continue through tool results, correct failures when possible, and only give the final concise summary after the requested files and validation are complete. Never claim that a file was created or changed unless a successful tool result confirms it. All writable environment and temporary paths are already isolated under the project's .muagent directory: never override those environment variables or create/write paths outside the project. Invoke executables directly and only use a shell wrapper when the requested operation genuinely requires shell syntax. If a required mutation is denied by host policy, state the exact blocked tool or policy instead of pretending the work was completed.
        """;
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
public sealed record ToolCallStartedEvent(string CallId, string Name, string? ArgumentsJson = null) : AgentEvent;
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
        var parameters = WithDefaultInstruction(request.Parameters);

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
            var emptyResponseAttempts = 0;
            for (var iteration = 0; iteration < _options.MaxToolIterations; iteration++)
            {
                var storedHistory = await conversations.GetMessagesAsync(
                    request.TenantId,
                    request.ConversationId,
                    cancellationToken).ConfigureAwait(false);
                var history = RemoveOrphanedToolParts(storedHistory, out var removedOrphans);
                if (removedOrphans > 0)
                {
                    // 兼容旧版本留下的悬空调用。持久化修复后，重启及后续轮次都使用合法历史。
                    await conversations.ReplaceMessagesAsync(
                        request.TenantId,
                        request.ConversationId,
                        history,
                        cancellationToken).ConfigureAwait(false);
                    yield return new WarningEvent($"Repaired {removedOrphans} orphaned tool history part(s) from this conversation.");
                }
                var plan = contextManager.Prepare(history, tools.Definitions, parameters);
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
                                   new AgentRequest(plan.Messages, tools.Definitions, parameters),
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

                if (text.Length == 0 && calls.Count == 0)
                {
                    emptyResponseAttempts++;
                    if (emptyResponseAttempts <= _options.MaxEmptyResponseRetries)
                    {
                        yield return new WarningEvent(
                            $"Model returned an empty response; retrying ({emptyResponseAttempts}/{_options.MaxEmptyResponseRetries}).");
                        iteration--;
                        continue;
                    }

                    throw new MuAgentException(
                        MuAgentErrorCategory.InvalidModelResponse,
                        $"Model returned an empty response after {_options.MaxEmptyResponseRetries} retries.");
                }
                emptyResponseAttempts = 0;

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
                            usage is null ? null : new MessageMetadata(parameters.Model, usage.InputTokens, usage.OutputTokens)),
                        cancellationToken).ConfigureAwait(false);
                }

                // 没有工具调用即表示模型已给出最终回答，不再进入下一次迭代。
                if (calls.Count == 0)
                {
                    runCompleted = true;
                    yield return new CompletedEvent(finishReason);
                    yield break;
                }

                foreach (var call in calls)
                {
                    // 控制台参数可以直接展示；文件写入只公开去除正文后的摘要，避免大段源码进入审批事件。
                    yield return new ToolCallStartedEvent(
                        call.CallId,
                        call.Name,
                        BuildClientVisibleArguments(call));
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

                // 已持久化的每个工具调用都必须拥有对应结果。只有结果全部落库后才能在轮次上限处停止，
                // 否则下一轮会话会携带悬空调用，部分兼容模型会忽略后续用户消息或直接返回空响应。
                if (iteration == _options.MaxToolIterations - 1)
                {
                    yield return new WarningEvent("Maximum tool iterations reached after completing the pending tool calls.");
                    runCompleted = true;
                    yield return new CompletedEvent("max_tool_iterations");
                    yield break;
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
            contextManager.Estimate(history, tools.Definitions, WithDefaultInstruction(parameters)),
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
            var plan = contextManager.CompactToTarget(
                history, tools.Definitions, WithDefaultInstruction(parameters), target);
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

    private ModelParameters WithDefaultInstruction(ModelParameters parameters) => parameters with
    {
        SystemInstruction = string.Join("\n\n", new[]
        {
            _options.DefaultSystemInstruction,
            parameters.SystemInstruction
        }.Where(value => !string.IsNullOrWhiteSpace(value)))
    };

    private static IReadOnlyList<AgentMessage> RemoveOrphanedToolParts(
        IReadOnlyList<AgentMessage> messages,
        out int removedCount)
    {
        var callIds = messages.SelectMany(message => message.Parts).OfType<ToolCallPart>()
            .Select(part => part.CallId).ToHashSet(StringComparer.Ordinal);
        var resultIds = messages.SelectMany(message => message.Parts).OfType<ToolResultPart>()
            .Select(part => part.CallId).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        var normalized = new List<AgentMessage>(messages.Count);
        foreach (var message in messages)
        {
            var parts = message.Parts.Where(part =>
            {
                var keep = part switch
                {
                    ToolCallPart call => resultIds.Contains(call.CallId),
                    ToolResultPart result => callIds.Contains(result.CallId),
                    _ => true
                };
                if (!keep) removed++;
                return keep;
            }).ToArray();
            if (parts.Length > 0) normalized.Add(message with { Parts = parts });
        }
        removedCount = removed;
        return removed == 0 ? messages : normalized;
    }

    private static string? BuildClientVisibleArguments(ToolInvocation call)
    {
        if (call.Name == "local.execute_command") return call.ArgumentsJson;
        if (call.Name != "local.write_file") return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
            var root = document.RootElement;
            var path = root.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? pathElement.GetString()
                : null;
            var characters = root.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? contentElement.GetString()?.Length ?? 0
                : 0;
            var overwrite = !root.TryGetProperty("overwrite", out var overwriteElement) ||
                            overwriteElement.ValueKind == System.Text.Json.JsonValueKind.True;
            return System.Text.Json.JsonSerializer.Serialize(new { path, characters, overwrite });
        }
        catch (System.Text.Json.JsonException)
        {
            return "{}";
        }
    }
}
