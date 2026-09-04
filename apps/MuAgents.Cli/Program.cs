using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

// CLI 只负责认证和呈现流；会话、工具和模型状态始终由 API 服务维护。
// CLI 不创建项目状态目录；当前终端目录仅用于解析 /add 的本地文件引用。
var options = CliOptions.Parse(args);
var cliWorkingDirectory = Path.GetFullPath(Directory.GetCurrentDirectory());
var references = new FileReferenceSet(cliWorkingDirectory);
using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
Console.WriteLine($"CLI 文件工作目录：{cliWorkingDirectory}");

string password;
HttpResponseMessage login;
if (options.SetupPassword)
{
    // 只有显式参数才在首次初始化时要求设置密码；--bootstrap 作为旧名称继续兼容。
    password = ReadNewPassword(options.UserName);
    using var bootstrap = await client.PostAsJsonAsync("api/v1/auth/bootstrap", new
    {
        userName = options.UserName,
        password,
        tenantName = options.TenantName
    });
    if (!bootstrap.IsSuccessStatusCode && bootstrap.StatusCode != System.Net.HttpStatusCode.Conflict)
        bootstrap.EnsureSuccessStatusCode();
    login = await PostLoginAsync(client, options, password);
}
else
{
    // 默认先尝试无密码登录。空身份库会自动建立 admin/Local；已有密码的账户才提示输入。
    password = string.Empty;
    login = await PostLoginAsync(client, options, password);
    if (!login.IsSuccessStatusCode)
    {
        login.Dispose();
        using var bootstrap = await client.PostAsJsonAsync("api/v1/auth/bootstrap", new
        {
            userName = options.UserName,
            password = string.Empty,
            tenantName = options.TenantName
        });
        if (bootstrap.IsSuccessStatusCode)
        {
            Console.WriteLine($"已为新项目创建无密码用户 {options.UserName} 和租户 {options.TenantName}。");
            login = await PostLoginAsync(client, options, string.Empty);
        }
        else if (bootstrap.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            Console.Write($"Password for {options.UserName}: ");
            password = ReadPassword();
            login = await PostLoginAsync(client, options, password);
        }
        else
        {
            bootstrap.EnsureSuccessStatusCode();
            throw new InvalidOperationException("Identity initialization failed.");
        }
    }
}
using var loginResponse = login;
loginResponse.EnsureSuccessStatusCode();
using var loginDocument = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Bearer", loginDocument.RootElement.GetProperty("accessToken").GetString());
var loggedInUser = loginDocument.RootElement.GetProperty("user").GetProperty("userName").GetString() ?? options.UserName;
var loggedInTenant = loginDocument.RootElement.GetProperty("tenant").GetProperty("tenantName").GetString() ?? "unknown";
var accessTokenExpiresAt = loginDocument.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
if (password.Length == 0)
    Console.WriteLine($"认证模式：{loggedInUser} 当前未设置密码，仅建议在绑定 127.0.0.1 的可信本机使用。");
var commandApprovalMode = await GetCommandApprovalModeAsync(client);

var conversationSelection = await GetOrCreateConversationAsync(client);
var conversationId = conversationSelection.Id;
Console.WriteLine(conversationSelection.Resumed
    ? $"已恢复最近会话 {conversationId}，历史上下文会继续发送给模型。输入 /new 可创建新会话。"
    : $"已创建 MuAgents 会话 {conversationId}。输入 /help 查看命令。");

while (true)
{
    var input = SlashCommandLine.ReadLine("you> ");
    if (input is null) break;
    if (string.IsNullOrWhiteSpace(input)) continue;
    // CLI 可以长时间保持打开；令牌即将过期时在发送任何 API 请求前重新登录，避免丢失当前会话。
    if (DateTimeOffset.UtcNow >= accessTokenExpiresAt.AddMinutes(-1))
    {
        var refreshed = await PostLoginAsync(client, options, password);
        if (refreshed.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            refreshed.Dispose();
            Console.Write($"Password for {options.UserName}: ");
            password = ReadPassword();
            refreshed = await PostLoginAsync(client, options, password);
        }
        using (refreshed)
        {
            refreshed.EnsureSuccessStatusCode();
            using var refreshedDocument = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", refreshedDocument.RootElement.GetProperty("accessToken").GetString());
            accessTokenExpiresAt = refreshedDocument.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        }
        Console.WriteLine("登录令牌已自动续期，当前会话保持不变。");
    }
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
                Console.WriteLine($"CLI 文件工作目录: {references.RootDirectory}");
                await PrintRuntimeAsync(client);
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
            if (type == "tool_call_started" &&
                TryGetString(data, "name", out var toolName) &&
                toolName is "local.execute_command" or "local.write_file" &&
                TryGetString(data, "callId", out var callId) &&
                TryGetString(data, "argumentsJson", out var argumentsJson))
            {
                Console.WriteLine();
                Console.WriteLine(toolName == "local.execute_command"
                    ? $"控制台命令请求：{FormatCommand(argumentsJson!)}"
                    : $"项目文件写入请求：{FormatFileWrite(argumentsJson!)}");
                if (commandApprovalMode.Equals("RequireApproval", StringComparison.OrdinalIgnoreCase))
                    await HandleCommandApprovalAsync(client, conversationId, callId!);
                else
                    Console.WriteLine(commandApprovalMode.Equals("Allowed", StringComparison.OrdinalIgnoreCase)
                        ? "审批模式为 Allowed，APP 将自动执行本地操作。"
                        : "审批模式为 Denied，APP 将拒绝本地操作。");
                Console.Write("agent> ");
            }
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

static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, CliOptions options, string password) =>
    client.PostAsJsonAsync("api/v1/auth/login", new
    {
        userName = options.UserName,
        password,
        tenantId = options.TenantId,
        useCookie = false
    });

static async Task<string> CreateConversationAsync(HttpClient client, string title)
{
    using var response = await client.PostAsJsonAsync("api/v1/conversations", new { title });
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.GetProperty("id").GetString()!;
}

/// <summary>默认恢复当前用户最近更新的会话；没有历史时才创建，避免 CLI 重启后丢失上下文。</summary>
static async Task<(string Id, bool Resumed)> GetOrCreateConversationAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/conversations?limit=1");
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0)
    {
        var id = document.RootElement[0].GetProperty("id").GetString();
        if (!string.IsNullOrWhiteSpace(id)) return (id, true);
    }
    return (await CreateConversationAsync(client, "CLI conversation"), false);
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

static async Task PrintRuntimeAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/runtime");
    if (!await EnsureCommandSuccessAsync(response, "读取服务端运行配置")) return;
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var runtime = document.RootElement;
    Console.WriteLine($"API 项目根目录: {runtime.GetProperty("projectDirectory").GetString()}");
    Console.WriteLine($"API 状态目录: {runtime.GetProperty("stateDirectory").GetString()}");
    if (TryGetString(runtime, "commandApprovalMode", out var approvalMode))
        Console.WriteLine($"控制台审批模式: {approvalMode}");
    Console.WriteLine("API 已加载配置文件:");
    foreach (var path in runtime.GetProperty("configurationFiles").EnumerateArray())
        Console.WriteLine($"  {path.GetString()}");
}

static async Task<string> GetCommandApprovalModeAsync(HttpClient client)
{
    using var response = await client.GetAsync("api/v1/runtime");
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return TryGetString(document.RootElement, "commandApprovalMode", out var mode) ? mode ?? "Denied" : "Denied";
}

static async Task HandleCommandApprovalAsync(
    HttpClient client,
    string conversationId,
    string callId)
{
    Console.Write("是否批准本次执行？[y/N]: ");
    var approved = ReadConfirmation();
    // 服务端在发出 tool_call_started 后注册等待项；极短竞态下重试 404，不能把批准误投给其他调用。
    for (var attempt = 0; attempt < 20; attempt++)
    {
        using var response = await client.PostAsJsonAsync(
            $"api/v1/command-approvals/{Uri.EscapeDataString(conversationId)}/{Uri.EscapeDataString(callId)}",
            new { approved });
        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine(approved ? "已批准本次命令。" : "已拒绝本次命令。");
            return;
        }
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            await EnsureCommandSuccessAsync(response, "提交命令审批");
            return;
        }
        await Task.Delay(50);
    }
    Console.Error.WriteLine("命令审批请求已失效或不存在。");
}

static string FormatCommand(string argumentsJson)
{
    try
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var command = root.TryGetProperty("command", out var commandElement)
            ? commandElement.GetString() ?? "<未指定>"
            : "<未指定>";
        var arguments = root.TryGetProperty("arguments", out var argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.Array
            ? argumentsElement.EnumerateArray().Select(value => JsonSerializer.Serialize(value.GetString())).ToArray()
            : [];
        var workingDirectory = root.TryGetProperty("workingDirectory", out var directoryElement)
            ? directoryElement.GetString()
            : null;
        return $"{command} {string.Join(' ', arguments)}" +
               (string.IsNullOrWhiteSpace(workingDirectory) ? string.Empty : $"  （目录：{workingDirectory}）");
    }
    catch (JsonException)
    {
        return argumentsJson;
    }
}

/// <summary>文件正文不会进入审批显示，只呈现服务端生成的路径、长度和覆盖标记。</summary>
static string FormatFileWrite(string argumentsJson)
{
    try
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (TryGetString(root, "error", out var error)) return $"参数无效，将反馈模型重试：{error}";
        var path = TryGetString(root, "path", out var configuredPath) ? configuredPath : "<未指定>";
        var characters = root.TryGetProperty("characters", out var charactersElement) && charactersElement.TryGetInt32(out var count)
            ? count
            : 0;
        var overwrite = root.TryGetProperty("overwrite", out var overwriteElement) && overwriteElement.ValueKind == JsonValueKind.True;
        return $"{path}（{characters} 字符，{(overwrite ? "允许覆盖" : "仅新建")}）";
    }
    catch (JsonException)
    {
        return "<参数无法解析>";
    }
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
    Console.WriteLine("可用命令：");
    foreach (var command in SlashCommandCatalog.Commands)
        Console.WriteLine($"  {command.Usage,-28} {command.Description}");
    Console.WriteLine();
    Console.WriteLine("输入 / 后按 Tab 查看候选，输入命令前缀后按 Tab 自动补全。");
    Console.WriteLine("文件引用只包含 UTF-8/UTF-16 文本；自动排除生成目录、秘密文件、二进制和超限文件。");
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

/// <summary>首次启用密码时读取两次，避免输入错误的密码后只能由服务器管理员重置。</summary>
static string ReadNewPassword(string userName)
{
    Console.Write($"Set password for {userName}: ");
    var password = ReadPassword();
    Console.Write("Confirm password: ");
    var confirmation = ReadPassword();
    if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        throw new InvalidOperationException("两次输入的密码不一致。");
    return password;
}

// 审批只接受明确的 y/yes；回车、其他字符和重定向输入结束都按拒绝处理。
static bool ReadConfirmation()
{
    return (Console.ReadLine() ?? string.Empty).Trim() is "y" or "Y" or "yes" or "YES";
}

/// <summary>CLI 支持的连接、账户、租户和可选密码初始化参数。</summary>
public sealed record CliOptions(
    string Url,
    string UserName,
    string? TenantId,
    bool SetupPassword,
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
            Value("--user", "admin"),
            Value("--tenant", string.Empty) is { Length: > 0 } tenant ? tenant : null,
            args.Contains("--setup-password", StringComparer.OrdinalIgnoreCase) ||
                args.Contains("--bootstrap", StringComparer.OrdinalIgnoreCase),
            Value("--tenant-name", "Local"));
    }
}
