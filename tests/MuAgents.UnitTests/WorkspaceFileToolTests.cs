using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Tools;

namespace MuAgents.UnitTests;

/// <summary>验证模型能够真正创建项目文件，同时不能逃逸项目或写入 .muagent 状态。</summary>
public sealed class WorkspaceFileToolTests
{
    [Fact]
    public async Task AllowedMode_WritesAndListsProjectFile()
    {
        var directory = CreateWorkspace();
        try
        {
            var relativeFile = Path.GetRelativePath(
                RuntimePaths.ProjectDirectory,
                Path.Combine(directory, "game", "index.html"));
            var write = CreateWriteTool(CommandApprovalMode.Allowed);
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path = relativeFile,
                content = "<canvas id=\"game\"></canvas>"
            }));

            var result = await write.InvokeAsync(arguments.RootElement, Context());

            Assert.False(result.IsError);
            Assert.Equal("<canvas id=\"game\"></canvas>", await File.ReadAllTextAsync(Path.Combine(directory, "game", "index.html")));

            var list = new ListWorkspaceFilesTool(Options.Create(new WorkspaceFileOptions()));
            using var listArguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path = Path.GetRelativePath(RuntimePaths.ProjectDirectory, directory),
                recursive = true
            }));
            var listed = await list.InvokeAsync(listArguments.RootElement, Context());
            Assert.False(listed.IsError);
            Assert.Contains("index.html", listed.Content);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DeniedMode_DoesNotCreateFile()
    {
        var directory = CreateWorkspace();
        try
        {
            var target = Path.Combine(directory, "denied.txt");
            using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path = Path.GetRelativePath(RuntimePaths.ProjectDirectory, target),
                content = "must not be written"
            }));

            var result = await CreateWriteTool(CommandApprovalMode.Denied)
                .InvokeAsync(arguments.RootElement, Context());

            Assert.True(result.IsError);
            Assert.False(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".muagent/config/secret.txt")]
    public async Task Write_RejectsProtectedOrEscapingPaths(string path)
    {
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "blocked" }));

        var result = await CreateWriteTool(CommandApprovalMode.Allowed)
            .InvokeAsync(arguments.RootElement, Context());

        Assert.True(result.IsError);
    }

    private static WriteWorkspaceFileTool CreateWriteTool(CommandApprovalMode mode) => new(
        Options.Create(new WorkspaceFileOptions()),
        Options.Create(new CommandExecutionOptions { ApprovalMode = mode }),
        new CommandApprovalCoordinator());

    private static ToolExecutionContext Context() => new("tenant", "conversation", "user", ToolCallId: "call");

    private static string CreateWorkspace()
    {
        var directory = Path.Combine(RuntimePaths.ProjectDirectory, "artifacts", "workspace-tool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
