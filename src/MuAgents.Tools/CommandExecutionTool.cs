using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Tools;

/// <summary>控制模型能否启动项目内控制台进程的三档审批模式。</summary>
public enum CommandApprovalMode
{
    /// <summary>完全禁止执行；工具调用会得到安全拒绝结果。</summary>
    Denied,
    /// <summary>每一次调用都必须由当前登录用户显式批准，默认模式。</summary>
    RequireApproval,
    /// <summary>无需交互审批即可执行，适合可信且隔离良好的开发环境。</summary>
    Allowed
}

/// <summary>控制台工具的审批、命令白名单、超时和输出限制。</summary>
public sealed class CommandExecutionOptions
{
    /// <summary>全局审批模式；默认逐次审批，避免模型未经确认改变项目。</summary>
    public CommandApprovalMode ApprovalMode { get; set; } = CommandApprovalMode.RequireApproval;
    /// <summary>允许的可执行文件名或绝对路径；空集合表示不额外限制。</summary>
    public List<string> AllowedCommands { get; set; } = [];
    /// <summary>单次命令可请求的最大执行秒数。</summary>
    public int MaxExecutionSeconds { get; set; } = 120;
    /// <summary>逐次审批模式下等待用户决定的最长秒数。</summary>
    public int ApprovalTimeoutSeconds { get; set; } = 120;
    /// <summary>标准输出和标准错误合计返回给模型的最大字符数。</summary>
    public int MaxOutputCharacters { get; set; } = 48_000;
}

/// <summary>
/// 保存正在等待用户决定的控制台调用。键同时包含租户、用户、会话和调用 ID，
/// 因而一个已认证用户不能批准其他用户或其他租户的命令。
/// </summary>
public sealed class CommandApprovalCoordinator
{
    private readonly ConcurrentDictionary<ApprovalKey, TaskCompletionSource<bool>> _pending = new();

    /// <summary>注册一次待审批调用并等待客户端批准或拒绝。</summary>
    public async Task<bool> WaitForDecisionAsync(
        ToolExecutionContext context,
        string callId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.UserId)) return false;
        var key = new ApprovalKey(context.TenantId, context.UserId, context.ConversationId, callId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion)) return false;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>提交当前身份对指定调用的决定；不存在或不属于当前身份时返回 false。</summary>
    public bool Resolve(
        string tenantId,
        string userId,
        string conversationId,
        string callId,
        bool approved) =>
        _pending.TryGetValue(new ApprovalKey(tenantId, userId, conversationId, callId), out var completion) &&
        completion.TrySetResult(approved);

    private sealed record ApprovalKey(string TenantId, string UserId, string ConversationId, string CallId);
}

/// <summary>
/// 模型可调用的控制台执行工具。命令与参数分开传递，不经过隐式 Shell；若确实需要 Shell，
/// 模型必须显式调用 pwsh/bash/cmd 及其参数，使审批界面能够展示真实入口。
/// </summary>
public sealed class CommandExecutionTool(
    IOptions<CommandExecutionOptions> options,
    CommandApprovalCoordinator approvals) : IAgentTool, IManagesOwnToolTimeout
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Executable name or absolute path, for example dotnet, git, pwsh or bash." },
            "arguments": { "type": "array", "items": { "type": "string" }, "description": "Argument array passed without implicit shell parsing." },
            "workingDirectory": { "type": "string", "description": "Project-relative working directory. Defaults to the project root." },
            "timeoutSeconds": { "type": "integer", "minimum": 1, "description": "Requested timeout, capped by host policy." }
          },
          "required": [ "command" ],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    private readonly CommandExecutionOptions _options = options.Value;

    public ToolDefinition Definition { get; } = new(
        "local.execute_command",
        "Executes a console program in the MuAgents project directory. Pass the executable and each argument separately. Execution may be denied or require explicit user approval.",
        Schema,
        IsMutating: true);

    public async Task<ToolResult> InvokeAsync(
        JsonElement arguments,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (_options.ApprovalMode == CommandApprovalMode.Denied)
            return Error("Console command execution is denied by host policy.");
        if (!TryReadRequest(arguments, out var request, out var validationError)) return Error(validationError!);
        if (!IsAllowedCommand(request!.Command))
            return Error($"Command '{request.Command}' is not in the host allowlist.");
        if (_options.ApprovalMode == CommandApprovalMode.RequireApproval)
        {
            if (string.IsNullOrWhiteSpace(context.ToolCallId)) return Error("Console command call ID is missing.");
            using var approvalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            approvalTimeout.CancelAfter(TimeSpan.FromSeconds(_options.ApprovalTimeoutSeconds));
            try
            {
                if (!await approvals.WaitForDecisionAsync(context, context.ToolCallId, approvalTimeout.Token).ConfigureAwait(false))
                    return Error("Console command execution was not approved by the user.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Error($"Console command approval timed out after {_options.ApprovalTimeoutSeconds} seconds.");
            }
        }

        return await ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        using var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("commands");
        var startInfo = new ProcessStartInfo
        {
            FileName = request.Command,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
        CopyRequiredEnvironment(startInfo);
        RuntimePaths.ConfigureChildProcess(startInfo, temporaryDirectory.DirectoryPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return Error("Console process could not be started.");
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            return Error($"Console command timed out after {request.TimeoutSeconds} seconds.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var remaining = _options.MaxOutputCharacters;
        var limitedOutput = Take(output, ref remaining, out var outputTruncated);
        var limitedError = Take(error, ref remaining, out var errorTruncated);
        var result = new StringBuilder()
            .Append("Exit code: ").AppendLine(process.ExitCode.ToString())
            .AppendLine("Standard output:").AppendLine(limitedOutput)
            .AppendLine("Standard error:").Append(limitedError)
            .ToString();
        if (outputTruncated || errorTruncated) result += "\n[command output truncated]";
        return new ToolResult(result, process.ExitCode != 0, outputTruncated || errorTruncated);
    }

    private bool TryReadRequest(JsonElement root, out CommandRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (!root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(commandElement.GetString()))
        {
            error = "command is required.";
            return false;
        }
        var command = commandElement.GetString()!.Trim();
        if (command.Length > 1_024)
        {
            error = "command is too long.";
            return false;
        }

        var commandArguments = new List<string>();
        if (root.TryGetProperty("arguments", out var array))
        {
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 256)
            {
                error = "arguments must be an array containing at most 256 strings.";
                return false;
            }
            foreach (var value in array.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: <= 32_768 } item)
                {
                    error = "each command argument must be a string of at most 32768 characters.";
                    return false;
                }
                commandArguments.Add(item);
            }
        }

        string? configuredDirectory = null;
        if (root.TryGetProperty("workingDirectory", out var directoryElement))
        {
            if (directoryElement.ValueKind != JsonValueKind.String)
            {
                error = "workingDirectory must be a string.";
                return false;
            }
            configuredDirectory = directoryElement.GetString();
        }
        var workingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredDirectory) ? RuntimePaths.ProjectDirectory : configuredDirectory,
            RuntimePaths.ProjectDirectory);
        if (!IsWithinProject(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            error = "workingDirectory must be an existing directory inside the MuAgents project root.";
            return false;
        }
        var requestedTimeout = _options.MaxExecutionSeconds;
        if (root.TryGetProperty("timeoutSeconds", out var timeoutElement) && !timeoutElement.TryGetInt32(out requestedTimeout))
        {
            error = "timeoutSeconds must be an integer.";
            return false;
        }
        if (requestedTimeout <= 0)
        {
            error = "timeoutSeconds must be positive.";
            return false;
        }
        request = new CommandRequest(command, commandArguments, workingDirectory, Math.Min(requestedTimeout, _options.MaxExecutionSeconds));
        return true;
    }

    private bool IsAllowedCommand(string command) =>
        _options.AllowedCommands.Count == 0 ||
        _options.AllowedCommands.Any(allowed =>
        {
            var allowedContainsPath = Path.IsPathRooted(allowed) ||
                                      allowed.Contains(Path.DirectorySeparatorChar) ||
                                      allowed.Contains(Path.AltDirectorySeparatorChar);
            var commandContainsPath = Path.IsPathRooted(command) ||
                                      command.Contains(Path.DirectorySeparatorChar) ||
                                      command.Contains(Path.AltDirectorySeparatorChar);
            return allowedContainsPath
                ? Path.GetFullPath(allowed, RuntimePaths.ProjectDirectory)
                    .Equals(Path.GetFullPath(command, RuntimePaths.ProjectDirectory), PathComparison)
                : !commandContainsPath && allowed.Equals(command, PathComparison);
        });

    private static bool IsWithinProject(string path)
    {
        var relative = Path.GetRelativePath(RuntimePaths.ProjectDirectory, path);
        return relative != ".." && !Path.IsPathRooted(relative) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison);
    }

    private static void CopyRequiredEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "PATHEXT", "ComSpec", "LD_LIBRARY_PATH" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
        }
    }

    private static string Take(string value, ref int remaining, out bool truncated)
    {
        var count = Math.Min(Math.Max(0, remaining), value.Length);
        truncated = count < value.Length;
        remaining -= count;
        return value[..count];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static ToolResult Error(string message) => new(message, IsError: true);
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record CommandRequest(
        string Command,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        int TimeoutSeconds);
}
