namespace MuAgents.Mcp;

public enum McpTransport
{
    StreamableHttp,
    Stdio
}

public sealed class McpOptions
{
    public List<McpServerProfile> Servers { get; set; } = [];
    public int ToolCacheSeconds { get; set; } = 60;
}

public sealed class McpServerProfile
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public McpTransport Transport { get; set; } = McpTransport.StreamableHttp;
    public string? Url { get; set; }
    public string? Command { get; set; }
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = [];
    public List<string> AllowTools { get; set; } = [];
    public List<string> DenyTools { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 60;
}
