using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Tools;

namespace MuAgents.UnitTests;

public sealed class ToolGatewayTests
{
    [Fact]
    public async Task InvokeAsync_RejectsMalformedArgumentsWithoutCallingTool()
    {
        var tool = new RecordingTool();
        var gateway = new ToolGateway(
            [tool], Options.Create(new ToolGatewayOptions()), NullLogger<ToolGateway>.Instance);

        var results = await gateway.InvokeAsync(
            [new ToolInvocation("1", "test.record", "not-json")],
            new ToolExecutionContext("tenant", "conversation"));

        Assert.True(results[0].Result.IsError);
        Assert.False(tool.WasCalled);
    }

    [Fact]
    public async Task InvokeAsync_TruncatesOversizedResult()
    {
        var gateway = new ToolGateway(
            [new RecordingTool()],
            Options.Create(new ToolGatewayOptions { MaxResultCharacters = 4 }),
            NullLogger<ToolGateway>.Instance);

        var results = await gateway.InvokeAsync(
            [new ToolInvocation("1", "test.record", "{}")],
            new ToolExecutionContext("tenant", "conversation"));

        Assert.True(results[0].Result.IsTruncated);
        Assert.StartsWith("abcd", results[0].Result.Content);
    }

    [Fact]
    public async Task InvokeAsync_PassesExactCallIdToToolContext()
    {
        var tool = new RecordingTool();
        var gateway = new ToolGateway(
            [tool], Options.Create(new ToolGatewayOptions()), NullLogger<ToolGateway>.Instance);

        await gateway.InvokeAsync(
            [new ToolInvocation("approval-call-42", "test.record", "{}")],
            new ToolExecutionContext("tenant", "conversation", "user"));

        Assert.Equal("approval-call-42", tool.ToolCallId);
    }

    private sealed class RecordingTool : IAgentTool
    {
        public bool WasCalled { get; private set; }
        public string? ToolCallId { get; private set; }
        public ToolDefinition Definition { get; } = new(
            "test.record", "test", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());

        public Task<ToolResult> InvokeAsync(
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            ToolCallId = context.ToolCallId;
            return Task.FromResult(new ToolResult("abcdefgh"));
        }
    }
}
