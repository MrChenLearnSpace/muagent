namespace MuAgents.Web;

/// <summary>Web 搜索和安全抓取的能力开关、地址、凭据及资源上限。</summary>
public sealed class WebOptions
{
    /// <summary>是否向模型注册搜索工具。</summary>
    public bool AgentMaySearch { get; set; } = true;
    /// <summary>一次网络请求的超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>手动跟随重定向的最大次数，每一跳都会重新执行安全检查。</summary>
    public int MaxRedirects { get; set; } = 5;
    /// <summary>允许下载的最大响应字节数。</summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
    /// <summary>从 HTML 提取后返回的最大字符数。</summary>
    public int MaxExtractedCharacters { get; set; } = 100_000;
    /// <summary>包含 {query} 和可选 {count} 占位符的搜索 API 地址。</summary>
    public string? SearchEndpoint { get; set; }
    /// <summary>搜索服务 API Key。</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>承载搜索 API Key 的请求头名称。</summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";
}
