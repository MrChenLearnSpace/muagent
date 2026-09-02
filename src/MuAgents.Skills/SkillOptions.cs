using MuAgents.Abstractions;

namespace MuAgents.Skills;

public sealed class SkillOptions
{
    public List<string> Directories { get; set; } = ["skills"];
    public ScriptExecutionPolicy ScriptPolicy { get; set; } = ScriptExecutionPolicy.RequireApproval;
    public List<string> AllowedRuntimes { get; set; } = ["dotnet", "python", "node", "pwsh", "bash"];
    public int ScriptTimeoutSeconds { get; set; } = 60;
    public int MaxScriptOutputCharacters { get; set; } = 48_000;
    public int MaxSkillCharacters { get; set; } = 100_000;
}
