using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Web;

public sealed class JsonWebSearchProvider(
    HttpClient httpClient,
    IOptions<WebOptions> options) : IWebSearchProvider
{
    private readonly WebOptions _options = options.Value;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (!_options.AgentMaySearch)
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "Web search is disabled.");
        if (string.IsNullOrWhiteSpace(_options.SearchEndpoint))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "Web search endpoint is not configured.");
        var endpoint = _options.SearchEndpoint
            .Replace("{query}", Uri.EscapeDataString(query), StringComparison.Ordinal)
            .Replace("{count}", Math.Clamp(count, 1, 20).ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        if (!string.IsNullOrEmpty(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeader, _options.ApiKey);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new MuAgentException(MuAgentErrorCategory.InvalidModelResponse, "Search returned no JSON.");
        var items = LocateItems(document.RootElement);
        var results = new List<WebSearchResult>();
        foreach (var item in items.EnumerateArray().Take(Math.Clamp(count, 1, 20)))
        {
            var url = String(item, "url") ?? String(item, "link");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            results.Add(new WebSearchResult(
                String(item, "name") ?? String(item, "title") ?? uri.Host,
                uri,
                String(item, "snippet") ?? String(item, "description") ?? string.Empty));
        }
        return results;
    }

    private static JsonElement LocateItems(JsonElement root)
    {
        if (root.TryGetProperty("webPages", out var pages) && pages.TryGetProperty("value", out var value)) return value;
        if (root.TryGetProperty("results", out var results)) return results;
        if (root.TryGetProperty("items", out var items)) return items;
        throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Search JSON has no supported result array.");
    }

    private static string? String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
}
