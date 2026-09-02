using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Core;
using MuAgents.Hosting;
using MuAgents.OpenAI;
using MuAgents.Mcp;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "muagents.settings.json"), optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "muagents.settings.local.json"), optional: true, reloadOnChange: true);
builder.Services.AddMuAgents(builder.Configuration);
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler();

await app.Services.GetRequiredService<IConversationStore>().InitializeAsync();

var api = app.MapGroup("/api/v1");
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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
        var tenantId = context.Request.Headers["X-Tenant-Id"].ToString();
        var userId = context.Request.Headers["X-User-Id"].ToString();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
        {
            throw new BadHttpRequestException("X-Tenant-Id and X-User-Id headers are required.");
        }
        return new RequestIdentity(tenantId, userId);
    }
}
