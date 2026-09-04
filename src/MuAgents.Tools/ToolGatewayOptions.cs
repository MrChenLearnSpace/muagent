namespace MuAgents.Tools;

/// <summary>工具网关的单次超时、全局并发和结果长度限制。</summary>
public sealed class ToolGatewayOptions
{
    /// <summary>单个工具调用的最长执行时间。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>同一批调用允许并行执行的工具数。</summary>
    public int MaxConcurrency { get; set; } = 4;
    /// <summary>返回模型前允许的最大字符数，超出部分会被截断。</summary>
    public int MaxResultCharacters { get; set; } = 48_000;
}
