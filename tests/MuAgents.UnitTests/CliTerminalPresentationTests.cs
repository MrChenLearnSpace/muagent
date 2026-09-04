public sealed class CliTerminalPresentationTests
{
    [Fact]
    public void SubmitKey_RequiresShiftAndEnterTogether()
    {
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
        var shiftEnter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, true, false, false);

        Assert.False(SlashCommandLine.IsSubmitKey(enter));
        Assert.True(SlashCommandLine.IsSubmitKey(shiftEnter));
    }

    [Fact]
    public void MarkdownRenderer_FormatsCommonBlocksWithoutLeavingMarkdownMarkers()
    {
        const string markdown = """
            # 标题
            - **重点**和`代码`
            > 引用
            ```csharp
            Console.WriteLine("ok");
            ```
            """;

        var rendered = TerminalMarkdownRenderer.Render(markdown, colorEnabled: false);

        Assert.Contains("◆ 标题", rendered);
        Assert.Contains("• 重点和代码", rendered);
        Assert.Contains("│ 引用", rendered);
        Assert.Contains("┌─ code · csharp", rendered);
        Assert.DoesNotContain("**", rendered);
        Assert.DoesNotContain("```", rendered);
    }

    [Fact]
    public void MarkdownRenderer_AddsAnsiStylesOnlyWhenEnabled()
    {
        Assert.Contains("\u001b[", TerminalMarkdownRenderer.Render("# title", colorEnabled: true));
        Assert.DoesNotContain("\u001b[", TerminalMarkdownRenderer.Render("# title", colorEnabled: false));
    }
}
