namespace MuAgents.Abstractions;

/// <summary>规范化后的单条 Web 搜索结果。</summary>
public sealed record WebSearchResult(
    string Title,
    Uri Url,
    string Snippet,
    DateTimeOffset? PublishedAt = null);

/// <summary>Web 搜索服务抽象，具体实现负责认证和供应商 JSON 映射。</summary>
public interface IWebSearchProvider
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}

/// <summary>完成安全抓取和正文提取后的网页内容。</summary>
public sealed record WebContent(
    Uri Url,
    string? Title,
    string Text,
    string MediaType,
    DateTimeOffset FetchedAt,
    bool IsTruncated);

/// <summary>具备 SSRF、防重定向绕过和响应大小限制的网页抓取接口。</summary>
public interface IWebContentFetcher
{
    Task<WebContent> FetchAsync(Uri uri, CancellationToken cancellationToken = default);
}
