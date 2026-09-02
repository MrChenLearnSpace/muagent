using System.Diagnostics;
using MuAgents.Abstractions;

namespace MuAgents.Content;

internal sealed record ProcessOutput(int ExitCode, string Output, string Error);

internal static class ExternalProcess
{
    public static async Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
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
