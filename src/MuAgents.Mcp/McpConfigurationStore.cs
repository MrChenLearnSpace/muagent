using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Mcp;

/// <summary>把可动态修改的 MCP 服务配置持久化到程序根目录的 config/mcp.json。</summary>
public sealed class McpConfigurationStore
{
    public const string RelativeConfigurationPath = "config/mcp.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();
    private McpOptions _current;

    public McpConfigurationStore(IOptions<McpOptions> defaults)
        : this(defaults, null)
    {
    }

    /// <summary>测试可传入根目录内的独立配置路径；生产环境始终使用默认位置。</summary>
    public McpConfigurationStore(IOptions<McpOptions> defaults, string? configurationPath)
    {
        ConfigurationPath = RuntimePaths.ResolveWritePath(
            configurationPath ?? RelativeConfigurationPath,
            "MCP configuration path");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
        if (File.Exists(ConfigurationPath))
        {
            _current = JsonSerializer.Deserialize<McpOptions>(File.ReadAllText(ConfigurationPath), JsonOptions)
                ?? throw new MuAgentException(MuAgentErrorCategory.Configuration, "MCP configuration file is empty.");
        }
        else
        {
            _current = Clone(defaults.Value);
            Validate(_current);
            SaveUnsafe();
        }
        Validate(_current);
    }

    /// <summary>MCP 配置文件的规范化绝对路径。</summary>
    public string ConfigurationPath { get; }

    public McpOptions Snapshot()
    {
        lock (_gate) return Clone(_current);
    }

    public McpServerProfile Upsert(McpServerProfile profile)
    {
        ValidateProfile(profile);
        lock (_gate)
        {
            var copy = Clone(profile);
            var index = _current.Servers.FindIndex(item => item.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _current.Servers[index] = copy;
            else _current.Servers.Add(copy);
            SaveUnsafe();
            return Clone(copy);
        }
    }

    public bool SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            var profile = _current.Servers.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return false;
            profile.Enabled = enabled;
            SaveUnsafe();
            return true;
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var removed = _current.Servers.RemoveAll(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) SaveUnsafe();
            return removed;
        }
    }

    private void SaveUnsafe()
    {
        var temporaryPath = ConfigurationPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_current, JsonOptions));
        File.Move(temporaryPath, ConfigurationPath, overwrite: true);
    }

    private static void Validate(McpOptions options)
    {
        if (options.ToolCacheSeconds < 0)
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "MCP tool cache seconds cannot be negative.");
        foreach (var profile in options.Servers) ValidateProfile(profile);
        if (options.Servers.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Servers.Count)
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "MCP server names must be unique.");
    }

    private static void ValidateProfile(McpServerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64 ||
            profile.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "MCP server name may contain only letters, digits, '_', '-' and '.'.");
        if (profile.TimeoutSeconds is < 1 or > 3600)
            throw new MuAgentException(MuAgentErrorCategory.Configuration, "MCP timeout must be between 1 and 3600 seconds.");
        if (profile.Transport == McpTransport.StreamableHttp &&
            (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, $"MCP server '{profile.Name}' requires an absolute HTTP(S) URL.");
        if (profile.Transport == McpTransport.Stdio && string.IsNullOrWhiteSpace(profile.Command))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, $"MCP server '{profile.Name}' requires a command.");
    }

    private static McpOptions Clone(McpOptions value) => new()
    {
        ToolCacheSeconds = value.ToolCacheSeconds,
        Servers = value.Servers.Select(Clone).ToList()
    };

    private static McpServerProfile Clone(McpServerProfile value) => new()
    {
        Name = value.Name,
        Enabled = value.Enabled,
        Transport = value.Transport,
        Url = value.Url,
        Command = value.Command,
        Arguments = [.. value.Arguments],
        Environment = new Dictionary<string, string>(value.Environment, StringComparer.Ordinal),
        Headers = new Dictionary<string, string>(value.Headers, StringComparer.OrdinalIgnoreCase),
        AllowTools = [.. value.AllowTools],
        DenyTools = [.. value.DenyTools],
        TimeoutSeconds = value.TimeoutSeconds
    };
}
