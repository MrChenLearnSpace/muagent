using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Tools;

namespace MuAgents.UnitTests;

public sealed class CommandExecutionToolTests
{
    [Fact]
    public async Task DeniedMode_RejectsBeforeStartingProcess()
    {
        var tool = CreateTool(CommandApprovalMode.Denied, out _);
        using var arguments = JsonDocument.Parse("""{"command":"definitely-missing-command"}""");

        var result = await tool.InvokeAsync(arguments.RootElement, Context("denied"));

        Assert.True(result.IsError);
        Assert.Contains("denied", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequireApproval_ExecutesOnlyAfterMatchingUserApproves()
    {
        var tool = CreateTool(CommandApprovalMode.RequireApproval, out var approvals);
        using var arguments = CommandArguments("approval-ok");
        var context = Context("approved-call");

        var execution = tool.InvokeAsync(arguments.RootElement, context);
        Assert.False(approvals.Resolve("other-tenant", context.UserId!, context.ConversationId, context.ToolCallId!, true));
        Assert.True(await ResolveEventuallyAsync(approvals, context, approved: true));
        var result = await execution;

        Assert.False(result.IsError);
        Assert.Contains("approval-ok", result.Content);
    }

    [Fact]
    public async Task RequireApproval_DoesNotExecuteWhenUserRejects()
    {
        var tool = CreateTool(CommandApprovalMode.RequireApproval, out var approvals);
        using var arguments = CommandArguments("must-not-run");
        var context = Context("rejected-call");

        var execution = tool.InvokeAsync(arguments.RootElement, context);
        Assert.True(await ResolveEventuallyAsync(approvals, context, approved: false));
        var result = await execution;

        Assert.True(result.IsError);
        Assert.Contains("not approved", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-run\r\n", result.Content);
    }

    [Fact]
    public async Task AllowedMode_ExecutesWithoutInteractiveApproval()
    {
        var tool = CreateTool(CommandApprovalMode.Allowed, out _);
        using var arguments = CommandArguments("automatic-ok");

        var result = await tool.InvokeAsync(arguments.RootElement, Context("automatic"));

        Assert.False(result.IsError);
        Assert.Contains("automatic-ok", result.Content);
    }

    [Fact]
    public async Task WorkingDirectory_CannotEscapeProjectRoot()
    {
        var tool = CreateTool(CommandApprovalMode.Allowed, out _);
        var outside = Path.GetFullPath(Path.Combine(RuntimePaths.ProjectDirectory, ".."));
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            workingDirectory = outside
        }));

        var result = await tool.InvokeAsync(arguments.RootElement, Context("outside"));

        Assert.True(result.IsError);
        Assert.Contains("inside", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CommandAllowlist_DoesNotAcceptAPathThatOnlySharesTheFileName()
    {
        var approvals = new CommandApprovalCoordinator();
        var tool = new CommandExecutionTool(Options.Create(new CommandExecutionOptions
        {
            ApprovalMode = CommandApprovalMode.Allowed,
            AllowedCommands = [OperatingSystem.IsWindows() ? "cmd.exe" : "sh"]
        }), approvals);
        var disguisedPath = Path.Combine(RuntimePaths.ProjectDirectory, OperatingSystem.IsWindows() ? "cmd.exe" : "sh");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { command = disguisedPath }));

        var result = await tool.InvokeAsync(arguments.RootElement, Context("allowlist"));

        Assert.True(result.IsError);
        Assert.Contains("allowlist", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandExecutionTool CreateTool(
        CommandApprovalMode mode,
        out CommandApprovalCoordinator approvals)
    {
        approvals = new CommandApprovalCoordinator();
        return new CommandExecutionTool(Options.Create(new CommandExecutionOptions
        {
            ApprovalMode = mode,
            ApprovalTimeoutSeconds = 5,
            MaxExecutionSeconds = 5
        }), approvals);
    }

    private static JsonDocument CommandArguments(string marker) => JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        command = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
        arguments = OperatingSystem.IsWindows()
            ? new[] { "/d", "/c", $"echo {marker}" }
            : new[] { "-c", $"printf '%s' '{marker}'" }
    }));

    private static ToolExecutionContext Context(string callId) =>
        new("tenant", "conversation", "user", ToolCallId: callId);

    private static async Task<bool> ResolveEventuallyAsync(
        CommandApprovalCoordinator approvals,
        ToolExecutionContext context,
        bool approved)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (approvals.Resolve(
                    context.TenantId,
                    context.UserId!,
                    context.ConversationId,
                    context.ToolCallId!,
                    approved)) return true;
            await Task.Delay(10);
        }
        return false;
    }
}
