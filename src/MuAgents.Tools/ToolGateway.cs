using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Tools;

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

        using (arguments)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.Timeout);
            try
            {
                var result = await tool.InvokeAsync(arguments.RootElement, context, timeout.Token)
                    .ConfigureAwait(false);
                return Finish(Limit(result));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Finish(new ToolResult($"Tool '{call.Name}' timed out.", true));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Tool {ToolName} failed for call {CallId}", call.Name, call.CallId);
                return Finish(new ToolResult($"Tool '{call.Name}' failed.", true));
            }
        }

        ToolInvocationResult Finish(ToolResult result) =>
            new(call.CallId, call.Name, result, stopwatch.Elapsed);
    }

    private ToolResult Limit(ToolResult result)
    {
        if (result.Content.Length <= _options.MaxResultCharacters)
        {
            return result;
        }

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
