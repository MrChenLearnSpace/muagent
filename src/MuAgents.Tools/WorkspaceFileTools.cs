using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Tools;

/// <summary>模型列举和写入项目文件时的容量限制。</summary>
public sealed class WorkspaceFileOptions
{
    /// <summary>是否向模型公开项目文件工具。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>单次写入允许的最大字符数。</summary>
    public int MaxWriteCharacters { get; set; } = 2_000_000;
    /// <summary>单次目录列举允许返回的最大条目数。</summary>
    public int MaxListEntries { get; set; } = 2_000;
}

/// <summary>列举 APP 项目根内文件，帮助模型先观察现有结构再决定创建或修改哪些文件。</summary>
public sealed class ListWorkspaceFilesTool(IOptions<WorkspaceFileOptions> options) : IAgentTool
{
    private readonly WorkspaceFileOptions _options = options.Value;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Project-relative directory. Defaults to the project root." },
            "recursive": { "type": "boolean", "description": "Whether to include descendants. Defaults to true." }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public ToolDefinition Definition { get; } = new(
        "local.list_files",
        "Lists files and directories inside the MuAgents project. Use this before editing an existing project.",
        Schema);

    public Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Task.FromResult(Error("Workspace file tools are disabled by host policy."));
        var configuredPath = arguments.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString()
            : null;
        var directory = WorkspacePath.Resolve(configuredPath ?? ".", requireExisting: true, allowStateDirectory: false);
        if (!Directory.Exists(directory)) return Task.FromResult(Error("path must be an existing project directory."));
        var recursive = !arguments.TryGetProperty("recursive", out var recursiveElement) ||
                        recursiveElement.ValueKind == JsonValueKind.True;
        if (arguments.TryGetProperty("recursive", out recursiveElement) &&
            recursiveElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            return Task.FromResult(Error("recursive must be a boolean."));

        var entries = new List<string>();
        var pending = new Stack<string>();
        pending.Push(directory);
        var truncated = false;
        while (pending.Count > 0 && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(current)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                entries.Add($"[unavailable] {Path.GetRelativePath(RuntimePaths.ProjectDirectory, current)}");
                continue;
            }

            var childDirectories = new List<string>();
            foreach (var child in children)
            {
                if (WorkspacePath.IsIgnored(child)) continue;
                var isDirectory = Directory.Exists(child);
                entries.Add(Path.GetRelativePath(RuntimePaths.ProjectDirectory, child).Replace('\\', '/') +
                            (isDirectory ? "/" : string.Empty));
                if (entries.Count >= _options.MaxListEntries)
                {
                    truncated = true;
                    break;
                }
                if (recursive && isDirectory) childDirectories.Add(child);
            }
            for (var index = childDirectories.Count - 1; index >= 0; index--) pending.Push(childDirectories[index]);
        }

        var content = entries.Count == 0 ? "[project directory is empty]" : string.Join('\n', entries);
        if (truncated) content += $"\n[list truncated at {_options.MaxListEntries} entries]";
        return Task.FromResult(new ToolResult(content, IsTruncated: truncated));
    }

    private static ToolResult Error(string message) => new(message, IsError: true);
}

/// <summary>
/// 在 APP 项目根内创建或覆盖 UTF-8 文本文件。写入审批复用控制台的三档本地执行策略，
/// 因此 Denied 不写入、RequireApproval 等待当前用户、Allowed 才会自动落盘。
/// </summary>
public sealed class WriteWorkspaceFileTool(
    IOptions<WorkspaceFileOptions> options,
    IOptions<CommandExecutionOptions> executionOptions,
    CommandApprovalCoordinator approvals) : IAgentTool, IManagesOwnToolTimeout
{
    private readonly WorkspaceFileOptions _options = options.Value;
    private readonly CommandExecutionOptions _executionOptions = executionOptions.Value;
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Project-relative file path. Parent directories are created automatically." },
            "content": { "type": "string", "description": "Complete UTF-8 text content to write." },
            "overwrite": { "type": "boolean", "description": "Allow replacing an existing file. Defaults to true." }
          },
          "required": [ "path", "content" ],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public ToolDefinition Definition { get; } = new(
        "local.write_file",
        "Creates or replaces a UTF-8 text file inside the MuAgents project. Parent directories are created automatically. Use it to implement requested code instead of only printing code in chat.",
        Schema,
        IsMutating: true);

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return Error("Workspace file tools are disabled by host policy.");
        if (_executionOptions.ApprovalMode == CommandApprovalMode.Denied)
            return Error("Workspace writes are denied by host policy.");
        if (!TryRead(arguments, out var request, out var validationError)) return Error(validationError!);
        if (_executionOptions.ApprovalMode == CommandApprovalMode.RequireApproval)
        {
            if (string.IsNullOrWhiteSpace(context.ToolCallId)) return Error("Workspace write call ID is missing.");
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(_executionOptions.ApprovalTimeoutSeconds));
            try
            {
                if (!await approvals.WaitForDecisionAsync(context, context.ToolCallId, approvalTimeout.Token).ConfigureAwait(false))
                    return Error("Workspace write was not approved by the user.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Error($"Workspace write approval timed out after {_executionOptions.ApprovalTimeoutSeconds} seconds.");
            }
        }

        var existed = File.Exists(request!.Path);
        if (existed && !request.Overwrite) return Error("Target file already exists and overwrite is false.");
        var parent = Path.GetDirectoryName(request.Path)!;
        WorkspacePath.EnsureNoReparsePoint(parent);
        Directory.CreateDirectory(parent);
        WorkspacePath.EnsureNoReparsePoint(parent);

        using var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("workspace-writes");
        var temporaryFile = Path.Combine(temporaryDirectory.DirectoryPath, "content.tmp");
        await File.WriteAllTextAsync(
            temporaryFile,
            request.Content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryFile, request.Path, overwrite: request.Overwrite);
        return new ToolResult(JsonSerializer.Serialize(new
        {
            path = Path.GetRelativePath(RuntimePaths.ProjectDirectory, request.Path).Replace('\\', '/'),
            characters = request.Content.Length,
            bytes = Encoding.UTF8.GetByteCount(request.Content),
            operation = existed ? "overwritten" : "created"
        }));
    }

    private bool TryRead(JsonElement arguments, out WriteRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (!arguments.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            error = "path is required.";
            return false;
        }
        if (!arguments.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
        {
            error = "content is required and must be a string.";
            return false;
        }
        var content = contentElement.GetString()!;
        if (content.Length > _options.MaxWriteCharacters)
        {
            error = $"content exceeds the {_options.MaxWriteCharacters} character limit.";
            return false;
        }
        var overwrite = !arguments.TryGetProperty("overwrite", out var overwriteElement) ||
                        overwriteElement.ValueKind == JsonValueKind.True;
        if (arguments.TryGetProperty("overwrite", out overwriteElement) &&
            overwriteElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            error = "overwrite must be a boolean.";
            return false;
        }
        try
        {
            var path = WorkspacePath.Resolve(pathElement.GetString()!, requireExisting: false, allowStateDirectory: false);
            if (Directory.Exists(path))
            {
                error = "path points to a directory.";
                return false;
            }
            request = new WriteRequest(path, content, overwrite);
            return true;
        }
        catch (MuAgentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static ToolResult Error(string message) => new(message, IsError: true);
    private sealed record WriteRequest(string Path, string Content, bool Overwrite);
}

/// <summary>项目文件工具共享的路径边界和生成目录排除规则。</summary>
internal static class WorkspacePath
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".muagent", ".git", "bin", "obj", "node_modules"
    };

    public static string Resolve(string configuredPath, bool requireExisting, bool allowStateDirectory)
    {
        var fullPath = Path.GetFullPath(configuredPath, RuntimePaths.ProjectDirectory);
        var relative = Path.GetRelativePath(RuntimePaths.ProjectDirectory, fullPath);
        if (relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "Path must stay inside the MuAgents project root.");
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (!allowStateDirectory && firstSegment.Equals(".muagent", StringComparison.OrdinalIgnoreCase))
            throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "The project .muagent state directory cannot be accessed by workspace tools.");
        if (requireExisting && !File.Exists(fullPath) && !Directory.Exists(fullPath))
            throw new MuAgentException(MuAgentErrorCategory.ContentFailure, "Workspace path does not exist.");
        EnsureNoReparsePoint(Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? RuntimePaths.ProjectDirectory);
        return fullPath;
    }

    public static bool IsIgnored(string path) =>
        IgnoredNames.Contains(Path.GetFileName(path)) ||
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    public static void EnsureNoReparsePoint(string path)
    {
        var current = RuntimePaths.ProjectDirectory;
        var relative = Path.GetRelativePath(current, path);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new MuAgentException(MuAgentErrorCategory.SecurityDenied, "Workspace paths cannot traverse symbolic links or junctions.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
