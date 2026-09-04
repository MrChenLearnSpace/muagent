using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MuAgents.Abstractions;

namespace MuAgents.Mcp;

/// <summary>从指定 MCP 服务发现的工具描述。</summary>
public sealed record McpToolInfo(string Server, string Name, string? Description, JsonElement InputSchema);

/// <summary>管理 MCP 连接、工具发现和远程调用的宿主接口。</summary>
public interface IMcpClientManager
{
    string ConfigurationPath { get; }
    IReadOnlyList<McpServerProfile> ListServers();
    Task<McpServerProfile> UpsertServerAsync(McpServerProfile profile);
    Task<bool> SetServerEnabledAsync(string name, bool enabled);
    Task<bool> RemoveServerAsync(string name);
    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(string server, CancellationToken cancellationToken = default);
    Task<ToolResult> InvokeAsync(string server, string tool, JsonElement arguments, CancellationToken cancellationToken = default);
}

/// <summary>延迟建立 MCP 客户端、缓存工具清单并按服务配置过滤工具的管理器。</summary>
public sealed class McpClientManager : IMcpClientManager, IAsyncDisposable
{
    private readonly McpConfigurationStore _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<McpClientManager> _logger;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlyList<McpToolInfo> Tools)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StdioSession> _stdio = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _httpSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _initializationLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _initialized = new(StringComparer.OrdinalIgnoreCase);

    public McpClientManager(
        McpConfigurationStore configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<McpClientManager> logger)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ConfigurationPath => _configuration.ConfigurationPath;

    public IReadOnlyList<McpServerProfile> ListServers() => _configuration.Snapshot().Servers;

    public async Task<McpServerProfile> UpsertServerAsync(McpServerProfile profile)
    {
        var saved = _configuration.Upsert(profile);
        await ResetServerAsync(saved.Name).ConfigureAwait(false);
        return saved;
    }

    public async Task<bool> SetServerEnabledAsync(string name, bool enabled)
    {
        var changed = _configuration.SetEnabled(name, enabled);
        if (changed) await ResetServerAsync(name).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> RemoveServerAsync(string name)
    {
        var removed = _configuration.Remove(name);
        if (removed) await ResetServerAsync(name).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        string server,
        CancellationToken cancellationToken = default)
    {
        var profile = Profile(server);
        // 工具定义通常稳定，短期缓存可避免每轮对话都发起 tools/list。
        if (_cache.TryGetValue(profile.Name, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            return cached.Tools;
        var result = await RequestAsync(profile, "tools/list", new { }, cancellationToken).ConfigureAwait(false);
        var tools = result.TryGetProperty("tools", out var array)
            ? array.EnumerateArray()
                .Where(item => IsAllowed(profile, String(item, "name") ?? string.Empty))
                .Select(item => new McpToolInfo(
                    profile.Name,
                    String(item, "name")!,
                    String(item, "description"),
                    item.TryGetProperty("inputSchema", out var schema) ? schema.Clone() : JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone()))
                .ToArray()
            : [];
        _cache[profile.Name] = (
            DateTimeOffset.UtcNow.AddSeconds(_configuration.Snapshot().ToolCacheSeconds),
            tools);
        return tools;
    }

    public async Task<ToolResult> InvokeAsync(
        string server,
        string tool,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var profile = Profile(server);
        // 白名单/黑名单在发出网络或进程请求前执行，不能依赖 MCP 服务端自行授权。
        if (!IsAllowed(profile, tool)) return new ToolResult($"MCP tool '{tool}' is denied by policy.", true);
        var available = await ListToolsAsync(server, cancellationToken).ConfigureAwait(false);
        if (!available.Any(candidate => candidate.Name == tool))
            return new ToolResult($"MCP tool '{tool}' is unavailable on server '{server}'.", true);
        var result = await RequestAsync(profile, "tools/call", new { name = tool, arguments }, cancellationToken)
            .ConfigureAwait(false);
        var isError = result.TryGetProperty("isError", out var errorValue) && errorValue.ValueKind == JsonValueKind.True;
        var content = result.TryGetProperty("content", out var contentValue)
            ? string.Join("\n", contentValue.EnumerateArray().Select(item => String(item, "text") ?? item.GetRawText()))
            : result.GetRawText();
        return new ToolResult(content, isError);
    }

    private async Task<JsonElement> RequestAsync(
        McpServerProfile profile,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        await EnsureInitializedAsync(profile, timeout.Token).ConfigureAwait(false);
        return await RawRequestAsync(profile, method, parameters, timeout.Token).ConfigureAwait(false);
    }

    private Task<JsonElement> RawRequestAsync(
        McpServerProfile profile,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        return profile.Transport switch
        {
            McpTransport.StreamableHttp => HttpRequestAsync(profile, method, parameters, cancellationToken),
            McpTransport.Stdio => _stdio.GetOrAdd(profile.Name, _ => new StdioSession(profile, _logger))
                .RequestAsync(method, parameters, cancellationToken),
            _ => throw new MuAgentException(MuAgentErrorCategory.Configuration, "Unsupported MCP transport.")
        };
    }

    private async Task EnsureInitializedAsync(McpServerProfile profile, CancellationToken cancellationToken)
    {
        if (_initialized.ContainsKey(profile.Name)) return;
        // 同一服务只执行一次 MCP initialize 握手，并阻止并发首请求重复创建会话。
        var gate = _initializationLocks.GetOrAdd(profile.Name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized.ContainsKey(profile.Name)) return;
            _ = await RawRequestAsync(profile, "initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "MuAgents", version = "0.2" }
            }, cancellationToken).ConfigureAwait(false);
            if (profile.Transport == McpTransport.StreamableHttp)
                await HttpNotificationAsync(profile, "notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
            else
                await _stdio[profile.Name].NotifyAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
            _initialized[profile.Name] = 0;
        }
        finally { gate.Release(); }
    }

    private async Task<JsonElement> HttpRequestAsync(
        McpServerProfile profile,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(profile.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, $"MCP server '{profile.Name}' has an invalid URL.");
        var client = _httpClientFactory.CreateClient("MuAgents.Mcp");
        var id = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var header in profile.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (_httpSessions.TryGetValue(profile.Name, out var session))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessions))
            _httpSessions[profile.Name] = sessions.First();
        var payload = await ReadPayloadAsync(response, cancellationToken).ConfigureAwait(false);
        return Result(payload);
    }

    private async Task HttpNotificationAsync(
        McpServerProfile profile,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("MuAgents.Mcp");
        using var request = new HttpRequestMessage(HttpMethod.Post, profile.Url)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = parameters })
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var header in profile.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (_httpSessions.TryGetValue(profile.Name, out var session))
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var media = response.Content.Headers.ContentType?.MediaType;
        if (media == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return JsonDocument.Parse(line[5..].Trim()).RootElement.Clone();
            throw new MuAgentException(MuAgentErrorCategory.InvalidModelResponse, "MCP stream returned no data event.");
        }
        using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new MuAgentException(MuAgentErrorCategory.InvalidModelResponse, "MCP returned no JSON.");
        return document.RootElement.Clone();
    }

    private McpServerProfile Profile(string server) =>
        _configuration.Snapshot().Servers.FirstOrDefault(
            profile => profile.Enabled && profile.Name.Equals(server, StringComparison.OrdinalIgnoreCase))
        ?? throw new MuAgentException(
            MuAgentErrorCategory.Configuration,
            $"MCP server '{server}' is not configured or is disabled.");

    private static bool IsAllowed(McpServerProfile profile, string tool) =>
        !profile.DenyTools.Contains(tool, StringComparer.Ordinal) &&
        (profile.AllowTools.Count == 0 || profile.AllowTools.Contains(tool, StringComparer.Ordinal));

    private static JsonElement Result(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
            throw new MuAgentException(MuAgentErrorCategory.ToolFailure, $"MCP error: {error.GetRawText()}");
        if (!response.TryGetProperty("result", out var result))
            throw new MuAgentException(MuAgentErrorCategory.InvalidModelResponse, "MCP response has no result.");
        return result.Clone();
    }

    private static string? String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _stdio.Values) await session.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ResetServerAsync(string name)
    {
        _cache.TryRemove(name, out _);
        _httpSessions.TryRemove(name, out _);
        _initialized.TryRemove(name, out _);
        if (_stdio.TryRemove(name, out var session))
            await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class StdioSession(McpServerProfile profile, ILogger logger) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Process? _process;
        private RuntimeTemporaryDirectory? _temporaryDirectory;

        public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStarted();
                var id = Guid.NewGuid().ToString("N");
                await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }))
                    .ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                while (await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    if (String(document.RootElement, "id") == id) return Result(document.RootElement);
                }
                throw new MuAgentException(MuAgentErrorCategory.ToolFailure, $"MCP stdio server '{profile.Name}' closed its output.");
            }
            finally { _gate.Release(); }
        }

        public async Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStarted();
                await _process!.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }))
                    .ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private void EnsureStarted()
        {
            if (_process is { HasExited: false }) return;
            if (string.IsNullOrWhiteSpace(profile.Command))
                throw new MuAgentException(MuAgentErrorCategory.Configuration, $"MCP server '{profile.Name}' has no command.");
            _process?.Dispose();
            _temporaryDirectory?.Dispose();
            _temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("mcp");
            var start = new ProcessStartInfo
            {
                FileName = profile.Command,
                // 保持相对脚本路径基于程序根目录解析；临时文件则通过下方环境变量进入专用目录。
                WorkingDirectory = RuntimePaths.RootDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in profile.Arguments) start.ArgumentList.Add(argument);
            foreach (var item in profile.Environment) start.Environment[item.Key] = item.Value;
            // MCP 服务是第三方进程，显式重定向所有常见临时目录变量，防止它回落到系统盘。
            // 固定变量最后写入，配置文件不能用同名 Environment 条目绕过程序根目录约束。
            RuntimePaths.ConfigureChildProcess(start, _temporaryDirectory.DirectoryPath);
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, args) => { if (args.Data is not null) logger.LogDebug("MCP {Server}: {Message}", profile.Name, args.Data); };
            if (!_process.Start()) throw new MuAgentException(MuAgentErrorCategory.ToolFailure, $"MCP server '{profile.Name}' could not start.");
            _process.BeginErrorReadLine();
        }

        public ValueTask DisposeAsync()
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
            _process?.Dispose();
            _temporaryDirectory?.Dispose();
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
