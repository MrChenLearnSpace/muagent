using System.Text.Json;
using MuAgents.Abstractions;

namespace MuAgents.Tools;

public sealed class CurrentTimeTool : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"utcOffset":{"type":"string"}},"additionalProperties":false}
        """).RootElement.Clone();

    public ToolDefinition Definition { get; } = new(
        "local.current_time",
        "Returns the current time for an optional UTC offset such as +08:00.",
        Schema);

    public Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        if (arguments.TryGetProperty("utcOffset", out var offsetValue) &&
            offsetValue.ValueKind == JsonValueKind.String)
        {
            if (!TimeSpan.TryParse(offsetValue.GetString(), out var offset) || Math.Abs(offset.TotalHours) > 14)
            {
                return Task.FromResult(new ToolResult("utcOffset must be between -14:00 and +14:00.", true));
            }

            now = now.ToOffset(offset);
        }

        return Task.FromResult(new ToolResult(now.ToString("O")));
    }
}
