using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.OpenAI;

namespace MuAgents.UnitTests;

public sealed class ProtocolAdapterTests
{
    [Fact]
    public async Task ChatCompletions_MergesToolCallFragmentsAndUsesConfiguredKey()
    {
        const string stream = """
            data: {"choices":[{"delta":{"content":"hello "},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"test.tool","arguments":"{\"a\":"}}]},"finish_reason":null}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var (model, handler) = Model(ModelProtocol.ChatCompletions, stream);

        var events = await CollectAsync(model);

        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("configured-key", handler.Authorization?.Parameter);
        Assert.Contains(events, item => item is ModelTextDelta { Delta: "hello " });
        Assert.Contains(events, item => item is ModelToolCall
        {
            CallId: "call-1", Name: "test.tool", ArgumentsJson: "{\"a\":1}"
        });
    }

    [Fact]
    public async Task Responses_ParsesTextUsageAndFunctionCall()
    {
        const string stream = """
            data: {"type":"response.output_text.delta","delta":"answer"}

            data: {"type":"response.output_item.added","item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"test.tool","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"item-1","delta":"{}"}

            data: {"type":"response.function_call_arguments.done","item_id":"item-1","arguments":"{}"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":4,"output_tokens":2}}}

            data: [DONE]

            """;
        var (model, _) = Model(ModelProtocol.Responses, stream);

        var events = await CollectAsync(model);

        Assert.Contains(events, item => item is ModelTextDelta { Delta: "answer" });
        Assert.Contains(events, item => item is ModelToolCall { CallId: "call-1", ArgumentsJson: "{}" });
        Assert.Contains(events, item => item is ModelUsage { InputTokens: 4, OutputTokens: 2 });
    }

    [Fact]
    public async Task Responses_ReplacesTruncatedToolArgumentsWithValidRecoveryPayload()
    {
        const string stream = """
            data: {"type":"response.output_item.added","item":{"type":"function_call","id":"item-1","call_id":"call-1","name":"local.write_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"item-1","delta":"{\"path\":\"index.html\",\"content\":\"unterminated"}

            data: {"type":"response.completed","response":{"status":"completed"}}

            data: [DONE]

            """;
        var (model, _) = Model(ModelProtocol.Responses, stream);

        var call = Assert.Single((await CollectAsync(model)).OfType<ModelToolCall>());

        using var arguments = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, arguments.RootElement.ValueKind);
        Assert.Contains("truncated", arguments.RootElement.GetProperty("_muagents_error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Messages_ParsesContentBlocksAndToolInput()
    {
        const string stream = """
            data: {"type":"message_start","message":{"usage":{"input_tokens":3}}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ok"}}

            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call-1","name":"test.tool","input":{}}}

            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"x\":true}"}}

            data: {"type":"content_block_stop","index":1}

            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}

            """;
        var (model, handler) = Model(ModelProtocol.Messages, stream);

        var events = await CollectAsync(model);

        Assert.Equal("configured-key", handler.ApiKey);
        Assert.Contains(events, item => item is ModelTextDelta { Delta: "ok" });
        Assert.Contains(events, item => item is ModelToolCall
        {
            CallId: "call-1", Name: "test.tool", ArgumentsJson: "{\"x\":true}"
        });
        Assert.Contains(events, item => item is ModelUsage { InputTokens: 3, OutputTokens: 2 });
    }

    private static (OpenAiCompatibleChatModel Model, StreamHandler Handler) Model(ModelProtocol protocol, string stream)
    {
        var handler = new StreamHandler(stream);
        var client = new HttpClient(handler);
        var options = Options.Create(new OpenAiCompatibleOptions
        {
            Protocol = protocol,
            BaseUrl = "https://model.example/v1/",
            ApiKey = "configured-key",
            Model = "test"
        });
        return (new OpenAiCompatibleChatModel(client, options, NullLogger<OpenAiCompatibleChatModel>.Instance), handler);
    }

    private static async Task<List<ModelEvent>> CollectAsync(IChatModel model)
    {
        var events = new List<ModelEvent>();
        await foreach (var item in model.CompleteAsync(new AgentRequest(
                           [AgentMessage.Text(AgentRole.User, "test")], [], new ModelParameters("test"))))
            events.Add(item);
        return events;
    }

    private sealed class StreamHandler(string stream) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            ApiKey = request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null;
            var content = new StringContent(stream, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
