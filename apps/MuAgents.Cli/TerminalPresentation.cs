using System.Text;
using System.Text.RegularExpressions;

/// <summary>集中定义 CLI 颜色，输出重定向或设置 NO_COLOR 时自动退回纯文本。</summary>
internal static class TerminalTheme
{
    private const string Reset = "\u001b[0m";
    private static bool ColorEnabled =>
        !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    public static void WriteUserPrompt(string prompt) => WriteStyled(prompt, "\u001b[1;96m");
    public static void WriteUserInput(string text) => WriteStyled(text, "\u001b[1;97m");
    public static void WriteAgentPrompt() => WriteStyled("AGENT › ", "\u001b[1;92m");
    public static void WriteTool(string text) => WriteStyled(text, "\u001b[93m");
    public static void WriteHint(string text) => WriteStyled(text, "\u001b[2;90m");

    public static string Style(string text, string ansiStyle, bool? colorEnabled = null) =>
        colorEnabled ?? ColorEnabled ? ansiStyle + text + Reset : text;

    private static void WriteStyled(string text, string style) => Console.Write(Style(text, style));
}

/// <summary>
/// 将模型返回的常用 Markdown 转成 ANSI 终端样式。它不是完整 Markdown 排版引擎，
/// 但覆盖标题、粗体、行内代码、链接、列表、引用、分隔线、表格和围栏代码块。
/// </summary>
public static partial class TerminalMarkdownRenderer
{
    private const string Reset = "\u001b[0m";

    public static void Write(string markdown)
    {
        var rendered = Render(markdown, !Console.IsOutputRedirected &&
                                        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")));
        Console.Write(rendered);
        if (!rendered.EndsWith('\n')) Console.WriteLine();
    }

    public static string Render(string markdown, bool colorEnabled)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        var safe = Sanitize(markdown).ReplaceLineEndings("\n");
        var output = new StringBuilder();
        var inCodeBlock = false;
        string? codeLanguage = null;
        foreach (var line in safe.Split('\n'))
        {
            var fence = FenceRegex().Match(line);
            if (fence.Success)
            {
                inCodeBlock = !inCodeBlock;
                codeLanguage = inCodeBlock ? fence.Groups[1].Value : null;
                var label = inCodeBlock
                    ? $"┌─ code{(string.IsNullOrWhiteSpace(codeLanguage) ? string.Empty : $" · {codeLanguage}")}"
                    : "└─";
                output.Append(Styled(label, "\u001b[2;90m", colorEnabled)).AppendLine();
                continue;
            }

            if (inCodeBlock)
            {
                output.Append(Styled("│ ", "\u001b[2;90m", colorEnabled))
                    .Append(Styled(line, "\u001b[38;5;252m", colorEnabled)).AppendLine();
                continue;
            }

            var heading = HeadingRegex().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                output.Append(Styled(
                    (level <= 2 ? "◆ " : "◇ ") + RenderInline(heading.Groups[2].Value, colorEnabled),
                    level == 1 ? "\u001b[1;96m" : "\u001b[1;94m",
                    colorEnabled)).AppendLine();
                continue;
            }

            if (HorizontalRuleRegex().IsMatch(line))
            {
                output.Append(Styled(new string('─', 48), "\u001b[2;90m", colorEnabled)).AppendLine();
                continue;
            }

            var quote = QuoteRegex().Match(line);
            if (quote.Success)
            {
                output.Append(Styled("│ ", "\u001b[93m", colorEnabled))
                    .Append(RenderInline(quote.Groups[1].Value, colorEnabled)).AppendLine();
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                output.Append(bullet.Groups[1].Value)
                    .Append(Styled("• ", "\u001b[92m", colorEnabled))
                    .Append(RenderInline(bullet.Groups[2].Value, colorEnabled)).AppendLine();
                continue;
            }

            var numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                output.Append(numbered.Groups[1].Value)
                    .Append(Styled(numbered.Groups[2].Value + " ", "\u001b[92m", colorEnabled))
                    .Append(RenderInline(numbered.Groups[3].Value, colorEnabled)).AppendLine();
                continue;
            }

            output.Append(RenderInline(line, colorEnabled)).AppendLine();
        }

        return output.ToString();
    }

    private static string RenderInline(string value, bool colorEnabled)
    {
        value = LinkRegex().Replace(value, match =>
            Styled(match.Groups[1].Value, "\u001b[4;94m", colorEnabled) +
            Styled($" ({match.Groups[2].Value})", "\u001b[2;90m", colorEnabled));
        value = InlineCodeRegex().Replace(value,
            match => Styled(match.Groups[1].Value, "\u001b[93m", colorEnabled));
        return BoldRegex().Replace(value, match => Styled(
            match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value,
            "\u001b[1m",
            colorEnabled));
    }

    private static string Styled(string text, string style, bool enabled) =>
        enabled ? style + text + Reset : text;

    private static string Sanitize(string value) => new(value
        .Where(character => character is '\n' or '\r' or '\t' || !char.IsControl(character))
        .ToArray());

    [GeneratedRegex(@"^\s*```\s*([^`]*)$", RegexOptions.Compiled)]
    private static partial Regex FenceRegex();
    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled)]
    private static partial Regex HorizontalRuleRegex();
    [GeneratedRegex(@"^\s*>\s?(.*)$", RegexOptions.Compiled)]
    private static partial Regex QuoteRegex();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex BulletRegex();
    [GeneratedRegex(@"^(\s*)(\d+[.)])\s+(.+)$", RegexOptions.Compiled)]
    private static partial Regex NumberedRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled)]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Compiled)]
    private static partial Regex InlineCodeRegex();
    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__", RegexOptions.Compiled)]
    private static partial Regex BoldRegex();
}
