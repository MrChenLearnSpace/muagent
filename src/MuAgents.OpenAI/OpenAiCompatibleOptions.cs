namespace MuAgents.OpenAI;

/// <summary>模型服务采用的线协议，决定请求 JSON 和流式事件解析方式。</summary>
public enum ModelProtocol
{
    ChatCompletions,
    Responses,
    Messages
}

/// <summary>OpenAI 兼容模型连接、模型能力和生成上限配置。</summary>
public sealed class OpenAiCompatibleOptions
{
    /// <summary>要使用的协议适配器。</summary>
    public ModelProtocol Protocol { get; set; } = ModelProtocol.Responses;
    /// <summary>模型服务基础地址，必须为绝对 HTTP(S) URL。</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    /// <summary>相对端点；为空时根据 Protocol 选择默认值。</summary>
    public string? Endpoint { get; set; }
    /// <summary>通过认证请求头发送的服务端密钥。</summary>
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>默认模型标识。</summary>
    public string Model { get; set; } = "gpt-5-mini";
    /// <summary>模型声明的最大上下文能力。</summary>
    public int MaxContextTokens { get; set; } = 128_000;
    /// <summary>默认最大输出 Token 数。</summary>
    public int MaxOutputTokens { get; set; } = 4_096;
    /// <summary>是否允许发送图片片段。</summary>
    public bool SupportsVision { get; set; } = true;
    /// <summary>是否允许声明和接收工具调用。</summary>
    public bool SupportsTools { get; set; } = true;
    /// <summary>单次模型 HTTP 请求超时。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>返回显式配置端点，或根据协议返回约定端点。</summary>
    public string ResolveEndpoint() => Endpoint ?? Protocol switch
    {
        ModelProtocol.ChatCompletions => "chat/completions",
        ModelProtocol.Responses => "responses",
        ModelProtocol.Messages => "messages",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol))
    };
}
