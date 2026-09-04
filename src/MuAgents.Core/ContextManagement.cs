using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Core;

/// <summary>上下文窗口和触发压缩所需的预算配置。</summary>
public sealed class ContextOptions
{
    /// <summary>模型可接受的总上下文 Token 上限。</summary>
    public int MaxContextTokens { get; set; } = 128_000;
    /// <summary>从总预算中预留给本轮模型输出的 Token。</summary>
    public int ReservedOutputTokens { get; set; } = 4_096;
    /// <summary>估算用量达到总窗口的这个比例时提前压缩，避免顶到硬上限。</summary>
    public double CompactionRatio { get; set; } = 0.6667;
    /// <summary>为协议开销和估算误差保留的安全余量。</summary>
    public int SafetyMarginTokens { get; set; } = 1_024;
    /// <summary>压缩旧历史时完整保留的最近对话轮数。</summary>
    public int RecentTurnsToKeep { get; set; } = 4;
}

/// <summary>Token 估算接口；这是发送请求前的保护性估算，不替代供应商返回的准确用量。</summary>
public interface ITokenEstimator
{
    int EstimateText(string text);
    int EstimateRequest(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters);
}

/// <summary>无需模型专用分词器的保守近似估算器，适用于协议无关的提前预算判断。</summary>
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
                    // 图片真实 Token 与模型和分辨率相关，这里使用固定保守值避免漏算为零。
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

/// <summary>准备完成的上下文及压缩前后估算信息。</summary>
public sealed record ContextPlan(
    IReadOnlyList<AgentMessage> Messages,
    int EstimatedTokens,
    bool WasCompacted,
    int OriginalEstimatedTokens);

/// <summary>在模型调用前校验预算并按需压缩历史的接口。</summary>
public interface IContextManager
{
    /// <summary>返回完整请求的近似 Token 数。</summary>
    int Estimate(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters);

    ContextPlan Prepare(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters);

    /// <summary>把消息压缩到指定 Token 目标以内；目标必须小于有效上下文窗口。</summary>
    ContextPlan CompactToTarget(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters,
        int targetTokens);
}

/// <summary>保留系统指令与最近轮次、将更早历史折叠为检查点的上下文管理器。</summary>
public sealed class ContextManager(
    ITokenEstimator estimator,
    IOptions<ContextOptions> options) : IContextManager
{
    private readonly ContextOptions _options = options.Value;

    public int Estimate(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters) => estimator.EstimateRequest(messages, tools, parameters);

    public ContextPlan Prepare(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters)
    {
        ValidateOptions();
        var estimated = estimator.EstimateRequest(messages, tools, parameters);
        // 请求预算必须同时扣除输出预留与估算误差，否则供应商仍可能因完整请求超窗而拒绝。
        var requestBudget = _options.MaxContextTokens - _options.ReservedOutputTokens - _options.SafetyMarginTokens;
        var threshold = Math.Min(requestBudget, (int)Math.Floor(_options.MaxContextTokens * _options.CompactionRatio));

        // 当前输入本身超限时，压缩历史也无济于事，应明确要求调用方缩短文本或拆分附件。
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
        // 系统消息决定行为，不能被摘要替换；最近轮次保留原文以维持工具调用和指代的准确性。
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

    public ContextPlan CompactToTarget(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelParameters parameters,
        int targetTokens)
    {
        ValidateOptions();
        if (targetTokens <= 0 || targetTokens >= _options.MaxContextTokens)
            throw new ArgumentOutOfRangeException(nameof(targetTokens));

        var before = estimator.EstimateRequest(messages, tools, parameters);
        if (before <= targetTokens)
            return new ContextPlan(messages, before, false, before);

        // 手动压缩必须达到硬目标，因此先把全部历史折叠为单个检查点，再按估算结果收紧摘要长度。
        var checkpoint = CreateCheckpoint(messages);
        var fixedCost = estimator.EstimateRequest([], tools, parameters);
        if (fixedCost >= targetTokens)
            throw new MuAgentException(
                MuAgentErrorCategory.Configuration,
                "Tool definitions and system instruction alone exceed the manual compaction target.");

        var text = ((TextPart)checkpoint.Parts[0]).Text;
        while (true)
        {
            var compacted = checkpoint with { Parts = [new TextPart(text)] };
            var after = estimator.EstimateRequest([compacted], tools, parameters);
            if (after <= targetTokens)
                return new ContextPlan([compacted], after, true, before);

            var excessCharacters = Math.Max(16, (after - targetTokens) * 4);
            var nextLength = Math.Max(0, text.Length - excessCharacters);
            if (nextLength >= text.Length)
                throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Context could not be compacted to the requested target.");
            text = text[..nextLength];
        }
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
        // 在元数据中保留来源消息 ID，便于未来审计一次压缩覆盖了哪些历史。
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
