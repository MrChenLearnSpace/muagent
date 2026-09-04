using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using MuAgents.Abstractions;

// CLI 只负责认证和呈现流；会话、工具和模型状态始终由 API 服务维护。
// 与 API 使用同一条便携路径规则，/add . 和所有相对路径都从 CLI 可执行文件目录开始。
RuntimePaths.InitializeProcessEnvironment();
var options = CliOptions.Parse(args);
var references = new FileReferenceSet(RuntimePaths.RootDirectory);
using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
Console.Write($"Password for {options.UserName}: ");
var password = ReadPassword();
// --bootstrap 仅用于空身份库。已初始化返回 Conflict 时继续登录，方便同一命令重复执行。
if (options.Bootstrap)
{
    var bootstrap = await client.PostAsJsonAsync("api/v1/auth/bootstrap", new
    {
        userName = options.UserName,
        password,
        tenantName = options.TenantName
    });
    if (!bootstrap.IsSuccessStatusCode && bootstrap.StatusCode != System.Net.HttpStatusCode.Conflict)
        bootstrap.EnsureSuccessStatusCode();
}
var login = await client.PostAsJsonAsync("api/v1/auth/login", new
{
    userName = options.UserName,
    password,
    tenantId = options.TenantId,
    useCookie = false
});
login.EnsureSuccessStatusCode();
using var loginDocument = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Bearer", loginDocument.RootElement.GetProperty("accessToken").GetString());
var loggedInUser = loginDocument.RootElement.GetProperty("user").GetProperty("userName").GetString() ?? options.UserName;
var loggedInTenant = loginDocument.RootElement.GetProperty("tenant").GetProperty("tenantName").GetString() ?? "unknown";

var conversationId = await CreateConversationAsync(client, "CLI conversation");
Console.WriteLine($"MuAgents 会话 {conversationId}。输入 /help 查看命令。");

while (true)
{
    Console.Write("you> ");
    var input = Console.ReadLine();
    if (input is null) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.StartsWith('/'))
    {
        var separator = input.IndexOf(' ');
        var command = (separator < 0 ? input : input[..separator]).ToLowerInvariant();
        var argument = separator < 0 ? string.Empty : input[(separator + 1)..].Trim();
        switch (command)
        {
            case "/exit":
            case "/quit":
                return;
            case "/help":
                PrintHelp();
                break;
            case "/model":
                await PrintModelAsync(client);
                break;
            case "/status":
                Console.WriteLine($"API: {client.BaseAddress}");
                Console.WriteLine($"用户/租户: {loggedInUser} / {loggedInTenant}");
                Console.WriteLine($"会话: {conversationId}");
                Console.WriteLine($"引用: {references.Count} 个文件，{references.TotalBytes} UTF-8 字节");
                Console.WriteLine($"CLI 程序根目录: {references.RootDirectory}");
                Console.WriteLine($"运行临时目录: {Environment.GetEnvironmentVariable("TEMP")}");
                await PrintContextStatusAsync(client, conversationId);
                break;
            case "/compact":
                await CompactAsync(client, conversationId);
                break;
            case "/new":
                conversationId = await CreateConversationAsync(
                    client,
                    string.IsNullOrWhiteSpace(argument) ? "CLI conversation" : argument);
                Console.WriteLine($"已创建新会话：{conversationId}");
                break;
            case "/add":
                try
                {
                    var result = await references.AddAsync(argument);
                    Console.WriteLine($"已添加 {result.Added} 个、更新 {result.Updated} 个文件；当前共 {references.Count} 个文件。");
                    foreach (var skipped in result.Skipped) Console.WriteLine($"  {skipped}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Console.Error.WriteLine($"添加引用失败：{exception.Message}");
                }
                break;
            case "/context":
            case "/files":
                if (references.Count == 0)
                    Console.WriteLine("当前没有文件引用。使用 /add . 或 /add <路径> 添加。");
                else
                    foreach (var file in references.Snapshot())
                        Console.WriteLine($"{System.Text.Encoding.UTF8.GetByteCount(file.Content),10}  {file.Path}");
                break;
            case "/remove":
                if (string.IsNullOrWhiteSpace(argument))
                    Console.WriteLine("用法：/remove <文件|目录|all>");
                else
                    Console.WriteLine($"已移除 {references.Remove(argument)} 个文件引用。");
                break;
            case "/mcp":
            case "/mcp_list":
                await PrintMcpAsync(client);
                break;
            case "/mcp_add":
                await AddMcpAsync(client, argument);
                break;
            case "/mcp_remove":
                if (RequireArgument(argument, "/mcp_remove <名称>"))
                    await DeleteAsync(client, $"api/v1/mcp/{Uri.EscapeDataString(argument)}", "MCP");
                break;
            case "/mcp_enable":
                if (RequireArgument(argument, "/mcp_enable <名称>"))
                    await SetEnabledAsync(client, $"api/v1/mcp/{Uri.EscapeDataString(argument)}/enabled", true, "MCP");
                break;
            case "/mcp_disable":
                if (RequireArgument(argument, "/mcp_disable <名称>"))
                    await SetEnabledAsync(client, $"api/v1/mcp/{Uri.EscapeDataString(argument)}/enabled", false, "MCP");
                break;
            case "/mcp_tools":
                await PrintMcpToolsAsync(client, argument);
                break;
            case "/skills":
            case "/skills_list":
                await PrintSkillsAsync(client);
                break;
            case "/skills_add":
                await AddSkillDirectoryAsync(client, argument);
                break;
            case "/skills_remove":
                if (RequireArgument(argument, "/skills_remove <目录>"))
                    await DeleteAsync(
                        client,
                        $"api/v1/skills/directories?path={Uri.EscapeDataString(argument.Trim().Trim('"'))}",
                        "Skill 目录");
                break;
            case "/skills_enable":
                if (RequireArgument(argument, "/skills_enable <名称>"))
                    await SetEnabledAsync(client, $"api/v1/skills/{Uri.EscapeDataString(argument)}/enabled", true, "Skill");
                break;
            case "/skills_disable":
                if (RequireArgument(argument, "/skills_disable <名称>"))
                    await SetEnabledAsync(client, $"api/v1/skills/{Uri.EscapeDataString(argument)}/enabled", false, "Skill");
                break;
            default:
                Console.WriteLine($"未知命令：{command}。输入 /help 查看可用命令。");
                break;
        }
        continue;
    }

    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"api/v1/conversations/{conversationId}/messages")
    {
        Content = JsonContent.Create(new { text = input, references = references.Snapshot() })
    };
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync();
    using var reader = new StreamReader(stream);
    Console.Write("agent> ");
    // 服务端使用 NDJSON：每行独立解析，不能等待整个响应结束后再反序列化。
    while (await reader.ReadLineAsync() is { } line)
    {
        try
        {
            using var item = JsonDocument.Parse(line);
            var root = item.RootElement;
            if (!TryGetProperty(root, "type", out var typeElement) ||
                !TryGetProperty(root, "data", out var data))
            {
                Console.Error.WriteLine("\nwarning: Server returned an invalid stream event.");
                continue;
            }

            var type = typeElement.GetString();
            if (type == "text_delta" && TryGetString(data, "delta", out var delta)) Console.Write(delta);
            if (type == "warning" && TryGetString(data, "message", out var warning)) Console.Error.WriteLine($"\nwarning: {warning}");
            if (type == "error" && TryGetString(data, "message", out var error)) Console.Error.WriteLine($"\nerror: {error}");
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("\nwarning: Server returned malformed JSON in the response stream.");
        }
    }
    Console.WriteLine();
    await PrintContextStatusAsync(client, conversationId);
}

static async Task<string> CreateConversationAsync(HttpClient client, string title)
{
    using var response = await client.PostAsJsonAsync("api/v1/conversations", new { title });
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.GetProperty("id").GetString()!;
}

static async Task PrintModelAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/model");
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var model = document.RootElement;
    Console.WriteLine($"模型: {model.GetProperty("model").GetString()}");
    Console.WriteLine($"协议: {model.GetProperty("protocol").GetString()}");
    Console.WriteLine($"端点: {model.GetProperty("endpoint").GetString()}");
    Console.WriteLine($"上下文/输出上限: {model.GetProperty("maxContextTokens").GetInt32()} / {model.GetProperty("maxOutputTokens").GetInt32()} tokens");
    Console.WriteLine($"能力: 图片={model.GetProperty("supportsVision").GetBoolean()}，工具={model.GetProperty("supportsTools").GetBoolean()}");
    Console.WriteLine($"API Key 已配置: {model.GetProperty("apiKeyConfigured").GetBoolean()}");
}

static async Task PrintContextStatusAsync(HttpClient client, string conversationId)
{
    using var response = await client.GetAsync($"api/v1/conversations/{conversationId}/context");
    if (!await EnsureCommandSuccessAsync(response, "读取上下文状态")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var value = document.RootElement;
    var current = value.GetProperty("currentTokens").GetInt32();
    var maximum = value.GetProperty("maxContextTokens").GetInt32();
    Console.WriteLine($"[上下文: {current:N0} / {maximum:N0} tokens，{(maximum == 0 ? 0 : current * 100d / maximum):F1}%]");
}

static async Task CompactAsync(HttpClient client, string conversationId)
{
    using var response = await client.PostAsync($"api/v1/conversations/{conversationId}/compact", null);
    if (!await EnsureCommandSuccessAsync(response, "压缩上下文")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var value = document.RootElement;
    Console.WriteLine(
        $"上下文压缩完成：{value.GetProperty("currentTokens").GetInt32():N0} / " +
        $"{value.GetProperty("maxContextTokens").GetInt32():N0} tokens，" +
        $"目标不超过 {value.GetProperty("compactTargetTokens").GetInt32():N0}。");
}

static async Task PrintMcpAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/mcp");
    if (!await EnsureCommandSuccessAsync(response, "读取 MCP 配置")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = document.RootElement;
    Console.WriteLine($"MCP 配置文件：{root.GetProperty("configurationPath").GetString()}");
    var servers = root.GetProperty("servers");
    if (servers.GetArrayLength() == 0) Console.WriteLine("当前没有 MCP 服务。");
    foreach (var server in servers.EnumerateArray())
    {
        var target = server.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()
            : server.TryGetProperty("command", out var command) ? command.GetString() : null;
        Console.WriteLine($"- {server.GetProperty("name").GetString()}  " +
                          $"[{(server.GetProperty("enabled").GetBoolean() ? "启用" : "禁用")}]  " +
                          $"{server.GetProperty("transport").GetString()}  {target}");
    }
}

static async Task AddMcpAsync(HttpClient client, string argument)
{
    if (string.IsNullOrWhiteSpace(argument))
    {
        Console.WriteLine("用法：/mcp_add <url> 或 /mcp_add <名称> <url>");
        return;
    }
    var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var firstIsUrl = Uri.TryCreate(parts[0].Trim('"'), UriKind.Absolute, out _);
    var name = firstIsUrl ? null : parts[0];
    var url = (firstIsUrl ? parts[0] : parts.Length > 1 ? parts[1] : string.Empty).Trim('"');
    if (!Uri.TryCreate(url, UriKind.Absolute, out _))
    {
        Console.WriteLine("MCP URL 无效。用法：/mcp_add <url> 或 /mcp_add <名称> <url>");
        return;
    }
    using var response = await client.PostAsJsonAsync("api/v1/mcp", new { name, url, enabled = true });
    if (!await EnsureCommandSuccessAsync(response, "添加 MCP")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Console.WriteLine($"MCP 已保存：{document.RootElement.GetProperty("name").GetString()}");
    Console.WriteLine($"配置文件：{document.RootElement.GetProperty("configurationPath").GetString()}");
}

static async Task PrintMcpToolsAsync(HttpClient client, string server)
{
    if (string.IsNullOrWhiteSpace(server))
    {
        Console.WriteLine("用法：/mcp_tools <名称>");
        return;
    }
    using var response = await client.GetAsync($"api/v1/mcp/{Uri.EscapeDataString(server)}/tools");
    if (!await EnsureCommandSuccessAsync(response, "读取 MCP 工具")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var tools = document.RootElement;
    if (tools.GetArrayLength() == 0) Console.WriteLine("该 MCP 没有可用工具。");
    foreach (var tool in tools.EnumerateArray())
        Console.WriteLine($"- {tool.GetProperty("name").GetString()}  {tool.GetProperty("description").GetString()}");
}

static async Task PrintSkillsAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/skills/config");
    if (!await EnsureCommandSuccessAsync(response, "读取 Skill 配置")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var root = document.RootElement;
    Console.WriteLine($"Skill 配置文件：{root.GetProperty("configurationPath").GetString()}");
    Console.WriteLine("扫描目录：");
    foreach (var directory in root.GetProperty("directories").EnumerateArray())
        Console.WriteLine($"  {directory.GetString()}");
    var skills = root.GetProperty("skills");
    if (skills.GetArrayLength() == 0) Console.WriteLine("当前没有发现 Skill。");
    foreach (var skill in skills.EnumerateArray())
        Console.WriteLine($"- {skill.GetProperty("name").GetString()}  " +
                          $"[{(skill.GetProperty("enabled").GetBoolean() ? "启用" : "禁用")}]  " +
                          $"{skill.GetProperty("description").GetString()}");
}

static async Task AddSkillDirectoryAsync(HttpClient client, string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        Console.WriteLine("用法：/skills_add <Skill 文件目录>");
        return;
    }
    using var response = await client.PostAsJsonAsync(
        "api/v1/skills/directories",
        new { path = path.Trim().Trim('"') });
    if (!await EnsureCommandSuccessAsync(response, "添加 Skill 目录")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Console.WriteLine($"Skill 目录已保存：{document.RootElement.GetProperty("directory").GetString()}");
    Console.WriteLine($"配置文件：{document.RootElement.GetProperty("configurationPath").GetString()}");
}

static async Task SetEnabledAsync(HttpClient client, string uri, bool enabled, string kind)
{
    using var response = await client.PutAsJsonAsync(uri, new { enabled });
    if (await EnsureCommandSuccessAsync(response, $"{(enabled ? "启用" : "禁用")}{kind}"))
        Console.WriteLine($"{kind} 已{(enabled ? "启用" : "禁用")}。");
}

static async Task DeleteAsync(HttpClient client, string uri, string kind)
{
    using var response = await client.DeleteAsync(uri);
    if (await EnsureCommandSuccessAsync(response, $"删除{kind}")) Console.WriteLine($"{kind} 配置已删除。");
}

static async Task<bool> EnsureCommandSuccessAsync(HttpResponseMessage response, string operation)
{
    if (response.IsSuccessStatusCode) return true;
    var detail = await response.Content.ReadAsStringAsync();
    Console.Error.WriteLine($"{operation}失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase} {detail}");
    return false;
}

static bool RequireArgument(string argument, string usage)
{
    if (!string.IsNullOrWhiteSpace(argument)) return true;
    Console.WriteLine($"用法：{usage}");
    return false;
}

static void PrintHelp()
{
    Console.WriteLine("""
        可用命令：
          /help                 显示本帮助
          /model                显示当前模型、协议、端点和能力
          /status               显示连接、身份、会话和文件上下文状态
          /compact              把当前会话压缩到最大上下文的 1/3 以内
          /new [标题]           创建新会话（文件引用继续保留）
          /add [文件或目录]     添加文件；目录会递归，省略路径等同 /add .
          /context              列出当前引用文件（/files 是别名）
          /remove <路径|all>    移除文件、目录或全部引用
          /mcp                  查看 MCP 服务和配置文件路径
          /mcp_add [名称] <url> 添加或更新 HTTP MCP 服务
          /mcp_remove <名称>    删除 MCP 配置
          /mcp_enable <名称>    启用 MCP
          /mcp_disable <名称>   禁用 MCP
          /mcp_tools <名称>     查看 MCP 暴露的工具
          /skills               查看 Skill、状态、目录和配置文件路径
          /skills_add <目录>    添加 Skill 或 Skill 根目录
          /skills_remove <目录> 从扫描配置中删除目录（不删除物理文件）
          /skills_enable <名称> 启用 Skill
          /skills_disable <名称> 禁用 Skill
          /exit                 退出（/quit 是别名）

        文件引用只包含 UTF-8/UTF-16 文本；自动排除生成目录、秘密文件、二进制和超限文件。
        """);
}

// 字段匹配忽略大小写，以兼容修复前使用默认序列化器输出 PascalCase 的服务端。
static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
    }

    value = default;
    return false;
}

static bool TryGetString(JsonElement element, string name, out string? value)
{
    if (TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
    {
        value = property.GetString();
        return true;
    }

    value = null;
    return false;
}

// 交互终端不回显密码；重定向输入时保留标准输入能力，便于自动化测试。
static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
    var password = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is { } key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && password.Length > 0) password.Length--;
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.WriteLine();
    return password.ToString();
}

/// <summary>CLI 支持的连接、账户、租户和首次初始化参数。</summary>
internal sealed record CliOptions(
    string Url,
    string UserName,
    string? TenantId,
    bool Bootstrap,
    string TenantName)
{
    public static CliOptions Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        return new CliOptions(
            Value("--url", "http://localhost:5000/"),
            Value("--user", Environment.UserName),
            Value("--tenant", string.Empty) is { Length: > 0 } tenant ? tenant : null,
            args.Contains("--bootstrap", StringComparer.OrdinalIgnoreCase),
            Value("--tenant-name", "Local"));
    }
}
