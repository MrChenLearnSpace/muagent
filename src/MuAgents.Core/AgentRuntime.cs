using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Core;

public sealed class AgentOptions
{
    public int MaxToolIterations { get; set; } = 12;
}

public sealed record AgentRunRequest(
    string TenantId,
    string UserId,
    string ConversationId,
    string? Text,
    ModelParameters Parameters,
    IReadOnlyList<ImagePart>? Images = null);

public abstract record AgentEvent;
public sealed record TextDeltaEvent(string Delta) : AgentEvent;
public sealed record ReasoningDeltaEvent(string Delta) : AgentEvent;
public sealed record ToolCallStartedEvent(string CallId, string Name) : AgentEvent;
public sealed record ToolCallCompletedEvent(string CallId, string Name, bool IsError, long DurationMilliseconds) : AgentEvent;
public sealed record CompactionStartedEvent(int EstimatedTokens) : AgentEvent;
public sealed record CompactionCompletedEvent(int BeforeTokens, int AfterTokens) : AgentEvent;
public sealed record UsageUpdatedEvent(int InputTokens, int OutputTokens) : AgentEvent;
public sealed record WarningEvent(string Message) : AgentEvent;
public sealed record CompletedEvent(string? FinishReason = null) : AgentEvent;

public sealed class AgentRuntime(
    IChatModel model,
    IToolGateway tools,
    IConversationStore conversations,
    IContextManager contextManager,
    IOptions<AgentOptions> options,
    ILogger<AgentRuntime> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ConversationLocks = new();
    private readonly AgentOptions _options = options.Value;

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text) && request.Images is not { Count: > 0 })
        {
            throw new ArgumentException("Message text or an image is required.", nameof(request));
        }

        var gateKey = $"{request.TenantId}\n{request.ConversationId}";
        var gate = ConversationLocks.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            for (var iteration = 0; iteration <= _options.MaxToolIterations; iteration++)
            {
                var history = await conversations.GetMessagesAsync(
                    request.TenantId,
                    request.ConversationId,
                    cancellationToken).ConfigureAwait(false);
                var plan = contextManager.Prepare(history, tools.Definitions, request.Parameters);
                if (plan.WasCompacted)
                {
                    yield return new CompactionStartedEvent(plan.OriginalEstimatedTokens);
                    yield return new CompactionCompletedEvent(plan.OriginalEstimatedTokens, plan.EstimatedTokens);
                }

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

                if (calls.Count == 0)
                {
                    yield return new CompletedEvent(finishReason);
                    yield break;
                }

                if (iteration == _options.MaxToolIterations)
                {
                    yield return new WarningEvent("Maximum tool iterations reached.");
                    yield return new CompletedEvent("max_tool_iterations");
                    yield break;
                }

                foreach (var call in calls)
                {
                    yield return new ToolCallStartedEvent(call.CallId, call.Name);
                }

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
            gate.Release();
            logger.LogDebug("Agent run finished for conversation {ConversationId}", request.ConversationId);
        }
    }
}
