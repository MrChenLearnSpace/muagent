using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Tools;

/// <summary>集中执行工具名称解析、JSON 参数解析、并发限制、超时、截断和遥测。</summary>
public sealed class ToolGateway : IToolGateway
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ToolGatewayOptions _options;
    private readonly ILogger<ToolGateway> _logger;

    public ToolGateway(
        IEnumerable<IAgentTool> tools,
        IOptions<ToolGatewayOptions> options,
        ILogger<ToolGateway> logger)
    {
        _options = options.Value;
        _logger = logger;
        // 工具名是模型请求中的协议标识，必须精确匹配且全局唯一，不能依赖大小写模糊解析。
        var registered = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            ValidateName(tool.Definition.Name);
            if (!registered.TryAdd(tool.Definition.Name, tool))
            {
                throw new InvalidOperationException($"Duplicate tool name '{tool.Definition.Name}'.");
            }
        }

        _tools = registered;
        Definitions = registered.Values.Select(x => x.Definition).ToArray();
    }

    public IReadOnlyList<ToolDefinition> Definitions { get; }

    public async Task<IReadOnlyList<ToolInvocationResult>> InvokeAsync(
        IReadOnlyList<ToolInvocation> calls,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // 并发执行可降低多个独立工具的总耗时；用原始索引收集结果以维持模型调用顺序。
        using var concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));
        var results = new ConcurrentDictionary<int, ToolInvocationResult>();

        await Task.WhenAll(calls.Select(async (call, index) =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await InvokeOneAsync(call, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Release();
            }
        })).ConfigureAwait(false);

        return Enumerable.Range(0, calls.Count).Select(index => results[index]).ToArray();
    }

    private async Task<ToolInvocationResult> InvokeOneAsync(
        ToolInvocation call,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolTag = new KeyValuePair<string, object?>("tool", call.Name);
        MuAgentsTelemetry.ToolInvocations.Add(1, toolTag);
        using var activity = MuAgentsTelemetry.Activities.StartActivity("tool.invoke", ActivityKind.Internal);
        activity?.SetTag("gen_ai.tool.name", call.Name);
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return Finish(new ToolResult($"Tool '{call.Name}' is not registered.", true));
        }

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
            {
                arguments.Dispose();
                return Finish(new ToolResult("Tool arguments must be a JSON object.", true));
            }
        }
        catch (JsonException)
        {
            return Finish(new ToolResult("Tool arguments are not valid JSON.", true));
        }

        // 链接调用方取消信号与工具超时：前者继续向上抛出，后者转换为可反馈给模型的工具错误。
        using (arguments)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            if (arguments.RootElement.TryGetProperty("_muagents_error", out var protocolError) &&
                protocolError.ValueKind == JsonValueKind.String)
            {
                return Finish(new ToolResult(protocolError.GetString()!, true));
            }
            if (tool is not IManagesOwnToolTimeout) timeout.CancelAfter(_options.Timeout);
            try
            {
                // 工具获得本次精确调用 ID；审批协调器据此把用户决定绑定到唯一调用。
                var callContext = context with { ToolCallId = call.CallId };
                var result = await tool.InvokeAsync(arguments.RootElement, callContext, timeout.Token)
                    .ConfigureAwait(false);
                return Finish(Limit(result));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Finish(new ToolResult($"Tool '{call.Name}' timed out.", true));
            }
            catch (Exception exception)
            {
                // 详细异常只进入服务日志，返回模型的文本不暴露路径、凭据或内部栈信息。
                _logger.LogError(exception, "Tool {ToolName} failed for call {CallId}", call.Name, call.CallId);
                return Finish(new ToolResult($"Tool '{call.Name}' failed.", true));
            }
        }

        ToolInvocationResult Finish(ToolResult result)
        {
            stopwatch.Stop();
            var outcome = result.IsError ? "error" : "success";
            activity?.SetTag("muagents.outcome", outcome);
            activity?.SetTag("muagents.tool.truncated", result.IsTruncated);
            if (result.IsError)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                MuAgentsTelemetry.ToolFailures.Add(1, toolTag);
            }
            MuAgentsTelemetry.ToolDuration.Record(
                stopwatch.Elapsed.TotalSeconds,
                toolTag,
                new KeyValuePair<string, object?>("outcome", outcome));
            return new ToolInvocationResult(call.CallId, call.Name, result, stopwatch.Elapsed);
        }
    }

    private ToolResult Limit(ToolResult result)
    {
        if (result.Content.Length <= _options.MaxResultCharacters)
        {
            return result;
        }

        // 截断标志会进入遥测和持久化结果，模型也能从尾部标记知道内容并不完整。
        return result with
        {
            Content = result.Content[.._options.MaxResultCharacters] + "\n[tool result truncated]",
            IsTruncated = true
        };
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
        {
            throw new InvalidOperationException($"Invalid tool name '{name}'.");
        }
    }
}
