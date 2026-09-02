using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Core;

public sealed class ContextOptions
{
    public int MaxContextTokens { get; set; } = 128_000;
    public int ReservedOutputTokens { get; set; } = 4_096;
    public double CompactionRatio { get; set; } = 0.6667;
    public int SafetyMarginTokens { get; set; } = 1_024;
    public int RecentTurnsToKeep { get; set; } = 4;
}

public interface ITokenEstimator
{
    int EstimateText(string text);
    int EstimateRequest(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters);
}

public sealed class ApproximateTokenEstimator : ITokenEstimator
{
    public int EstimateText(string text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public int EstimateRequest(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters)
    {
        var total = EstimateText(parameters.SystemInstruction ?? string.Empty) + 8;
        foreach (var message in messages)
        {
            total += 6;
            foreach (var part in message.Parts)
            {
                total += part switch
                {
                    TextPart text => EstimateText(text.Text),
                    ImagePart => 1_024,
                    ToolCallPart call => EstimateText(call.Name) + EstimateText(call.ArgumentsJson) + 8,
                    ToolResultPart result => EstimateText(result.Content) + 8,
                    _ => 0
                };
            }
        }

        foreach (var tool in tools)
        {
            total += EstimateText(tool.Name) + EstimateText(tool.Description) +
                     EstimateText(tool.ParametersSchema.GetRawText()) + 12;
        }

        return total;
    }
}

public sealed record ContextPlan(
    IReadOnlyList<AgentMessage> Messages,
    int EstimatedTokens,
    bool WasCompacted,
    int OriginalEstimatedTokens);

public interface IContextManager
{
    ContextPlan Prepare(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters);
}

public sealed class ContextManager(
    ITokenEstimator estimator,
    IOptions<ContextOptions> options) : IContextManager
{
    private readonly ContextOptions _options = options.Value;

    public ContextPlan Prepare(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters)
    {
        ValidateOptions();
        var estimated = estimator.EstimateRequest(messages, tools, parameters);
        var requestBudget = _options.MaxContextTokens - _options.ReservedOutputTokens - _options.SafetyMarginTokens;
        var threshold = Math.Min(requestBudget, (int)Math.Floor(_options.MaxContextTokens * _options.CompactionRatio));

        var currentMessage = messages.LastOrDefault(x => x.Role == AgentRole.User);
        if (currentMessage is not null &&
            estimator.EstimateRequest([currentMessage], tools, parameters) >= requestBudget)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"The current input exceeds the request budget of {requestBudget} tokens. Shorten it or split attachments.");
        }

        if (estimated < threshold)
        {
            return new ContextPlan(messages, estimated, false, estimated);
        }

        var keepCount = Math.Max(2, _options.RecentTurnsToKeep * 2);
        var stableSystem = messages.Where(x => x.Role == AgentRole.System).ToArray();
        var nonSystem = messages.Where(x => x.Role != AgentRole.System).ToArray();
        var recent = nonSystem.TakeLast(keepCount).ToArray();
        var older = nonSystem.SkipLast(Math.Min(keepCount, nonSystem.Length)).ToArray();
        var checkpoint = CreateCheckpoint(older);
        var compacted = stableSystem
            .Concat([checkpoint])
            .Concat(recent)
            .DistinctBy(x => x.Id)
            .ToArray();
        var compactedTokens = estimator.EstimateRequest(compacted, tools, parameters);

        return new ContextPlan(compacted, compactedTokens, true, estimated);
    }

    private static AgentMessage CreateCheckpoint(IReadOnlyList<AgentMessage> messages)
    {
        var text = new StringBuilder("# Conversation checkpoint\n\n## Earlier conversation\n");
        foreach (var message in messages)
        {
            var content = string.Join(" ", message.Parts.Select(DescribePart));
            if (content.Length > 80)
            {
                content = content[..80] + "…";
            }

            text.Append("- ").Append(message.Role).Append(": ").AppendLine(content);
        }

        text.AppendLine("\n## Current state\nRecent messages below remain authoritative.");
        return new AgentMessage(
            $"checkpoint-{Guid.NewGuid():N}",
            AgentRole.System,
            [new TextPart(text.ToString())],
            DateTimeOffset.UtcNow,
            new MessageMetadata(Properties: new Dictionary<string, string>
            {
                ["kind"] = "compaction-checkpoint",
                ["sourceMessageIds"] = string.Join(',', messages.Select(x => x.Id))
            }));
    }

    private static string DescribePart(MessagePart part) => part switch
    {
        TextPart text => text.Text.ReplaceLineEndings(" "),
        ImagePart => "[image]",
        ToolCallPart call => $"[called {call.Name}: {call.ArgumentsJson}]",
        ToolResultPart result => $"[tool result: {result.Content.ReplaceLineEndings(" ")}]",
        _ => "[content]"
    };

    private void ValidateOptions()
    {
        if (_options.MaxContextTokens <= 0 || _options.ReservedOutputTokens < 0 ||
            _options.SafetyMarginTokens < 0 || _options.ReservedOutputTokens + _options.SafetyMarginTokens >= _options.MaxContextTokens ||
            _options.CompactionRatio is <= 0 or > 1 || _options.RecentTurnsToKeep < 1)
        {
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "Context configuration is invalid.");
        }
    }
}
