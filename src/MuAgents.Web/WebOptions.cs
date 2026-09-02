namespace MuAgents.Web;

public sealed class WebOptions
{
    public bool AgentMaySearch { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 20;
    public int MaxRedirects { get; set; } = 5;
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    public int MaxExtractedCharacters { get; set; } = 100_000;
    public string? SearchEndpoint { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyHeader { get; set; } = "X-API-Key";
}
