using System.Text.Json;
using MuAgents.Abstractions;

namespace MuAgents.Mcp;

public sealed class McpInvokeTool(IMcpClientManager manager) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"server":{"type":"string"},"tool":{"type":"string"},"arguments":{"type":"object"}},"required":["server","tool","arguments"],"additionalProperties":false}
        """).RootElement.Clone();
    public ToolDefinition Definition { get; } = new(
        "mcp.call", "Calls an allowed tool on a configured MCP server. Use the MCP tools API to inspect availability.", Schema);

    public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("server", out var server) || !arguments.TryGetProperty("tool", out var tool) ||
            !arguments.TryGetProperty("arguments", out var toolArguments))
            return Task.FromResult(new ToolResult("server, tool, and arguments are required.", true));
        return manager.InvokeAsync(server.GetString() ?? string.Empty, tool.GetString() ?? string.Empty, toolArguments, cancellationToken);
    }
}
