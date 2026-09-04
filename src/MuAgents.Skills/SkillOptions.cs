using MuAgents.Abstractions;

namespace MuAgents.Skills;

/// <summary>Skill 发现目录、脚本策略、运行时白名单和内容上限。</summary>
public sealed class SkillOptions
{
    /// <summary>Skill 根目录；相对路径以启动时的项目根目录为基准。</summary>
    public List<string> Directories { get; set; } = ["skills"];
    /// <summary>全局脚本执行策略。</summary>
    public ScriptExecutionPolicy ScriptPolicy { get; set; } = ScriptExecutionPolicy.RequireApproval;
    /// <summary>允许启动的解释器或运行时名称。</summary>
    public List<string> AllowedRuntimes { get; set; } = ["dotnet", "python", "node", "pwsh", "bash"];
    /// <summary>脚本执行超时秒数。</summary>
    public int ScriptTimeoutSeconds { get; set; } = 60;
    /// <summary>标准输出与错误输出合计的字符预算。</summary>
    public int MaxScriptOutputCharacters { get; set; } = 48_000;
    /// <summary>单个 SKILL.md 允许读取的最大字符数。</summary>
    public int MaxSkillCharacters { get; set; } = 100_000;
}
