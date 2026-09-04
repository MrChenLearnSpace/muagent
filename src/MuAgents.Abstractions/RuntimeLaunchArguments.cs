namespace MuAgents.Abstractions;

/// <summary>
/// 表示应用入口已经消费过的通用启动参数。项目目录参数不会继续传给 ASP.NET Core
/// 或 CLI 自己的参数解析器，避免未知参数产生歧义。
/// </summary>
public sealed record RuntimeLaunchArguments(string ProjectDirectory, string[] RemainingArguments)
{
    /// <summary>
    /// 解析 <c>-d &lt;目录&gt;</c> 或 <c>--directory &lt;目录&gt;</c>。未指定时使用启动命令的
    /// 当前目录；相对路径也始终相对于该启动目录，而不是程序二进制所在目录。
    /// </summary>
    public static RuntimeLaunchArguments Parse(string[] args, string? launchDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var baseDirectory = Path.GetFullPath(launchDirectory ?? Directory.GetCurrentDirectory());
        var remaining = new List<string>(args.Length);
        string? configuredDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            string? value = null;
            if (argument.Equals("-d", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--directory", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                    throw new ArgumentException($"启动参数 {argument} 后必须提供项目路径。", nameof(args));
                value = args[++index];
            }
            else if (argument.StartsWith("-d=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[3..];
            }
            else if (argument.StartsWith("--directory=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[12..];
            }

            if (value is null)
            {
                remaining.Add(argument);
                continue;
            }

            if (configuredDirectory is not null)
                throw new ArgumentException("项目路径参数 -d/--directory 只能指定一次。", nameof(args));
            configuredDirectory = value.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(configuredDirectory))
                throw new ArgumentException("项目路径不能为空。", nameof(args));
        }

        var projectDirectory = Path.GetFullPath(configuredDirectory ?? baseDirectory, baseDirectory);
        if (!Directory.Exists(projectDirectory))
            throw new DirectoryNotFoundException($"项目路径不存在：{projectDirectory}");
        return new RuntimeLaunchArguments(projectDirectory, remaining.ToArray());
    }
}
