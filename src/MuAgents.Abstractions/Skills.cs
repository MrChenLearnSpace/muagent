namespace MuAgents.Abstractions;

public enum ScriptExecutionPolicy
{
    Denied,
    RequireApproval,
    Allowed
}

public sealed record SkillManifest(
    string Name,
    string Description,
    string Version,
    string Directory,
    string Instructions,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> AllowedRuntimes);

public interface ISkillCatalog
{
    Task<IReadOnlyList<SkillManifest>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<SkillManifest?> GetAsync(string name, CancellationToken cancellationToken = default);
    Task<string> ReadReferenceAsync(
        SkillManifest skill,
        string relativePath,
        CancellationToken cancellationToken = default);
}

public sealed record ScriptRunRequest(
    SkillManifest Skill,
    string ScriptPath,
    IReadOnlyList<string> Arguments,
    bool IsApproved = false);

public sealed record ScriptRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasTruncated,
    string Sha256);

public interface IScriptRunner
{
    Task<ScriptRunResult> RunAsync(
        ScriptRunRequest request,
        CancellationToken cancellationToken = default);
}
