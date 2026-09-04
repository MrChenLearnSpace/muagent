using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;
using MuAgents.Abstractions;
using MuAgents.Core;
using MuAgents.Hosting;
using MuAgents.OpenAI;
using MuAgents.Mcp;
using MuAgents.Skills;
using MuAgents.Tools;

// -d 可显式指定项目根；未指定时使用启动终端当前目录。通用参数先被移除，避免宿主重复解释。
var launchArguments = RuntimeLaunchArguments.Parse(args);
RuntimePaths.InitializeProcessEnvironment(launchArguments.ProjectDirectory);
args = launchArguments.RemainingArguments;
var packagedSettingsPath = Path.Combine(RuntimePaths.ApplicationDirectory, "muagents.settings.json");
var projectConfigurationDirectory = RuntimePaths.ResolveWritePath("config", "project configuration directory");
var projectAppSettingsPath = RuntimePaths.ResolveWritePath(
    Path.Combine("config", "appsettings.json"),
    "project appsettings path");
var projectSettingsPath = RuntimePaths.ResolveWritePath(
    Path.Combine("config", "muagents.settings.json"),
    "project MuAgents settings path");
Directory.CreateDirectory(projectConfigurationDirectory);
// 每个项目首次启动时获得一份独立模板，之后升级程序不会覆盖项目自己的模型和认证配置。
if (!File.Exists(projectSettingsPath)) File.Copy(packagedSettingsPath, projectSettingsPath);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // ASP.NET 的静态默认配置仍随二进制发布；项目覆盖配置从 .muagent/config 单独加载。
    ContentRootPath = RuntimePaths.ApplicationDirectory
});
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// appsettings.json 是随程序发布的默认值；项目级覆盖和秘密只存在当前项目的 .muagent/config。
builder.Configuration
    .AddJsonFile(packagedSettingsPath, optional: false, reloadOnChange: false)
    .AddJsonFile(projectAppSettingsPath, optional: true, reloadOnChange: true)
    .AddJsonFile(projectSettingsPath, optional: false, reloadOnChange: true)
    // 保持 ASP.NET Core 约定：环境变量和启动参数仍可覆盖项目文件。
    .AddEnvironmentVariables()
    .AddCommandLine(args);

// 记录确实存在且参与本次配置合并的文件；既方便启动排错，也供已认证 CLI 的 /status 查询。
var loadedConfigurationFiles = new List<string>();
AddLoadedConfiguration(Path.Combine(RuntimePaths.ApplicationDirectory, "appsettings.json"));
AddLoadedConfiguration(Path.Combine(
    RuntimePaths.ApplicationDirectory,
    $"appsettings.{builder.Environment.EnvironmentName}.json"));
AddLoadedConfiguration(packagedSettingsPath);
AddLoadedConfiguration(projectAppSettingsPath);
AddLoadedConfiguration(projectSettingsPath);

Console.WriteLine($"MuAgents 项目根目录：{RuntimePaths.ProjectDirectory}");
Console.WriteLine($"MuAgents 状态目录：{RuntimePaths.RootDirectory}");
Console.WriteLine($"控制台审批模式：{builder.Configuration.GetValue<CommandApprovalMode>("MuAgents:CommandExecution:ApprovalMode")}");
Console.WriteLine("本次加载的配置文件：");
foreach (var configurationFile in loadedConfigurationFiles) Console.WriteLine($"  {configurationFile}");

void AddLoadedConfiguration(string path)
{
    var fullPath = Path.GetFullPath(path);
    if (File.Exists(fullPath) && !loadedConfigurationFiles.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        loadedConfigurationFiles.Add(fullPath);
}

builder.Services.AddMuAgents(builder.Configuration);
builder.Services.AddProblemDetails();
var authenticationSection = builder.Configuration.GetSection("MuAgents:Authentication");
var configuredAuthentication = authenticationSection.Get<AuthenticationOptions>() ?? new AuthenticationOptions();
builder.Services.AddOptions<AuthenticationOptions>()
    .Bind(authenticationSection)
    .Validate(options => options.JwtSigningKey.Length >= 32, "JWT signing key must contain at least 32 characters.")
    .Validate(options => options.AccessTokenMinutes is >= 5 and <= 1440, "Access token lifetime must be 5-1440 minutes.")
    .Validate(options => options.MinimumPasswordLength is >= 12 and <= 128, "Minimum password length must be 12-128.")
    .ValidateOnStart();
builder.Services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
// Cookie 加密密钥需要持久化，但必须和数据库一样留在当前项目的 .muagent 内。
var dataProtectionPath = RuntimePaths.ResolveWritePath(
    configuredAuthentication.DataProtectionKeysPath,
    "MuAgents:Authentication:DataProtectionKeysPath");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("MuAgents");
builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
builder.Services.AddScoped<LocalAuthenticationService>();
var signingKey = configuredAuthentication.JwtSigningKey.Length >= 32
    ? Encoding.UTF8.GetBytes(configuredAuthentication.JwtSigningKey)
    : new byte[32];
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = "MuAgents.Auth";
        options.DefaultChallengeScheme = "MuAgents.Auth";
    })
    .AddPolicyScheme("MuAgents.Auth", "Cookie or JWT", options =>
    {
        // API 客户端带 Bearer 头时使用 JWT；浏览器请求默认使用 HttpOnly Cookie。
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "MuAgents.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(configuredAuthentication.CookieDays);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuredAuthentication.Issuer,
            ValidateAudience = true,
            ValidAudience = configuredAuthentication.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("system-admin", policy => policy.RequireClaim("system_admin", "true")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
// 在响应真正写出前加入关联 ID，流式响应同样可以据此查询日志和链路。
app.Use(async (context, next) =>
{
    var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.TryAdd("X-Trace-Id", traceId);
        return Task.CompletedTask;
    });
    await next();
});
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// 映射路由前初始化两组表结构，使启动失败能够尽早暴露而不是延迟到第一个用户请求。
await app.Services.GetRequiredService<IConversationStore>().InitializeAsync();
await app.Services.GetRequiredService<IIdentityStore>().InitializeAsync();

// --set-password 是本机一次性管理模式：从当前项目数据库读取用户、安全输入两遍新密码，
// 更新成功后直接退出，不开放 HTTP 监听，也不把明文密码放进进程参数或配置文件。
if (launchArguments.PasswordResetUserName is { } passwordResetUserName)
{
    Console.WriteLine($"本机密码修改模式，目标用户：{passwordResetUserName}");
    try
    {
        var newPassword = ReadConfirmedPassword();
        await using var scope = app.Services.CreateAsyncScope();
        var authentication = scope.ServiceProvider.GetRequiredService<LocalAuthenticationService>();
        await authentication.ResetPasswordAsync(passwordResetUserName, newPassword, CancellationToken.None);
        Console.WriteLine($"用户 {passwordResetUserName} 的密码已修改；APP 未启动 HTTP 服务。");
    }
    catch (Exception exception) when (exception is BadHttpRequestException or KeyNotFoundException or InvalidOperationException)
    {
        Console.Error.WriteLine($"密码修改失败：{exception.Message}");
        Environment.ExitCode = 2;
    }
    return;
}
// 动态扩展配置在启动时就创建，管理员无需先调用接口才能找到配置文件。
_ = app.Services.GetRequiredService<McpConfigurationStore>();
_ = app.Services.GetRequiredService<SkillConfigurationStore>();

var publicApi = app.MapGroup("/api/v1");
publicApi.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// bootstrap 是唯一无需身份的写接口，存储层以事务和唯一约束保证只能完成一次。
publicApi.MapPost("/auth/bootstrap", async (
    BootstrapRequest request,
    IIdentityStore store,
    LocalAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (await store.HasUsersAsync(cancellationToken))
        return Results.Conflict(new { message = "Identity bootstrap has already been completed." });
    var result = await authentication.BootstrapAsync(
        request.UserName, request.Password, request.TenantName, cancellationToken);
    return Results.Created("/api/v1/auth/login", new
    {
        user = new { result.User.Id, result.User.UserName },
        tenant = new { result.Membership.TenantId, result.Membership.TenantName, result.Membership.Role }
    });
}).RequireRateLimiting("authentication");
publicApi.MapPost("/auth/login", async (
    HttpContext http,
    LoginRequest request,
    LocalAuthenticationService authentication,
    IOptions<AuthenticationOptions> options,
    CancellationToken cancellationToken) =>
{
    var session = await authentication.LoginAsync(
        request.UserName, request.Password, request.TenantId,
        http.Connection.RemoteIpAddress?.ToString(), cancellationToken);
    if (session is null) return Results.Unauthorized();
    if (request.UseCookie)
    {
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            session.Principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(options.Value.CookieDays),
                AllowRefresh = true
            });
    }
    return Results.Ok(new
    {
        accessToken = session.AccessToken,
        tokenType = "Bearer",
        expiresAt = session.ExpiresAt,
        user = new { session.User.Id, session.User.UserName, session.User.IsSystemAdmin },
        tenant = new { session.Membership.TenantId, session.Membership.TenantName, session.Membership.Role }
    });
}).RequireRateLimiting("authentication");

// 下方路由全部要求已验证的 Cookie 或 JWT，不接受调用方自报用户/租户 ID。
var api = app.MapGroup("/api/v1").RequireAuthorization();
api.MapPost("/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});
api.MapGet("/auth/me", (HttpContext http) => Results.Ok(new
{
    userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier),
    userName = http.User.Identity?.Name,
    tenantId = http.User.FindFirstValue("tenant_id"),
    tenantName = http.User.FindFirstValue("tenant_name"),
    role = http.User.FindFirstValue(ClaimTypes.Role),
    isSystemAdmin = http.User.HasClaim("system_admin", "true")
}));
api.MapGet("/model", (IOptions<OpenAiCompatibleOptions> options) =>
{
    var model = options.Value;
    var endpoint = new Uri(new Uri(model.BaseUrl, UriKind.Absolute), model.ResolveEndpoint());
    // 只报告模型连接状态，不把实际密钥回传给任何客户端。
    return Results.Ok(new
    {
        protocol = model.Protocol.ToString(),
        endpoint = endpoint.ToString(),
        model = model.Model,
        model.MaxContextTokens,
        model.MaxOutputTokens,
        model.SupportsVision,
        model.SupportsTools,
        apiKeyConfigured = !string.IsNullOrWhiteSpace(model.ApiKey)
    });
});
api.MapGet("/runtime", (IOptions<CommandExecutionOptions> commandOptions) => Results.Ok(new
{
    projectDirectory = RuntimePaths.ProjectDirectory,
    stateDirectory = RuntimePaths.RootDirectory,
    commandApprovalMode = commandOptions.Value.ApprovalMode.ToString(),
    configurationFiles = loadedConfigurationFiles
}));
// 该接口只能释放当前认证身份、当前会话和精确调用 ID 对应的等待项，不能主动创建或执行命令。
api.MapPost("/command-approvals/{conversationId}/{callId}", (
    HttpContext http,
    string conversationId,
    string callId,
    CommandApprovalDecision request,
    CommandApprovalCoordinator approvals) =>
{
    var identity = RequestIdentity.From(http);
    return approvals.Resolve(identity.TenantId, identity.UserId, conversationId, callId, request.Approved)
        ? Results.Ok(new { conversationId, callId, request.Approved })
        : Results.NotFound();
});
api.MapGet("/auth/tenants", async (
    HttpContext http,
    IIdentityStore store,
    CancellationToken cancellationToken) =>
{
    // 身份只从已验证声明读取，避免请求正文或自定义头绕过租户隔离。
    var identity = RequestIdentity.From(http);
    return Results.Ok(await store.GetMembershipsAsync(identity.UserId, cancellationToken));
});

api.MapPost("/admin/users", async Task<IResult> (
    CreateUserRequest request,
    LocalAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    try
    {
        var user = await authentication.CreateUserAsync(
            request.UserName, request.Password, request.IsSystemAdmin, cancellationToken);
        return Results.Created($"/api/v1/admin/users/{user.Id}", new
        {
            user.Id,
            user.UserName,
            user.IsSystemAdmin,
            user.IsDisabled,
            user.CreatedAt
        });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
}).RequireAuthorization("system-admin");

api.MapPost("/admin/tenants", async Task<IResult> (
    HttpContext http,
    CreateTenantRequest request,
    IIdentityStore store,
    LocalAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    var ownerUserId = identity.UserId;
    if (!string.IsNullOrWhiteSpace(request.OwnerUserName))
    {
        var owner = await store.FindUserAsync(request.OwnerUserName, cancellationToken);
        if (owner is null) return Results.NotFound(new { message = "The requested owner was not found." });
        ownerUserId = owner.Id;
    }
    try
    {
        var tenant = await authentication.CreateTenantAsync(request.Name, ownerUserId, cancellationToken);
        return Results.Created($"/api/v1/admin/tenants/{tenant.Id}", tenant);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
}).RequireAuthorization("system-admin");

api.MapPut("/tenants/{tenantId}/members", async Task<IResult> (
    HttpContext http,
    string tenantId,
    SetMembershipRequest request,
    LocalAuthenticationService authentication,
    CancellationToken cancellationToken) =>
{
    if (!TenantAccess.CanManageMembers(http.User, tenantId)) return Results.Forbid();
    try
    {
        var membership = await authentication.SetMembershipAsync(
            tenantId, request.UserName, request.Role, cancellationToken);
        return Results.Ok(membership);
    }
    catch (KeyNotFoundException exception)
    {
        return Results.NotFound(new { message = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { message = exception.Message });
    }
});

api.MapPost("/conversations", async (
    HttpContext http,
    CreateConversationRequest request,
    IConversationStore store,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    var conversation = await store.CreateAsync(
        identity.TenantId, identity.UserId, request.Title, cancellationToken);
    return Results.Created($"/api/v1/conversations/{conversation.Id}", conversation);
});

api.MapGet("/conversations/{conversationId}", async (
    HttpContext http,
    string conversationId,
    IConversationStore store,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    var conversation = await store.GetAsync(identity.TenantId, conversationId, cancellationToken);
    if (conversation is null) return Results.NotFound();
    var messages = await store.GetMessagesAsync(identity.TenantId, conversationId, cancellationToken);
    return Results.Ok(new { conversation, messages });
});

api.MapGet("/conversations/{conversationId}/context", async Task<IResult> (
    HttpContext http,
    string conversationId,
    AgentRuntime runtime,
    IOptions<OpenAiCompatibleOptions> modelOptions,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    try
    {
        var model = modelOptions.Value;
        return Results.Ok(await runtime.GetContextStatusAsync(
            identity.TenantId,
            conversationId,
            new ModelParameters(model.Model, model.MaxOutputTokens),
            cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

api.MapPost("/conversations/{conversationId}/compact", async Task<IResult> (
    HttpContext http,
    string conversationId,
    AgentRuntime runtime,
    IOptions<OpenAiCompatibleOptions> modelOptions,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    try
    {
        var model = modelOptions.Value;
        return Results.Ok(await runtime.CompactAsync(
            identity.TenantId,
            conversationId,
            new ModelParameters(model.Model, model.MaxOutputTokens),
            cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

api.MapPost("/conversations/{conversationId}/messages", async (
    HttpContext http,
    string conversationId,
    SendMessageRequest request,
    AgentRuntime runtime,
    IOptions<OpenAiCompatibleOptions> modelOptions,
    IImageInputProcessor imageProcessor,
    ISkillCatalog skillCatalog,
    IToolGateway toolGateway,
    CancellationToken cancellationToken) =>
{
    var identity = RequestIdentity.From(http);
    var configuredModel = modelOptions.Value;
    var model = request.Model ?? configuredModel.Model;
    var maxOutputTokens = request.MaxOutputTokens ?? configuredModel.MaxOutputTokens;
    var inputText = ComposeReferencedMessage(request.Text, request.References);
    var images = new List<ImagePart>();
    // 图片在进入模型前统一经过来源、大小、魔数、像素和文件根目录检查。
    foreach (var image in request.Images ?? [])
    {
        if (!Enum.TryParse<ImageSourceKind>(image.Kind, ignoreCase: true, out var kind))
            throw new BadHttpRequestException("Image kind must be HttpsUrl, FileReference, or DataUrl.");
        images.Add(await imageProcessor.ProcessAsync(new ImageSource(kind, image.Value), image.MediaType, cancellationToken));
    }
    var systemInstruction = request.SystemInstruction;
    // Skill 文本被明确标记为不可信，且只有依赖工具全部可用时才注入系统指令。
    foreach (var skillName in request.Skills ?? [])
    {
        var skill = await skillCatalog.GetAsync(skillName, cancellationToken)
            ?? throw new BadHttpRequestException($"Skill '{skillName}' was not found.");
        var missingTools = skill.RequiredTools.Except(toolGateway.Definitions.Select(tool => tool.Name), StringComparer.Ordinal).ToArray();
        if (missingTools.Length > 0)
            throw new BadHttpRequestException($"Skill '{skillName}' requires unavailable tools: {string.Join(", ", missingTools)}.");
        systemInstruction = string.Join("\n\n", new[]
        {
            systemInstruction,
            $"<skill name=\"{skill.Name}\" version=\"{skill.Version}\" trust=\"untrusted\">\n{skill.Instructions}\n</skill>"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    // NDJSON 每行都是一个完整事件；逐行 Flush 可让 CLI/UI 在模型完成前实时呈现增量。
    http.Response.StatusCode = StatusCodes.Status200OK;
    http.Response.ContentType = "application/x-ndjson; charset=utf-8";
    try
    {
        await foreach (var agentEvent in runtime.RunAsync(
                           new AgentRunRequest(
                               identity.TenantId,
                               identity.UserId,
                               conversationId,
                               inputText,
                               new ModelParameters(model, maxOutputTokens, request.Temperature, systemInstruction),
                               images),
                           cancellationToken))
        {
            await EventEnvelope.WriteAsync(
                http.Response.Body,
                EventEnvelope.From(agentEvent),
                cancellationToken);
            await http.Response.WriteAsync("\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }
    }
    // 响应头发出后无法改成 ProblemDetails，因此把运行期错误写成流中的 error 事件。
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
        var category = exception is MuAgentException known ? known.Category.ToString() : "Unhandled";
        await EventEnvelope.WriteAsync(
            http.Response.Body,
            new EventEnvelope("error", new { category, message = exception.Message }),
            cancellationToken);
        await http.Response.WriteAsync("\n", cancellationToken);
    }
});

api.MapGet("/skills", async (HttpContext http, ISkillCatalog skills, CancellationToken cancellationToken) =>
{
    _ = RequestIdentity.From(http);
    var discovered = await skills.DiscoverAsync(cancellationToken);
    return Results.Ok(discovered.Select(skill => new { skill.Name, skill.Description, skill.Version, skill.RequiredTools }));
});

api.MapGet("/skills/config", async (
    HttpContext http,
    FileSystemSkillCatalog skills,
    SkillConfigurationStore configuration,
    CancellationToken cancellationToken) =>
{
    _ = RequestIdentity.From(http);
    var settings = configuration.Snapshot();
    var entries = await skills.DiscoverAllAsync(cancellationToken);
    return Results.Ok(new
    {
        configurationPath = configuration.ConfigurationPath,
        directories = settings.Directories,
        skills = entries.Select(entry => new
        {
            entry.Manifest.Name,
            entry.Manifest.Description,
            entry.Manifest.Version,
            entry.Manifest.Directory,
            entry.Enabled
        })
    });
});

api.MapPost("/skills/directories", (
    SkillDirectoryRequest request,
    SkillConfigurationStore configuration) =>
    Results.Ok(new
    {
        directory = configuration.AddDirectory(request.Path),
        configurationPath = configuration.ConfigurationPath
    })).RequireAuthorization("system-admin");

api.MapDelete("/skills/directories", (
    string path,
    SkillConfigurationStore configuration) =>
    configuration.RemoveDirectory(path) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization("system-admin");

api.MapPut("/skills/{name}/enabled", async Task<IResult> (
    string name,
    EnabledRequest request,
    FileSystemSkillCatalog skills,
    SkillConfigurationStore configuration,
    CancellationToken cancellationToken) =>
{
    var exists = (await skills.DiscoverAllAsync(cancellationToken))
        .Any(entry => entry.Manifest.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (!exists) return Results.NotFound();
    configuration.SetEnabled(name, request.Enabled);
    return Results.Ok(new { name, request.Enabled, configurationPath = configuration.ConfigurationPath });
}).RequireAuthorization("system-admin");

api.MapPost("/skills/{name}/scripts/{script}", async (
    HttpContext http,
    string name,
    string script,
    RunScriptRequest request,
    ISkillCatalog skills,
    IScriptRunner runner,
    CancellationToken cancellationToken) =>
{
    _ = RequestIdentity.From(http);
    var skill = await skills.GetAsync(name, cancellationToken);
    if (skill is null) return Results.NotFound();
    var result = await runner.RunAsync(
        new ScriptRunRequest(skill, script, request.Arguments ?? [], request.Approved), cancellationToken);
    return Results.Ok(result);
});

api.MapGet("/mcp/{server}/tools", async (
    HttpContext http,
    string server,
    IMcpClientManager mcp,
    CancellationToken cancellationToken) =>
{
    _ = RequestIdentity.From(http);
    return Results.Ok(await mcp.ListToolsAsync(server, cancellationToken));
});

api.MapGet("/mcp", (HttpContext http, IMcpClientManager mcp) =>
{
    _ = RequestIdentity.From(http);
    return Results.Ok(new
    {
        configurationPath = mcp.ConfigurationPath,
        servers = mcp.ListServers().Select(profile => new
        {
            profile.Name,
            profile.Enabled,
            transport = profile.Transport.ToString(),
            profile.Url,
            profile.Command,
            profile.TimeoutSeconds,
            profile.AllowTools,
            profile.DenyTools
        })
    });
});

api.MapPost("/mcp", async (
    McpAddRequest request,
    IMcpClientManager mcp) =>
{
    var name = string.IsNullOrWhiteSpace(request.Name) ? DeriveMcpName(request.Url) : request.Name.Trim();
    var profile = await mcp.UpsertServerAsync(new McpServerProfile
    {
        Name = name,
        Enabled = request.Enabled,
        Transport = McpTransport.StreamableHttp,
        Url = request.Url,
        TimeoutSeconds = request.TimeoutSeconds
    });
    return Results.Ok(new
    {
        profile.Name,
        profile.Enabled,
        transport = profile.Transport.ToString(),
        profile.Url,
        configurationPath = mcp.ConfigurationPath
    });
}).RequireAuthorization("system-admin");

api.MapPut("/mcp/{name}/enabled", async Task<IResult> (
    string name,
    EnabledRequest request,
    IMcpClientManager mcp) =>
    await mcp.SetServerEnabledAsync(name, request.Enabled)
        ? Results.Ok(new { name, request.Enabled, configurationPath = mcp.ConfigurationPath })
        : Results.NotFound()).RequireAuthorization("system-admin");

api.MapDelete("/mcp/{name}", async Task<IResult> (string name, IMcpClientManager mcp) =>
    await mcp.RemoveServerAsync(name) ? Results.NoContent() : Results.NotFound())
    .RequireAuthorization("system-admin");

app.Run();

// 交互输入不回显；重定向输入时读取两行，便于服务器自动化管理但仍不接受命令行明文密码。
static string ReadConfirmedPassword()
{
    Console.Write("New password: ");
    var password = ReadSecret();
    Console.Write("Confirm password: ");
    var confirmation = ReadSecret();
    if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        throw new InvalidOperationException("两次输入的密码不一致。");
    return password;
}

static string ReadSecret()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
    var value = new StringBuilder();
    while (Console.ReadKey(intercept: true) is { } key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && value.Length > 0) value.Length--;
        else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
    }
    Console.WriteLine();
    return value.ToString();
}

static string? ComposeReferencedMessage(
    string? text,
    IReadOnlyList<ReferencedFileRequest>? references)
{
    if (references is not { Count: > 0 }) return text;
    if (references.Count > 200)
        throw new BadHttpRequestException("A message may reference at most 200 files.");

    var totalCharacters = 0;
    var builder = new StringBuilder(text ?? string.Empty);
    builder.AppendLine().AppendLine().AppendLine("<referenced_files trust=\"untrusted\">");
    foreach (var file in references)
    {
        if (string.IsNullOrWhiteSpace(file.Path) || file.Path.Length > 1_024)
            throw new BadHttpRequestException("Each referenced file must have a path of at most 1024 characters.");
        if (file.Content is null || file.Content.Length > 256 * 1024)
            throw new BadHttpRequestException($"Referenced file '{file.Path}' exceeds the per-file character limit.");
        totalCharacters = checked(totalCharacters + file.Content.Length);
        if (totalCharacters > 2 * 1024 * 1024)
            throw new BadHttpRequestException("Referenced files exceed the total character limit.");

        // 路径使用 JSON 字符串转义，文件内容保持原文；整段位于 User 消息中，不获得系统指令权限。
        builder.Append("<file path=").Append(JsonSerializer.Serialize(file.Path)).AppendLine(">");
        builder.AppendLine(file.Content).AppendLine("</file>");
    }
    builder.Append("</referenced_files>");
    return builder.ToString();
}

static string DeriveMcpName(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        throw new BadHttpRequestException("MCP URL must be absolute.");
    var name = new string(uri.Host
        .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '-')
        .ToArray());
    return string.IsNullOrWhiteSpace(name) ? "mcp-server" : name;
}

/// <summary>创建一个属于当前认证租户和用户的新会话。</summary>
public sealed record CreateConversationRequest(string? Title);
/// <summary>首次初始化管理员及默认租户所需数据；空密码表示本地开发用无密码账户。</summary>
public sealed record BootstrapRequest(string UserName, string Password, string TenantName);
/// <summary>登录凭据、可选租户选择和 Cookie 登录开关。</summary>
public sealed record LoginRequest(string UserName, string Password, string? TenantId = null, bool UseCookie = false);
/// <summary>系统管理员创建用户的请求。</summary>
public sealed record CreateUserRequest(string UserName, string Password, bool IsSystemAdmin = false);
/// <summary>系统管理员创建租户及指定所有者的请求。</summary>
public sealed record CreateTenantRequest(string Name, string? OwnerUserName = null);
/// <summary>创建或更新租户成员角色的请求。</summary>
public sealed record SetMembershipRequest(string UserName, string Role);
/// <summary>一次对话消息及其可选模型参数、图片和 Skill。</summary>
public sealed record SendMessageRequest(
    string? Text,
    string? Model = null,
    int? MaxOutputTokens = null,
    double? Temperature = null,
    string? SystemInstruction = null,
    IReadOnlyList<ImageInputRequest>? Images = null,
    IReadOnlyList<string>? Skills = null,
    IReadOnlyList<ReferencedFileRequest>? References = null);
/// <summary>由 CLI 读取并随消息上传的文本文件；Path 仅用于向模型标识来源。</summary>
public sealed record ReferencedFileRequest(string Path, string Content);
/// <summary>API 图片来源；Kind 对应 HttpsUrl、FileReference 或 DataUrl。</summary>
public sealed record ImageInputRequest(string Kind, string Value, string? MediaType = null);
/// <summary>执行 Skill 脚本的参数和显式批准状态。</summary>
public sealed record RunScriptRequest(IReadOnlyList<string>? Arguments = null, bool Approved = false);
/// <summary>动态添加 HTTP MCP 服务的请求。</summary>
public sealed record McpAddRequest(string Url, string? Name = null, bool Enabled = true, int TimeoutSeconds = 60);
/// <summary>统一的启用或禁用请求。</summary>
public sealed record EnabledRequest(bool Enabled);
/// <summary>当前登录用户对一次等待中的控制台调用作出的明确决定。</summary>
public sealed record CommandApprovalDecision(bool Approved);
/// <summary>添加 Skill 根目录的请求。</summary>
public sealed record SkillDirectoryRequest(string Path);

/// <summary>API 输出的 NDJSON 事件外壳，Type 是稳定的客户端判别字段。</summary>
public sealed record EventEnvelope(string Type, object Data)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(Stream stream, EventEnvelope value, CancellationToken cancellationToken = default) =>
        JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);

    public static EventEnvelope From(AgentEvent value) => value switch
    {
        TextDeltaEvent item => new("text_delta", item),
        ReasoningDeltaEvent item => new("reasoning_delta", item),
        ToolCallStartedEvent item => new("tool_call_started", item),
        ToolCallCompletedEvent item => new("tool_call_completed", item),
        CompactionStartedEvent item => new("compaction_started", item),
        CompactionCompletedEvent item => new("compaction_completed", item),
        UsageUpdatedEvent item => new("usage_updated", item),
        WarningEvent item => new("warning", item),
        CompletedEvent item => new("completed", item),
        _ => new("unknown", value)
    };
}

/// <summary>从验证后的声明提取的请求身份，不允许由客户端正文直接构造授权上下文。</summary>
public sealed record RequestIdentity(string TenantId, string UserId)
{
    public static RequestIdentity From(HttpContext context)
    {
        var tenantId = context.User.FindFirstValue("tenant_id");
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("Authenticated tenant and user claims are required.");
        }
        return new RequestIdentity(tenantId, userId);
    }
}

/// <summary>租户管理授权规则。</summary>
public static class TenantAccess
{
    /// <summary>系统管理员可管理任意租户；普通用户仅能以目标租户 Owner 身份管理成员。</summary>
    public static bool CanManageMembers(ClaimsPrincipal principal, string tenantId) =>
        principal.HasClaim("system_admin", "true") ||
        (string.Equals(principal.FindFirstValue("tenant_id"), tenantId, StringComparison.Ordinal) &&
         string.Equals(principal.FindFirstValue(ClaimTypes.Role), "Owner", StringComparison.Ordinal));
}
