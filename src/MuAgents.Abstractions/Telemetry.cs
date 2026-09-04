using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MuAgents.Abstractions;

/// <summary>
/// MuAgents 全局遥测入口。名称保持稳定，使宿主无需引用具体实现即可订阅活动和指标。
/// </summary>
public static class MuAgentsTelemetry
{
    /// <summary>ActivitySource 与 Meter 共用的逻辑名称。</summary>
    public const string SourceName = "MuAgents";
    public const string Version = "0.1.0";

    public static readonly ActivitySource Activities = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    // 指标描述使用英文是为了与 OpenTelemetry 后端和跨语言仪表盘保持一致。
    public static readonly Counter<long> AgentRuns = Meter.CreateCounter<long>(
        "muagents.agent.runs", description: "Number of agent runs started.");
    public static readonly Counter<long> AgentFailures = Meter.CreateCounter<long>(
        "muagents.agent.failures", description: "Number of agent runs that failed.");
    public static readonly Histogram<double> AgentDuration = Meter.CreateHistogram<double>(
        "muagents.agent.duration", "s", "Agent run duration.");
    public static readonly Counter<long> Compactions = Meter.CreateCounter<long>(
        "muagents.context.compactions", description: "Number of context compactions.");

    public static readonly Counter<long> ModelRequests = Meter.CreateCounter<long>(
        "muagents.model.requests", description: "Number of model requests started.");
    public static readonly Counter<long> ModelFailures = Meter.CreateCounter<long>(
        "muagents.model.failures", description: "Number of model requests that failed.");
    public static readonly Histogram<double> ModelDuration = Meter.CreateHistogram<double>(
        "muagents.model.duration", "s", "Model request duration.");
    public static readonly Histogram<double> ModelFirstEventDuration = Meter.CreateHistogram<double>(
        "muagents.model.first_event.duration", "s", "Time to the first streamed model event.");
    public static readonly Counter<long> ModelInputTokens = Meter.CreateCounter<long>(
        "muagents.model.input_tokens", "{token}", "Model input tokens reported by the provider.");
    public static readonly Counter<long> ModelOutputTokens = Meter.CreateCounter<long>(
        "muagents.model.output_tokens", "{token}", "Model output tokens reported by the provider.");

    public static readonly Counter<long> ToolInvocations = Meter.CreateCounter<long>(
        "muagents.tool.invocations", description: "Number of tool invocations.");
    public static readonly Counter<long> ToolFailures = Meter.CreateCounter<long>(
        "muagents.tool.failures", description: "Number of failed tool invocations.");
    public static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "muagents.tool.duration", "s", "Tool invocation duration.");
}
