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

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "muagents.settings.json"), optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "muagents.settings.local.json"), optional: true, reloadOnChange: true);
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
var dataProtectionPath = Path.IsPathRooted(configuredAuthentication.DataProtectionKeysPath)
    ? configuredAuthentication.DataProtectionKeysPath
    : Path.Combine(AppContext.BaseDirectory, configuredAuthentication.DataProtectionKeysPath);
Directory.CreateDirectory(dataProtectionPath);
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("MuAgents");
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
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
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await app.Services.GetRequiredService<IConversationStore>().InitializeAsync();
await app.Services.GetRequiredService<IIdentityStore>().InitializeAsync();

var publicApi = app.MapGroup("/api/v1");
publicApi.MapGet("/health", () => Results.Ok(new { status = "ok" }));
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
api.MapGet("/auth/tenants", async (
    HttpContext http,
    IIdentityStore store,
    CancellationToken cancellationToken) =>
{
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
    var images = new List<ImagePart>();
    foreach (var image in request.Images ?? [])
    {
        if (!Enum.TryParse<ImageSourceKind>(image.Kind, ignoreCase: true, out var kind))
            throw new BadHttpRequestException("Image kind must be HttpsUrl, FileReference, or DataUrl.");
        images.Add(await imageProcessor.ProcessAsync(new ImageSource(kind, image.Value), image.MediaType, cancellationToken));
    }
    var systemInstruction = request.SystemInstruction;
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

    http.Response.StatusCode = StatusCodes.Status200OK;
    http.Response.ContentType = "application/x-ndjson; charset=utf-8";
    try
    {
        await foreach (var agentEvent in runtime.RunAsync(
                           new AgentRunRequest(
                               identity.TenantId,
                               identity.UserId,
                               conversationId,
                               request.Text,
                               new ModelParameters(model, maxOutputTokens, request.Temperature, systemInstruction),
                               images),
                           cancellationToken))
        {
            await JsonSerializer.SerializeAsync(
                http.Response.Body,
                EventEnvelope.From(agentEvent),
                cancellationToken: cancellationToken);
            await http.Response.WriteAsync("\n", cancellationToken);
            await http.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
    {
        var category = exception is MuAgentException known ? known.Category.ToString() : "Unhandled";
        await JsonSerializer.SerializeAsync(
            http.Response.Body,
            new EventEnvelope("error", new { category, message = exception.Message }),
            cancellationToken: cancellationToken);
        await http.Response.WriteAsync("\n", cancellationToken);
    }
});

api.MapGet("/skills", async (HttpContext http, ISkillCatalog skills, CancellationToken cancellationToken) =>
{
    _ = RequestIdentity.From(http);
    var discovered = await skills.DiscoverAsync(cancellationToken);
    return Results.Ok(discovered.Select(skill => new { skill.Name, skill.Description, skill.Version, skill.RequiredTools }));
});

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

app.Run();

public sealed record CreateConversationRequest(string? Title);
public sealed record BootstrapRequest(string UserName, string Password, string TenantName);
public sealed record LoginRequest(string UserName, string Password, string? TenantId = null, bool UseCookie = false);
public sealed record CreateUserRequest(string UserName, string Password, bool IsSystemAdmin = false);
public sealed record CreateTenantRequest(string Name, string? OwnerUserName = null);
public sealed record SetMembershipRequest(string UserName, string Role);
public sealed record SendMessageRequest(
    string? Text,
    string? Model = null,
    int? MaxOutputTokens = null,
    double? Temperature = null,
    string? SystemInstruction = null,
    IReadOnlyList<ImageInputRequest>? Images = null,
    IReadOnlyList<string>? Skills = null);
public sealed record ImageInputRequest(string Kind, string Value, string? MediaType = null);
public sealed record RunScriptRequest(IReadOnlyList<string>? Arguments = null, bool Approved = false);

public sealed record EventEnvelope(string Type, object Data)
{
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

public static class TenantAccess
{
    public static bool CanManageMembers(ClaimsPrincipal principal, string tenantId) =>
        principal.HasClaim("system_admin", "true") ||
        (string.Equals(principal.FindFirstValue("tenant_id"), tenantId, StringComparison.Ordinal) &&
         string.Equals(principal.FindFirstValue(ClaimTypes.Role), "Owner", StringComparison.Ordinal));
}
