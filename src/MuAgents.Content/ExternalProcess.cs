using System.Diagnostics;
using MuAgents.Abstractions;

namespace MuAgents.Content;

/// <summary>受控外部进程的退出码、标准输出和标准错误。</summary>
internal sealed record ProcessOutput(int ExitCode, string Output, string Error);

/// <summary>内容模块专用进程助手，负责无 Shell 启动、超时终止和根目录内临时工作区。</summary>
internal static class ExternalProcess
{
    public static async Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // 一次调用一个目录，进程退出或抛错后由 using 递归清理，且不会使用系统 Temp。
        using var temporaryDirectory = RuntimePaths.CreateTemporaryDirectory("content-processes");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = temporaryDirectory.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        RuntimePaths.ConfigureChildProcess(startInfo, temporaryDirectory.DirectoryPath);
        // ArgumentList 绕开 Shell 解析，文件名或参数中的特殊字符不会转化为额外命令。
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process could not be started.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new MuAgentException(
                MuAgentErrorCategory.ContentFailure,
                $"Required content executable '{executable}' is unavailable.",
                exception);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new MuAgentException(MuAgentErrorCategory.Timeout, $"Content process '{executable}' timed out.");
        }
        return new ProcessOutput(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }
}
