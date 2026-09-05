public sealed class CliTerminalPresentationTests
{
    [Fact]
    public void SubmitKey_UsesEnterWithoutShift()
    {
        var enter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
        var shiftEnter = new ConsoleKeyInfo('\r', ConsoleKey.Enter, true, false, false);

        Assert.True(SlashCommandLine.IsSubmitKey(enter));
        Assert.False(SlashCommandLine.IsSubmitKey(shiftEnter));
    }

    [Fact]
    public void RenderedRowCount_ReservesSpaceForEveryInputLine()
    {
        Assert.Equal(1, SlashCommandLine.CalculateRenderedRowCount("YOU > ", "hello", 80));
        Assert.Equal(2, SlashCommandLine.CalculateRenderedRowCount("YOU > ", "hello\nworld", 80));
        Assert.Equal(3, SlashCommandLine.CalculateRenderedRowCount("YOU > ", "one\ntwo\nthree", 80));
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
