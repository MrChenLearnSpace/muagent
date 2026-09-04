using System.Diagnostics;

namespace MuAgents.Abstractions;

/// <summary>
/// 集中管理项目级运行路径。启动时的当前目录是项目根目录，所有可写状态都进入其 .muagent 子目录。
/// </summary>
public static class RuntimePaths
{
    private static int _processInitialized;

    /// <summary>程序二进制所在目录，只用于读取随程序发布的默认配置和资源。</summary>
    public static string ApplicationDirectory { get; } = Path.GetFullPath(AppContext.BaseDirectory);

    /// <summary>启动命令所在的项目目录；文件引用和扩展相对路径均以此为基准。</summary>
    public static string ProjectDirectory { get; } = Path.GetFullPath(Directory.GetCurrentDirectory());

    /// <summary>项目的 MuAgent 状态根目录，即 &lt;项目目录&gt;/.muagent。</summary>
    public static string RootDirectory { get; } = Path.Combine(ProjectDirectory, ".muagent");

    /// <summary>默认持久数据目录。</summary>
    public static string DataDirectory => ResolveWritePath("data", "runtime data directory");

    /// <summary>
    /// 在任何配置、HTTP 或文件组件启动前固定进程环境。除了当前工作目录，系统临时目录、
    /// .NET CLI 主目录、NuGet 缓存和单文件解压目录也全部改到项目的 .muagent 内。
    /// </summary>
    public static void InitializeProcessEnvironment()
    {
        if (Interlocked.Exchange(ref _processInitialized, 1) != 0) return;

        Directory.CreateDirectory(RootDirectory);
        Directory.SetCurrentDirectory(ProjectDirectory);
        var temporaryDirectory = EnsureDirectory(Path.Combine("data", "temp", "process"), "process temporary directory");
        var dotnetHome = EnsureDirectory(Path.Combine("data", "dotnet", "home"), ".NET CLI home directory");
        var nugetPackages = EnsureDirectory(Path.Combine("data", "nuget", "packages"), "NuGet packages directory");
        var nugetCache = EnsureDirectory(Path.Combine("data", "nuget", "http-cache"), "NuGet HTTP cache directory");
        var bundleDirectory = EnsureDirectory(Path.Combine("data", "temp", "dotnet-bundle"), ".NET bundle directory");

        SetPortableEnvironmentVariable("TEMP", temporaryDirectory);
        SetPortableEnvironmentVariable("TMP", temporaryDirectory);
        SetPortableEnvironmentVariable("TMPDIR", temporaryDirectory);
        SetPortableEnvironmentVariable("DOTNET_CLI_HOME", dotnetHome);
        SetPortableEnvironmentVariable("NUGET_PACKAGES", nugetPackages);
        SetPortableEnvironmentVariable("NUGET_HTTP_CACHE_PATH", nugetCache);
        SetPortableEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR", bundleDirectory);
    }

    /// <summary>
    /// 给受控子进程重新写入便携环境。调用方传入本次任务的独立临时目录；即使 MCP 配置
    /// 或父进程环境给出了系统盘路径，以下固定变量也会在进程启动前被覆盖。
    /// </summary>
    public static void ConfigureChildProcess(ProcessStartInfo startInfo, string temporaryDirectory)
    {
        var normalizedTemporaryDirectory = ResolveWritePath(temporaryDirectory, "child process temporary directory");
        Directory.CreateDirectory(normalizedTemporaryDirectory);
        startInfo.Environment["TEMP"] = normalizedTemporaryDirectory;
        startInfo.Environment["TMP"] = normalizedTemporaryDirectory;
        startInfo.Environment["TMPDIR"] = normalizedTemporaryDirectory;
        startInfo.Environment["DOTNET_CLI_HOME"] = EnsureDirectory(Path.Combine("data", "dotnet", "home"), ".NET CLI home directory");
        startInfo.Environment["NUGET_PACKAGES"] = EnsureDirectory(Path.Combine("data", "nuget", "packages"), "NuGet packages directory");
        startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = EnsureDirectory(Path.Combine("data", "nuget", "http-cache"), "NuGet HTTP cache directory");
        startInfo.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = EnsureDirectory(
            Path.Combine("data", "temp", "dotnet-bundle"),
            ".NET bundle directory");
    }

    /// <summary>把可写路径规范化为绝对路径，并验证结果仍位于项目的 .muagent 内。</summary>
    public static string ResolveWritePath(string configuredPath, string settingName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new MuAgentException(MuAgentErrorCategory.Configuration, $"{settingName} is required.");
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(RootDirectory, configuredPath));
        // 不能只检查字符串前缀，例如 C:\App2 会错误匹配 C:\App；相对路径判断可识别真实目录边界。
        var relative = Path.GetRelativePath(RootDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
        {
            throw new MuAgentException(
                MuAgentErrorCategory.Configuration,
                $"{settingName} must stay inside the project state root '{RootDirectory}'.");
        }
        return fullPath;
    }

    /// <summary>在项目 .muagent/data/temp/&lt;category&gt; 下创建具有随机名称的一次性目录。</summary>
    public static RuntimeTemporaryDirectory CreateTemporaryDirectory(string category)
    {
        if (string.IsNullOrWhiteSpace(category) ||
            category.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            category.Contains(Path.DirectorySeparatorChar) ||
            category.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Temporary directory category must be a single valid path segment.", nameof(category));
        }
        // category 只允许一个路径段，防止调用方借此改变临时目录的固定父级。
        var parent = ResolveWritePath(Path.Combine("data", "temp", category), "temporary directory");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new RuntimeTemporaryDirectory(path);
    }

    private static string EnsureDirectory(string relativePath, string settingName)
    {
        var path = ResolveWritePath(relativePath, settingName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SetPortableEnvironmentVariable(string name, string value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

/// <summary>表示项目 .muagent 目录内的一次性工作目录，释放时递归清理其中内容。</summary>
public sealed class RuntimeTemporaryDirectory(string directoryPath) : IDisposable
{
    /// <summary>已经完成安全解析并创建的绝对目录。</summary>
    public string DirectoryPath { get; } = directoryPath;

    /// <summary>清理临时目录；调用方应通过 using 保证异常路径也会执行。</summary>
    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
