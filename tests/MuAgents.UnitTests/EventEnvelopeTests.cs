using System.Text.Json;
using MuAgents.Core;

namespace MuAgents.UnitTests;

public sealed class EventEnvelopeTests
{
    [Fact]
    public async Task WriteAsync_UsesCamelCaseForEnvelopeAndEventData()
    {
        await using var stream = new MemoryStream();

        await EventEnvelope.WriteAsync(
            stream,
            EventEnvelope.From(new TextDeltaEvent("hello")));

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        Assert.Equal("text_delta", root.GetProperty("type").GetString());
        Assert.Equal("hello", root.GetProperty("data").GetProperty("delta").GetString());
        Assert.False(root.TryGetProperty("Type", out _));
        Assert.False(root.GetProperty("data").TryGetProperty("Delta", out _));
    }

    [Fact]
    public async Task WriteAsync_IncludesToolArgumentsForApprovalClients()
    {
        await using var stream = new MemoryStream();

        await EventEnvelope.WriteAsync(
            stream,
            EventEnvelope.From(new ToolCallStartedEvent(
                "call-1",
                "local.execute_command",
                "{\"command\":\"dotnet\"}")));

        stream.Position = 0;
        using var document = await JsonDocument.ParseAsync(stream);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("call-1", data.GetProperty("callId").GetString());
        Assert.Equal("{\"command\":\"dotnet\"}", data.GetProperty("argumentsJson").GetString());
    }
}
