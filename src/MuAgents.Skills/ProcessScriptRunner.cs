using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Skills;

public sealed class ProcessScriptRunner(IOptions<SkillOptions> options) : IScriptRunner
{
    private readonly SkillOptions _options = options.Value;

    public async Task<ScriptRunResult> RunAsync(
        ScriptRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_options.ScriptPolicy == ScriptExecutionPolicy.Denied)
            throw Security("Skill script execution is denied by policy.");
        if (_options.ScriptPolicy == ScriptExecutionPolicy.RequireApproval && !request.IsApproved)
            throw Security("Skill script execution requires explicit approval.");
        var script = FileSystemSkillCatalog.ResolveWithin(
            Path.Combine(request.Skill.Directory, "scripts"), request.ScriptPath);
        if (!File.Exists(script)) throw new FileNotFoundException("Skill script was not found.", script);
        var (runtime, prefixArguments) = Runtime(script);
        if (!_options.AllowedRuntimes.Contains(runtime, StringComparer.OrdinalIgnoreCase) ||
            (request.Skill.AllowedRuntimes.Count > 0 && !request.Skill.AllowedRuntimes.Contains(runtime, StringComparer.OrdinalIgnoreCase)))
            throw Security($"Script runtime '{runtime}' is not allowed.");

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"muagents-skill-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime,
                WorkingDirectory = temporaryDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in prefixArguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(script);
            foreach (var argument in request.Arguments) startInfo.ArgumentList.Add(argument);
            var inheritedPath = Environment.GetEnvironmentVariable("PATH");
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            startInfo.Environment.Clear();
            if (inheritedPath is not null) startInfo.Environment["PATH"] = inheritedPath;
            if (systemRoot is not null) startInfo.Environment["SystemRoot"] = systemRoot;
            startInfo.Environment["TEMP"] = temporaryDirectory;
            startInfo.Environment["TMP"] = temporaryDirectory;

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new MuAgentException(MuAgentErrorCategory.ToolFailure, "Script process could not start.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.ScriptTimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                throw new MuAgentException(MuAgentErrorCategory.Timeout, "Skill script timed out.");
            }
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            var truncated = output.Length > _options.MaxScriptOutputCharacters || error.Length > _options.MaxScriptOutputCharacters;
            await using var scriptStream = File.OpenRead(script);
            var hash = await SHA256.HashDataAsync(scriptStream, cancellationToken).ConfigureAwait(false);
            return new ScriptRunResult(
                process.ExitCode,
                Limit(output),
                Limit(error),
                truncated,
                Convert.ToHexString(hash).ToLowerInvariant());
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private (string Runtime, string[] PrefixArguments) Runtime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".ps1" => ("pwsh", ["-NoLogo", "-NoProfile", "-NonInteractive", "-File"]),
        ".sh" => ("bash", []),
        ".py" => ("python", []),
        ".js" or ".mjs" => ("node", []),
        ".dll" => ("dotnet", []),
        _ => throw Security("Unsupported skill script type.")
    };

    private string Limit(string value) => value.Length <= _options.MaxScriptOutputCharacters
        ? value
        : value[.._options.MaxScriptOutputCharacters];
    private static MuAgentException Security(string message) => new(MuAgentErrorCategory.SecurityDenied, message);
}
