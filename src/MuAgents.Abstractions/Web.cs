namespace MuAgents.Abstractions;

public sealed record WebSearchResult(
    string Title,
    Uri Url,
    string Snippet,
    DateTimeOffset? PublishedAt = null);

public interface IWebSearchProvider
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}

public sealed record WebContent(
    Uri Url,
    string? Title,
    string Text,
    string MediaType,
    DateTimeOffset FetchedAt,
    bool IsTruncated);

public interface IWebContentFetcher
{
    Task<WebContent> FetchAsync(Uri uri, CancellationToken cancellationToken = default);
}
