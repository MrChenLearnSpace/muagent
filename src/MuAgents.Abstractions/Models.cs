using System.Runtime.CompilerServices;

namespace MuAgents.Abstractions;

public sealed record AgentRequest(
    IReadOnlyList<AgentMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    ModelParameters Parameters);

public sealed record ModelParameters(
    string Model,
    int MaxOutputTokens = 4096,
    double? Temperature = null,
    string? SystemInstruction = null);

public sealed record ProviderCapabilities(
    bool SupportsVision = true,
    bool SupportsTools = true,
    bool SupportsReasoning = false);

public abstract record ModelEvent;
public sealed record ModelTextDelta(string Delta) : ModelEvent;
public sealed record ModelReasoningDelta(string Delta) : ModelEvent;
public sealed record ModelToolCall(string CallId, string Name, string ArgumentsJson) : ModelEvent;
public sealed record ModelUsage(int InputTokens, int OutputTokens) : ModelEvent;
public sealed record ModelWarning(string Message) : ModelEvent;
public sealed record ModelCompleted(string? FinishReason = null) : ModelEvent;

public interface IChatModel
{
    IAsyncEnumerable<ModelEvent> CompleteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DelegateChatModel(
    Func<AgentRequest, CancellationToken, IAsyncEnumerable<ModelEvent>> completion) : IChatModel
{
    public IAsyncEnumerable<ModelEvent> CompleteAsync(
        AgentRequest request,
        CancellationToken cancellationToken = default) => completion(request, cancellationToken);

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
