using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Skills;

public sealed partial class FileSystemSkillCatalog(IOptions<SkillOptions> options) : ISkillCatalog
{
    private readonly SkillOptions _options = options.Value;

    public async Task<IReadOnlyList<SkillManifest>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var skills = new Dictionary<string, SkillManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredDirectory in _options.Directories)
        {
            var root = Path.GetFullPath(configuredDirectory);
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var skillFile = Path.Combine(directory, "SKILL.md");
                if (!File.Exists(skillFile)) continue;
                var manifest = await ParseAsync(skillFile, cancellationToken).ConfigureAwait(false);
                if (!skills.TryAdd(manifest.Name, manifest))
                    throw new MuAgentException(MuAgentErrorCategory.Configuration, $"Duplicate skill '{manifest.Name}'.");
            }
        }
        return skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();
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
        var relative = Path.GetRelativePath(normalizedRoot, path);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw Security("Skill path traversal was denied.");
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
