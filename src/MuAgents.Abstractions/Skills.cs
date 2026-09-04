namespace MuAgents.Abstractions;

/// <summary>Skill 脚本执行策略，宿主可完全禁止、逐次批准或直接允许。</summary>
public enum ScriptExecutionPolicy
{
    Denied,
    RequireApproval,
    Allowed
}

/// <summary>从 SKILL.md 解析出的只读清单与指令。</summary>
public sealed record SkillManifest(
    string Name,
    string Description,
    string Version,
    string Directory,
    string Instructions,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> AllowedRuntimes);

/// <summary>发现 Skill、按名称读取 Skill 以及安全读取其引用文件的目录接口。</summary>
public interface ISkillCatalog
{
    Task<IReadOnlyList<SkillManifest>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<SkillManifest?> GetAsync(string name, CancellationToken cancellationToken = default);
    Task<string> ReadReferenceAsync(
        SkillManifest skill,
        string relativePath,
        CancellationToken cancellationToken = default);
}

/// <summary>一次 Skill 脚本执行请求；IsApproved 仅对 RequireApproval 策略有效。</summary>
public sealed record ScriptRunRequest(
    SkillManifest Skill,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    bool IsApproved = false);

/// <summary>脚本退出状态、受限输出和脚本内容哈希，便于审计实际执行版本。</summary>
public sealed record ScriptRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasTruncated,
    string Sha256);

/// <summary>受策略约束的 Skill 脚本执行器。</summary>
public interface IScriptRunner
{
    Task<ScriptRunResult> RunAsync(
        ScriptRunRequest request,
        CancellationToken cancellationToken = default);
}
