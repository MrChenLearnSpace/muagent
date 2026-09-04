using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.OpenAI;

/// <summary>在统一消息/事件模型与三类 OpenAI 兼容 HTTP 协议之间执行双向转换。</summary>
public sealed class OpenAiCompatibleChatModel(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleOptions> options,
    ILogger<OpenAiCompatibleChatModel> logger) : IChatModel
{
    private readonly OpenAiCompatibleOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<ModelEvent> CompleteAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        // 三种协议只在序列化和事件解释层分叉，AgentRuntime 始终消费同一种 ModelEvent。
        var completion = _options.Protocol switch
        {
            ModelProtocol.ChatCompletions => CompleteChatAsync(request, cancellationToken),
            ModelProtocol.Responses => CompleteResponsesAsync(request, cancellationToken),
            ModelProtocol.Messages => CompleteMessagesAsync(request, cancellationToken),
            _ => throw new MuAgentException(MuAgentErrorCategory.Configuration, "Unknown model protocol.")
        };
        var modelTag = new KeyValuePair<string, object?>("model", request.Parameters.Model);
        var protocolTag = new KeyValuePair<string, object?>("protocol", _options.Protocol.ToString());
        var startedAt = Stopwatch.GetTimestamp();
        MuAgentsTelemetry.ModelRequests.Add(1, modelTag, protocolTag);
        using var activity = MuAgentsTelemetry.Activities.StartActivity("model.complete", ActivityKind.Client);
        activity?.SetTag("gen_ai.request.model", request.Parameters.Model);
        activity?.SetTag("muagents.model.protocol", _options.Protocol.ToString());
        var completed = false;
        var failed = false;
        var firstEventObserved = false;
        try
        {
            await using var enumerator = completion.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch
                {
                    failed = !cancellationToken.IsCancellationRequested;
                    throw;
                }
                if (!hasNext) break;

                // 首事件耗时比完整请求耗时更能反映流式交互的用户体感。
                if (!firstEventObserved)
                {
                    firstEventObserved = true;
                    MuAgentsTelemetry.ModelFirstEventDuration.Record(
                        Stopwatch.GetElapsedTime(startedAt).TotalSeconds, modelTag, protocolTag);
                    activity?.AddEvent(new ActivityEvent("model.first_event"));
                }
                if (enumerator.Current is ModelUsage usage)
                {
                    MuAgentsTelemetry.ModelInputTokens.Add(usage.InputTokens, modelTag, protocolTag);
                    MuAgentsTelemetry.ModelOutputTokens.Add(usage.OutputTokens, modelTag, protocolTag);
                }
                yield return enumerator.Current;
            }
            completed = true;
        }
        finally
        {
            var outcome = completed
                ? "success"
                : cancellationToken.IsCancellationRequested ? "cancelled" : failed ? "error" : "abandoned";
            activity?.SetTag("muagents.outcome", outcome);
            if (failed)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                MuAgentsTelemetry.ModelFailures.Add(1, modelTag, protocolTag);
            }
            MuAgentsTelemetry.ModelDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
                modelTag,
                protocolTag,
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async IAsyncEnumerable<ModelEvent> CompleteChatAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Parameters.Model,
            ["messages"] = BuildChatMessages(request),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["max_tokens"] = request.Parameters.MaxOutputTokens,
            ["temperature"] = request.Parameters.Temperature,
            ["tools"] = request.Tools.Count == 0 ? null : BuildChatTools(request.Tools)
        };
        // Chat Completions 会把一次工具调用的名称和参数拆到多个 delta，按 index 聚合后再上报。
        var calls = new Dictionary<int, CallAccumulator>();
        string? finishReason = null;

        await foreach (var data in PostSseAsync(body, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                break;
            }

            using var document = ParseEvent(data);
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                yield return new ModelUsage(GetInt(usage, "prompt_tokens"), GetInt(usage, "completion_tokens"));
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
            }

            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                yield return new ModelTextDelta(content.GetString()!);
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            {
                yield return new ModelReasoningDelta(reasoning.GetString()!);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls))
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    var index = GetInt(toolCall, "index");
                    if (!calls.TryGetValue(index, out var accumulator))
                    {
                        accumulator = calls[index] = new CallAccumulator();
                    }

                    accumulator.AppendChat(toolCall);
                }
            }
        }

        foreach (var call in calls.OrderBy(x => x.Key).Select(x => x.Value))
        {
            yield return call.ToEvent();
        }

        yield return new ModelCompleted(finishReason);
    }

    private async IAsyncEnumerable<ModelEvent> CompleteResponsesAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Parameters.Model,
            ["input"] = BuildResponsesInput(request),
            ["instructions"] = request.Parameters.SystemInstruction,
            ["stream"] = true,
            ["max_output_tokens"] = request.Parameters.MaxOutputTokens,
            ["temperature"] = request.Parameters.Temperature,
            ["tools"] = request.Tools.Count == 0 ? null : BuildResponsesTools(request.Tools)
        };
        // Responses 使用 item_id/call_id 关联参数碎片，字典负责跨 SSE 事件恢复完整调用。
        var calls = new Dictionary<string, CallAccumulator>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        string? finishReason = null;

        await foreach (var data in PostSseAsync(body, cancellationToken).ConfigureAwait(false))
        {
            if (data == "[DONE]")
            {
                break;
            }

            using var document = ParseEvent(data);
            var root = document.RootElement;
            var type = GetString(root, "type") ?? string.Empty;
            switch (type)
            {
                case "response.output_text.delta":
                    yield return new ModelTextDelta(GetString(root, "delta") ?? string.Empty);
                    break;
                case "response.reasoning_summary_text.delta":
                    yield return new ModelReasoningDelta(GetString(root, "delta") ?? string.Empty);
                    break;
                case "response.output_item.added":
                    {
                        if (root.TryGetProperty("item", out var item) && GetString(item, "type") == "function_call")
                        {
                            var key = GetString(item, "id") ?? GetString(item, "call_id") ?? Guid.NewGuid().ToString("N");
                            var call = calls[key] = new CallAccumulator();
                            call.Id.Append(GetString(item, "call_id") ?? GetString(item, "id"));
                            call.Name.Append(GetString(item, "name"));
                            call.Arguments.Append(GetString(item, "arguments"));
                        }
                        break;
                    }
                case "response.function_call_arguments.delta":
                    {
                        var key = GetString(root, "item_id") ?? GetString(root, "call_id") ?? string.Empty;
                        if (!calls.TryGetValue(key, out var call))
                        {
                            call = calls[key] = new CallAccumulator();
                            call.Id.Append(GetString(root, "call_id") ?? key);
                            call.Name.Append(GetString(root, "name"));
                        }
                        call.Arguments.Append(GetString(root, "delta"));
                        break;
                    }
                case "response.function_call_arguments.done":
                    {
                        var key = GetString(root, "item_id") ?? GetString(root, "call_id") ?? string.Empty;
                        if (!calls.TryGetValue(key, out var call))
                        {
                            call = calls[key] = new CallAccumulator();
                        }
                        if (call.Arguments.Length == 0)
                        {
                            call.Arguments.Append(GetString(root, "arguments"));
                        }
                        if (call.Id.Length == 0)
                        {
                            call.Id.Append(GetString(root, "call_id") ?? key);
                        }
                        if (call.Name.Length == 0)
                        {
                            call.Name.Append(GetString(root, "name"));
                        }
                        if (emitted.Add(key))
                        {
                            yield return call.ToEvent();
                        }
                        break;
                    }
                case "response.completed":
                    if (root.TryGetProperty("response", out var response))
                    {
                        finishReason = GetString(response, "status");
                        if (response.TryGetProperty("usage", out var usage))
                        {
                            yield return new ModelUsage(GetInt(usage, "input_tokens"), GetInt(usage, "output_tokens"));
                        }
                    }
                    break;
                case "error":
                    throw InvalidResponse(root);
            }
        }

        foreach (var pair in calls.Where(pair => emitted.Add(pair.Key)))
        {
            yield return pair.Value.ToEvent();
        }
        yield return new ModelCompleted(finishReason);
    }

    private async IAsyncEnumerable<ModelEvent> CompleteMessagesAsync(
        AgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Parameters.Model,
            ["messages"] = BuildMessagesProtocolMessages(request.Messages),
            ["system"] = BuildMessagesSystem(request),
            ["stream"] = true,
            ["max_tokens"] = request.Parameters.MaxOutputTokens,
            ["temperature"] = request.Parameters.Temperature,
            ["tools"] = request.Tools.Count == 0 ? null : BuildMessagesTools(request.Tools)
        };
        var calls = new Dictionary<int, CallAccumulator>();
        var inputTokens = 0;
        var outputTokens = 0;
        string? finishReason = null;

        await foreach (var data in PostSseAsync(body, cancellationToken).ConfigureAwait(false))
        {
            using var document = ParseEvent(data);
            var root = document.RootElement;
            switch (GetString(root, "type"))
            {
                case "content_block_start":
                    {
                        var index = GetInt(root, "index");
                        if (root.TryGetProperty("content_block", out var block) && GetString(block, "type") == "tool_use")
                        {
                            var call = calls[index] = new CallAccumulator();
                            call.Id.Append(GetString(block, "id"));
                            call.Name.Append(GetString(block, "name"));
                            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object && input.GetRawText() != "{}")
                            {
                                call.Arguments.Append(input.GetRawText());
                            }
                        }
                        break;
                    }
                case "content_block_delta":
                    {
                        var index = GetInt(root, "index");
                        if (!root.TryGetProperty("delta", out var delta)) break;
                        switch (GetString(delta, "type"))
                        {
                            case "text_delta":
                                yield return new ModelTextDelta(GetString(delta, "text") ?? string.Empty);
                                break;
                            case "thinking_delta":
                                yield return new ModelReasoningDelta(GetString(delta, "thinking") ?? string.Empty);
                                break;
                            case "input_json_delta" when calls.TryGetValue(index, out var call):
                                call.Arguments.Append(GetString(delta, "partial_json"));
                                break;
                        }
                        break;
                    }
                case "content_block_stop":
                    {
                        var index = GetInt(root, "index");
                        if (calls.Remove(index, out var call))
                        {
                            yield return call.ToEvent();
                        }
                        break;
                    }
                case "message_start":
                    if (root.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var startUsage))
                    {
                        inputTokens = GetInt(startUsage, "input_tokens");
                    }
                    break;
                case "message_delta":
                    if (root.TryGetProperty("delta", out var messageDelta))
                    {
                        finishReason = GetString(messageDelta, "stop_reason");
                    }
                    if (root.TryGetProperty("usage", out var endUsage))
                    {
                        outputTokens = GetInt(endUsage, "output_tokens");
                    }
                    break;
                case "error":
                    throw InvalidResponse(root);
            }
        }

        foreach (var call in calls.OrderBy(x => x.Key).Select(x => x.Value))
        {
            yield return call.ToEvent();
        }
        if (inputTokens > 0 || outputTokens > 0)
        {
            yield return new ModelUsage(inputTokens, outputTokens);
        }
        yield return new ModelCompleted(finishReason);
    }

    private async IAsyncEnumerable<string> PostSseAsync(
        object body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureTrailingSlash(_options.BaseUrl)), _options.ResolveEndpoint()))
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var apiKey = _options.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (_options.Protocol == ModelProtocol.Messages)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }

        // ResponseHeadersRead 防止 HttpClient 等待完整响应缓冲，收到 SSE 后即可逐行产出事件。
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (detail.Length > 2_000) detail = detail[..2_000];
            logger.LogWarning("Model endpoint returned {StatusCode}", (int)response.StatusCode);
            throw new MuAgentException(
                response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? MuAgentErrorCategory.RateLimit : MuAgentErrorCategory.InvalidModelResponse,
                $"Model endpoint returned HTTP {(int)response.StatusCode}: {detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            // SSE 允许 event/id 等字段；模型负载只取 data 行，并由各协议解析器解释 JSON。
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                yield return line[5..].TrimStart();
            }
        }
    }

    private static IReadOnlyList<object> BuildChatMessages(AgentRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Parameters.SystemInstruction))
        {
            messages.Add(new { role = "system", content = request.Parameters.SystemInstruction });
        }
        foreach (var message in request.Messages)
        {
            if (message.Role == AgentRole.Tool)
            {
                messages.AddRange(message.Parts.OfType<ToolResultPart>().Select(result =>
                    (object)new { role = "tool", tool_call_id = result.CallId, content = result.Content }));
                continue;
            }

            var text = string.Concat(message.Parts.OfType<TextPart>().Select(x => x.Text));
            var images = message.Parts.OfType<ImagePart>().ToArray();
            object content = images.Length == 0
                ? text
                : message.Parts.SelectMany(ToChatContent).ToArray();
            var toolCalls = message.Parts.OfType<ToolCallPart>()
                .Select(call => new { id = call.CallId, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } })
                .ToArray();
            messages.Add(new
            {
                role = Role(message.Role),
                content,
                tool_calls = toolCalls.Length == 0 ? null : toolCalls
            });
        }
        return messages;
    }

    private static IEnumerable<object> ToChatContent(MessagePart part) => part switch
    {
        TextPart text => [new { type = "text", text = text.Text }],
        ImagePart image => [new { type = "image_url", image_url = new { url = image.Source.Value } }],
        _ => []
    };

    private static IReadOnlyList<object> BuildResponsesInput(AgentRequest request)
    {
        var input = new List<object>();
        foreach (var message in request.Messages)
        {
            foreach (var result in message.Parts.OfType<ToolResultPart>())
            {
                input.Add(new { type = "function_call_output", call_id = result.CallId, output = result.Content });
            }
            foreach (var call in message.Parts.OfType<ToolCallPart>())
            {
                input.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = call.ArgumentsJson });
            }
            var content = message.Parts.SelectMany(part => part switch
            {
                TextPart text => new object[] { new { type = message.Role == AgentRole.Assistant ? "output_text" : "input_text", text = text.Text } },
                ImagePart image => new object[] { new { type = "input_image", image_url = image.Source.Value } },
                _ => []
            }).ToArray();
            if (content.Length > 0)
            {
                input.Add(new { type = "message", role = Role(message.Role), content });
            }
        }
        return input;
    }

    private static IReadOnlyList<object> BuildMessagesProtocolMessages(IReadOnlyList<AgentMessage> source)
    {
        var messages = new List<object>();
        foreach (var message in source.Where(x => x.Role != AgentRole.System))
        {
            var content = new List<object>();
            content.AddRange(message.Parts.SelectMany(part => part switch
            {
                TextPart text => new object[] { new { type = "text", text = text.Text } },
                ImagePart image => new object[] { new { type = "image", source = MessageImageSource(image) } },
                ToolCallPart call => new object[] { new { type = "tool_use", id = call.CallId, name = call.Name, input = ParseArguments(call.ArgumentsJson) } },
                ToolResultPart result => new object[] { new { type = "tool_result", tool_use_id = result.CallId, content = result.Content, is_error = result.IsError } },
                _ => []
            }));
            messages.Add(new { role = message.Role == AgentRole.Assistant ? "assistant" : "user", content });
        }
        return messages;
    }

    private static string? BuildMessagesSystem(AgentRequest request)
    {
        var systemMessages = request.Messages.Where(x => x.Role == AgentRole.System)
            .SelectMany(x => x.Parts.OfType<TextPart>()).Select(x => x.Text);
        return string.Join("\n\n", new[] { request.Parameters.SystemInstruction }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Concat(systemMessages));
    }

    private static object MessageImageSource(ImagePart image)
    {
        if (image.Source.Kind == ImageSourceKind.DataUrl)
        {
            var comma = image.Source.Value.IndexOf(',');
            return new { type = "base64", media_type = image.MediaType ?? "image/jpeg", data = comma >= 0 ? image.Source.Value[(comma + 1)..] : image.Source.Value };
        }
        return new { type = "url", url = image.Source.Value };
    }

    private static object[] BuildChatTools(IReadOnlyList<ToolDefinition> tools) => tools.Select(tool =>
        (object)new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.ParametersSchema } }).ToArray();

    private static object[] BuildResponsesTools(IReadOnlyList<ToolDefinition> tools) => tools.Select(tool =>
        (object)new { type = "function", name = tool.Name, description = tool.Description, parameters = tool.ParametersSchema }).ToArray();

    private static object[] BuildMessagesTools(IReadOnlyList<ToolDefinition> tools) => tools.Select(tool =>
        (object)new { name = tool.Name, description = tool.Description, input_schema = tool.ParametersSchema }).ToArray();

    private static JsonElement ParseArguments(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static string Role(AgentRole role) => role switch
    {
        AgentRole.System => "system",
        AgentRole.User => "user",
        AgentRole.Assistant => "assistant",
        AgentRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role))
    };

    private void ValidateRequest(AgentRequest request)
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "Model BaseUrl must be an absolute HTTP(S) URL.");
        }
        if (!_options.SupportsVision && request.Messages.SelectMany(x => x.Parts).Any(x => x is ImagePart))
        {
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "The configured model does not support image input.");
        }
        if (!_options.SupportsTools && request.Tools.Count > 0)
        {
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "The configured model does not support tools.");
        }
    }

    private static JsonDocument ParseEvent(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch (JsonException exception)
        {
            throw new MuAgentException(MuAgentErrorCategory.InvalidModelResponse, "The model returned malformed stream JSON.", exception);
        }
    }

    private static MuAgentException InvalidResponse(JsonElement error) =>
        new(MuAgentErrorCategory.InvalidModelResponse, $"Model stream error: {error.GetRawText()}");

    private static int GetInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.TryGetInt32(out var number) ? number : 0;

    private static string? GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static string EnsureTrailingSlash(string value) => value.EndsWith('/') ? value : value + "/";

    private sealed class CallAccumulator
    {
        public StringBuilder Id { get; } = new();
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();

        public void AppendChat(JsonElement toolCall)
        {
            Id.Append(GetString(toolCall, "id"));
            if (toolCall.TryGetProperty("function", out var function))
            {
                Name.Append(GetString(function, "name"));
                Arguments.Append(GetString(function, "arguments"));
            }
        }

        public ModelToolCall ToEvent() => new(
            Id.Length == 0 ? Guid.NewGuid().ToString("N") : Id.ToString(),
            Name.ToString(),
            NormalizeArguments(Arguments.ToString()));

        private static string NormalizeArguments(string arguments)
        {
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                try
                {
                    using var document = JsonDocument.Parse(arguments);
                    if (document.RootElement.ValueKind == JsonValueKind.Object) return arguments;
                }
                catch (JsonException)
                {
                    // 工具参数常在模型达到输出 Token 上限时被截断。不能把残缺 JSON 写进会话，
                    // 否则下一次 Responses 请求会在供应商解析历史时直接返回 500。
                }
            }

            return JsonSerializer.Serialize(new
            {
                _muagents_error = "Tool arguments were empty, truncated, or invalid JSON. Retry with smaller arguments; split large files into separate HTML, CSS, and JavaScript files."
            });
        }
    }
}
