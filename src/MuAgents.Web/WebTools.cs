using System.Text.Json;
using MuAgents.Abstractions;

namespace MuAgents.Web;

/// <summary>把经过 SSRF 防护的网页读取能力暴露给模型。</summary>
public sealed class WebFetchTool(IWebContentFetcher fetcher) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"url":{"type":"string"}},"required":["url"],"additionalProperties":false}
        """).RootElement.Clone();
    public ToolDefinition Definition { get; } = new("web.fetch", "Fetches readable text from a public HTTP or HTTPS URL.", Schema);

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("url", out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
            return new ToolResult("url must be an absolute HTTP or HTTPS URL.", true);
        var content = await fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false);
        return new ToolResult(JsonSerializer.Serialize(new
        {
            url = content.Url.ToString(),
            content.Title,
            content.Text,
            content.FetchedAt,
            content.IsTruncated
        }));
    }
}

/// <summary>把配置的 Web 搜索服务暴露为模型工具。</summary>
public sealed class WebSearchTool(IWebSearchProvider search) : IAgentTool
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer","minimum":1,"maximum":20}},"required":["query"],"additionalProperties":false}
        """).RootElement.Clone();
    public ToolDefinition Definition { get; } = new("web.search", "Searches the web for current information and returns source URLs.", Schema);

    public async Task<ToolResult> InvokeAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("query", out var value) || string.IsNullOrWhiteSpace(value.GetString()))
            return new ToolResult("query is required.", true);
        var count = arguments.TryGetProperty("count", out var countValue) && countValue.TryGetInt32(out var parsed) ? parsed : 5;
        var results = await search.SearchAsync(value.GetString()!, count, cancellationToken).ConfigureAwait(false);
        return new ToolResult(JsonSerializer.Serialize(results));
    }
}
