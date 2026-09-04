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
}
