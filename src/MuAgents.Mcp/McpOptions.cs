namespace MuAgents.Mcp;

/// <summary>MCP 客户端支持的传输方式。</summary>
public enum McpTransport
{
    StreamableHttp,
    Stdio
}

/// <summary>MCP 服务集合及工具清单缓存时间。</summary>
public sealed class McpOptions
{
    /// <summary>已配置的 MCP 服务；只有 Enabled 的条目会建立连接。</summary>
    public List<McpServerProfile> Servers { get; set; } = [];
    /// <summary>服务端工具定义的缓存秒数。</summary>
    public int ToolCacheSeconds { get; set; } = 60;
}

/// <summary>一个 MCP 服务的连接信息、安全过滤规则和超时。</summary>
public sealed class McpServerProfile
{
    /// <summary>宿主内唯一的服务名，同时用于工具命名空间。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>是否启用此连接。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>使用流式 HTTP 还是本地标准输入输出进程。</summary>
    public McpTransport Transport { get; set; } = McpTransport.StreamableHttp;
    /// <summary>StreamableHttp 服务地址。</summary>
    public string? Url { get; set; }
    /// <summary>Stdio 服务启动命令。</summary>
    public string? Command { get; set; }
    /// <summary>Stdio 启动参数列表，避免经由 Shell 拼接执行。</summary>
    public List<string> Arguments { get; set; } = [];
    /// <summary>传给 Stdio 子进程的附加环境变量。</summary>
    public Dictionary<string, string> Environment { get; set; } = [];
    /// <summary>StreamableHttp 请求附加头。</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
    /// <summary>非空时只允许清单内工具。</summary>
    public List<string> AllowTools { get; set; } = [];
    /// <summary>始终拒绝的工具，优先级高于允许清单。</summary>
    public List<string> DenyTools { get; set; } = [];
    /// <summary>连接和调用的超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
