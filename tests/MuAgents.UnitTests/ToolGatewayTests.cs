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

    private sealed class RecordingTool : IAgentTool
    {
        public bool WasCalled { get; private set; }
        public ToolDefinition Definition { get; } = new(
            "test.record", "test", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone());

        public Task<ToolResult> InvokeAsync(
            JsonElement arguments,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ToolResult("abcdefgh"));
        }
    }
}
