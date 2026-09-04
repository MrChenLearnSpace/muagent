using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Skills;

/// <summary>可动态修改并保存到磁盘的 Skill 目录与禁用清单。</summary>
public sealed class SkillRuntimeConfiguration
{
    public List<string> Directories { get; set; } = ["skills"];
    public List<string> DisabledSkills { get; set; } = [];
}

/// <summary>把 Skill 启停和目录配置持久化到项目的 .muagent/config/skills.json。</summary>
public sealed class SkillConfigurationStore
{
    public const string RelativeConfigurationPath = "config/skills.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private SkillRuntimeConfiguration _current;

    public SkillConfigurationStore(IOptions<SkillOptions> defaults)
        : this(defaults, null)
    {
    }

    public SkillConfigurationStore(IOptions<SkillOptions> defaults, string? configurationPath)
    {
        ConfigurationPath = RuntimePaths.ResolveWritePath(
            configurationPath ?? RelativeConfigurationPath,
            "Skill configuration path");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        if (File.Exists(ConfigurationPath))
        {
            _current = JsonSerializer.Deserialize<SkillRuntimeConfiguration>(File.ReadAllText(ConfigurationPath), JsonOptions)
                ?? throw new MuAgentException(MuAgentErrorCategory.Configuration, "Skill configuration file is empty.");
        }
        else
        {
            _current = new SkillRuntimeConfiguration { Directories = [.. defaults.Value.Directories] };
            NormalizeListsUnsafe();
            SaveUnsafe();
        }

        // IConfiguration 对数组采用追加式绑定，默认值与 appsettings 中相同的 "skills"
        // 可能同时出现。这里统一去重，也顺便修复旧版本已经写出的重复配置。
        lock (_gate)
        {
            if (NormalizeListsUnsafe()) SaveUnsafe();
        }
    }

    public string ConfigurationPath { get; }

    public SkillRuntimeConfiguration Snapshot()
    {
        lock (_gate)
        {
            return new SkillRuntimeConfiguration
            {
                Directories = [.. _current.Directories],
                DisabledSkills = [.. _current.DisabledSkills]
            };
        }
    }

    public string AddDirectory(string path)
    {
        var stored = NormalizeDirectory(path, requireExists: true);
        lock (_gate)
        {
            if (!_current.Directories.Any(item => SameDirectory(item, stored)))
            {
                _current.Directories.Add(stored);
                SaveUnsafe();
            }
        }
        return stored;
    }

    public bool RemoveDirectory(string path)
    {
        var normalized = NormalizeDirectory(path, requireExists: false);
        lock (_gate)
        {
            var removed = _current.Directories.RemoveAll(item => SameDirectory(item, normalized)) > 0;
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    public void SetEnabled(string skillName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(skillName)) throw new ArgumentException("Skill name is required.", nameof(skillName));
        lock (_gate)
        {
            _current.DisabledSkills.RemoveAll(item => item.Equals(skillName, StringComparison.OrdinalIgnoreCase));
            if (!enabled) _current.DisabledSkills.Add(skillName);
            SaveUnsafe();
        }
    }

    public bool IsEnabled(string skillName)
    {
        lock (_gate)
            return !_current.DisabledSkills.Contains(skillName, StringComparer.OrdinalIgnoreCase);
    }

    private string NormalizeDirectory(string path, bool requireExists)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Skill directory is required.", nameof(path));
        var fullPath = Path.GetFullPath(path.Trim().Trim('"'), RuntimePaths.ProjectDirectory);
        if (requireExists && !Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        var relative = Path.GetRelativePath(RuntimePaths.ProjectDirectory, fullPath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            ? relative
            : fullPath;
    }

    private static bool SameDirectory(string left, string right) =>
        Path.GetFullPath(left, RuntimePaths.ProjectDirectory)
            .Equals(Path.GetFullPath(right, RuntimePaths.ProjectDirectory), PathComparison);

    private void SaveUnsafe()
    {
        var temporaryPath = ConfigurationPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_current, JsonOptions));
        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
    }

    private bool NormalizeListsUnsafe()
    {
        var directories = _current.Directories
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(PathStringComparer)
            .ToList();
        var disabledSkills = _current.DisabledSkills
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var changed = directories.Count != _current.Directories.Count ||
                      disabledSkills.Count != _current.DisabledSkills.Count;
        _current.Directories = directories;
        _current.DisabledSkills = disabledSkills;
        return changed;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathStringComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
