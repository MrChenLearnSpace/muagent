using System.Text;

/// <summary>一条可被帮助页和 Tab 补全共同使用的斜杠命令定义。</summary>
public sealed record SlashCommandDefinition(string Name, string Usage, string Description);

/// <summary>
/// 维护斜杠命令的唯一清单，并实现不依赖终端的前缀匹配；命令分派、帮助和补全因而不会各自维护一份列表。
/// </summary>
public static class SlashCommandCatalog
{
    public static IReadOnlyList<SlashCommandDefinition> Commands { get; } =
    [
        new("/help", "/help", "显示本帮助"),
        new("/model", "/model", "显示当前模型、协议、端点和能力"),
        new("/status", "/status", "显示连接、配置、会话和上下文状态"),
        new("/compact", "/compact", "把当前会话压缩到最大上下文的 1/3 以内"),
        new("/new", "/new [标题]", "创建新会话（文件引用继续保留）"),
        new("/add", "/add [文件或目录]", "递归添加文件引用；省略路径等同 /add ."),
        new("/context", "/context", "列出当前引用文件"),
        new("/files", "/files", "列出当前引用文件（/context 的别名）"),
        new("/remove", "/remove <路径|all>", "移除文件、目录或全部引用"),
        new("/mcp", "/mcp", "查看 MCP 服务和配置文件路径"),
        new("/mcp_list", "/mcp_list", "查看 MCP（/mcp 的别名）"),
        new("/mcp_add", "/mcp_add [名称] <url>", "添加或更新 HTTP MCP 服务"),
        new("/mcp_remove", "/mcp_remove <名称>", "删除 MCP 配置"),
        new("/mcp_enable", "/mcp_enable <名称>", "启用 MCP"),
        new("/mcp_disable", "/mcp_disable <名称>", "禁用 MCP"),
        new("/mcp_tools", "/mcp_tools <名称>", "查看 MCP 暴露的工具"),
        new("/skills", "/skills", "查看 Skill、状态、目录和配置文件路径"),
        new("/skills_list", "/skills_list", "查看 Skill（/skills 的别名）"),
        new("/skills_add", "/skills_add <目录>", "添加 Skill 或 Skill 根目录"),
        new("/skills_remove", "/skills_remove <目录>", "从扫描配置中移除目录，不删除文件"),
        new("/skills_enable", "/skills_enable <名称>", "启用 Skill"),
        new("/skills_disable", "/skills_disable <名称>", "禁用 Skill"),
        new("/exit", "/exit", "退出"),
        new("/quit", "/quit", "退出（/exit 的别名）")
    ];

    /// <summary>对光标前的第一个单词补全；已有参数时不改写输入，避免误改文件路径或 URL。</summary>
    public static SlashCompletion Complete(string input)
    {
        if (string.IsNullOrEmpty(input) || input[0] != '/' || input.Any(char.IsWhiteSpace))
            return new SlashCompletion(input, []);

        var candidates = Commands
            .Where(command => command.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return new SlashCompletion(input, []);

        if (candidates.Length == 1)
            return new SlashCompletion(candidates[0].Name + " ", candidates);

        // 多个候选时先扩展到共同前缀；若无法继续扩展，交互层会把候选清单打印出来。
        var commonPrefix = candidates[0].Name;
        foreach (var candidate in candidates.Skip(1))
        {
            var length = 0;
            while (length < commonPrefix.Length && length < candidate.Name.Length &&
                   char.ToUpperInvariant(commonPrefix[length]) == char.ToUpperInvariant(candidate.Name[length]))
                length++;
            commonPrefix = commonPrefix[..length];
        }
        return new SlashCompletion(commonPrefix.Length > input.Length ? commonPrefix : input, candidates);
    }
}

/// <summary>Tab 补全后的文本以及可显示的匹配命令。</summary>
public sealed record SlashCompletion(string Text, IReadOnlyList<SlashCommandDefinition> Candidates);

/// <summary>
/// 提供轻量的交互行编辑：斜杠命令 Tab 补全、候选展示、左右移动及历史记录。
/// 标准输入被重定向时退回 ReadLine，确保管道和自动化脚本仍可使用。
/// </summary>
internal static class SlashCommandLine
{
    private static readonly List<string> History = [];

    public static string? ReadLine(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected) return Console.ReadLine();

        var buffer = new StringBuilder();
        var cursor = 0;
        var renderedLength = 0;
        var historyIndex = History.Count;

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                var value = buffer.ToString();
                if (!string.IsNullOrWhiteSpace(value) && (History.Count == 0 || History[^1] != value)) History.Add(value);
                return value;
            }
            if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                Console.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.D && key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0)
            {
                Console.WriteLine();
                return null;
            }

            switch (key.Key)
            {
                case ConsoleKey.Backspace when cursor > 0:
                    buffer.Remove(--cursor, 1);
                    break;
                case ConsoleKey.Delete when cursor < buffer.Length:
                    buffer.Remove(cursor, 1);
                    break;
                case ConsoleKey.LeftArrow when cursor > 0:
                    cursor--;
                    break;
                case ConsoleKey.RightArrow when cursor < buffer.Length:
                    cursor++;
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    break;
                case ConsoleKey.End:
                    cursor = buffer.Length;
                    break;
                case ConsoleKey.UpArrow:
                    if (History.Count > 0 && historyIndex > 0)
                    {
                        historyIndex--;
                        ReplaceBuffer(buffer, History[historyIndex], ref cursor);
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (historyIndex < History.Count - 1)
                    {
                        historyIndex++;
                        ReplaceBuffer(buffer, History[historyIndex], ref cursor);
                    }
                    else if (historyIndex < History.Count)
                    {
                        historyIndex = History.Count;
                        ReplaceBuffer(buffer, string.Empty, ref cursor);
                    }
                    break;
                case ConsoleKey.Tab:
                    if (cursor == buffer.Length)
                    {
                        var original = buffer.ToString();
                        var completion = SlashCommandCatalog.Complete(original);
                        ReplaceBuffer(buffer, completion.Text, ref cursor);
                        if (completion.Candidates.Count > 1 && completion.Text.Equals(original, StringComparison.Ordinal))
                        {
                            ClearRenderedLine(prompt, renderedLength);
                            Console.WriteLine();
                            foreach (var candidate in completion.Candidates)
                                Console.WriteLine($"  {candidate.Usage,-28} {candidate.Description}");
                            renderedLength = 0;
                        }
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                    }
                    break;
            }

            Render(prompt, buffer, cursor, ref renderedLength);
        }
    }

    private static void ReplaceBuffer(StringBuilder buffer, string value, ref int cursor)
    {
        buffer.Clear();
        buffer.Append(value);
        cursor = buffer.Length;
    }

    private static void ClearRenderedLine(string prompt, int renderedLength)
    {
        Console.Write('\r');
        Console.Write(new string(' ', prompt.Length + renderedLength));
        Console.Write('\r');
    }

    private static void Render(string prompt, StringBuilder buffer, int cursor, ref int renderedLength)
    {
        Console.Write('\r');
        Console.Write(prompt);
        Console.Write(buffer);
        if (renderedLength > buffer.Length) Console.Write(new string(' ', renderedLength - buffer.Length));
        Console.Write('\r');
        Console.Write(prompt);
        if (cursor > 0) Console.Write(buffer.ToString(0, cursor));
        renderedLength = buffer.Length;
    }
}
