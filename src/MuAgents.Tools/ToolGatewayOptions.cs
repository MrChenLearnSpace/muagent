namespace MuAgents.Tools;

public sealed class ToolGatewayOptions
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxConcurrency { get; set; } = 4;
    public int MaxResultCharacters { get; set; } = 48_000;
}
