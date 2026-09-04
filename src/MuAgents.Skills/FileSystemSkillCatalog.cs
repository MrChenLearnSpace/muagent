using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Skills;

/// <summary>Skill 清单及其当前启用状态。</summary>
public sealed record SkillCatalogEntry(SkillManifest Manifest, bool Enabled);

/// <summary>从受控目录发现和解析 SKILL.md，并安全读取 Skill 引用文件。</summary>
public sealed partial class FileSystemSkillCatalog(
    IOptions<SkillOptions> options,
    SkillConfigurationStore configuration) : ISkillCatalog
{
    private readonly SkillOptions _options = options.Value;

    public async Task<IReadOnlyList<SkillManifest>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        (await DiscoverAllAsync(cancellationToken).ConfigureAwait(false))
        .Where(entry => entry.Enabled)
        .Select(entry => entry.Manifest)
        .ToArray();

    public async Task<IReadOnlyList<SkillCatalogEntry>> DiscoverAllAsync(CancellationToken cancellationToken = default)
    {
        var skills = new Dictionary<string, SkillManifest>(StringComparer.OrdinalIgnoreCase);
        // Skill 是项目内容：相对目录基于启动时的项目根目录，启停配置则另存于 .muagent。
        var settings = configuration.Snapshot();
        foreach (var configuredDirectory in settings.Directories)
        {
            var root = Path.GetFullPath(configuredDirectory, RuntimePaths.ProjectDirectory);
            if (!Directory.Exists(root)) continue;
            var directories = File.Exists(Path.Combine(root, "SKILL.md"))
                ? new[] { root }
                : Directory.EnumerateDirectories(root);
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var skillFile = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var manifest = await ParseAsync(skillFile, cancellationToken).ConfigureAwait(false);
                if (!skills.TryAdd(manifest.Name, manifest))
                    throw new MuAgentException(MuAgentErrorCategory.Configuration, $"Duplicate skill '{manifest.Name}'.");
            }
        }
        return skills.Values
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => new SkillCatalogEntry(
                skill,
                !settings.DisabledSkills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<SkillManifest?> GetAsync(string name, CancellationToken cancellationToken = default) =>
        (await DiscoverAsync(cancellationToken).ConfigureAwait(false))
        .FirstOrDefault(skill => skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public async Task<string> ReadReferenceAsync(
        SkillManifest skill,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveWithin(skill.Directory, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Skill reference was not found.", path);
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return text.Length <= _options.MaxSkillCharacters ? text : text[.._options.MaxSkillCharacters];
    }

    public static string ResolveWithin(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw Security("Skill path must be relative.");
        var normalizedRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        // 先规范化再计算相对路径，阻断 ..、绝对路径和相似目录名前缀绕过。
        var relative = Path.GetRelativePath(normalizedRoot, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw Security("Skill path traversal was denied.");
        // 路径字符串位于目录内仍不够，符号链接的最终目标也必须留在 Skill 根目录。
        var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is not null)
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                var targetRelative = Path.GetRelativePath(normalizedRoot, target.FullName);
                if (targetRelative == ".." || targetRelative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    throw Security("Skill symbolic link leaves its directory.");
            }
        }
        return path;
    }

    private async Task<SkillManifest> ParseAsync(string skillFile, CancellationToken cancellationToken)
    {
        var source = await File.ReadAllTextAsync(skillFile, cancellationToken).ConfigureAwait(false);
        if (source.Length > _options.MaxSkillCharacters)
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, $"Skill file '{skillFile}' is too large.");
        var normalized = source.ReplaceLineEndings("\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, $"Skill '{skillFile}' has no YAML front matter.");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) throw new MuAgentException(MuAgentErrorCategory.ContentFailure, $"Skill '{skillFile}' has invalid front matter.");
        var metadata = ParseMetadata(normalized[4..end]);
        var name = Value("name");
        if (!NameRegex().IsMatch(name))
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, $"Skill name '{name}' is invalid.");
        return new SkillManifest(
            name,
            Value("description"),
            metadata.GetValueOrDefault("version", "1.0.0"),
            Path.GetDirectoryName(Path.GetFullPath(skillFile))!,
            normalized[(end + 5)..],
            List("required-tools", "tools"),
            List("allowed-runtimes", "runtimes"));

        string Value(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new MuAgentException(MuAgentErrorCategory.ContentFailure, $"Skill '{skillFile}' is missing '{key}'.");
        string[] List(params string[] keys)
        {
            var value = keys.Select(key => metadata.GetValueOrDefault(key)).FirstOrDefault(item => item is not null);
            return value is null ? [] : value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(item => item.Trim('"', '\'')).ToArray();
        }
    }

    private static Dictionary<string, string> ParseMetadata(string yaml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in yaml.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var separator = line.IndexOf(':');
            if (separator > 0) result[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }
        return result;
    }

    private static MuAgentException Security(string message) => new(MuAgentErrorCategory.SecurityDenied, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.IgnoreCase)]
    private static partial Regex NameRegex();
}
