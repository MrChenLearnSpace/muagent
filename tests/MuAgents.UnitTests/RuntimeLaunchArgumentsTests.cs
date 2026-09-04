using MuAgents.Abstractions;

namespace MuAgents.UnitTests;

public sealed class RuntimeLaunchArgumentsTests
{
    [Fact]
    public void Parse_UsesLaunchDirectoryByDefault()
    {
        var directory = CreateDirectory("launch-default");

        var result = RuntimeLaunchArguments.Parse(["--urls", "http://localhost:5000"], directory);

        Assert.Equal(Path.GetFullPath(directory), result.ProjectDirectory);
        Assert.Equal(new[] { "--urls", "http://localhost:5000" }, result.RemainingArguments);
    }

    [Fact]
    public void Parse_ResolvesRelativeDirectoryAndRemovesCommonArguments()
    {
        var parent = CreateDirectory("launch-parent");
        var project = Directory.CreateDirectory(Path.Combine(parent, "child project")).FullName;

        var result = RuntimeLaunchArguments.Parse(
            ["--url", "http://localhost:5000", "-d", "child project", "--bootstrap"],
            parent);

        Assert.Equal(project, result.ProjectDirectory);
        Assert.Equal(new[] { "--url", "http://localhost:5000", "--bootstrap" }, result.RemainingArguments);
    }

    [Fact]
    public void Parse_AcceptsLongEqualsForm()
    {
        var directory = CreateDirectory("launch-equals");

        var result = RuntimeLaunchArguments.Parse([$"--directory={directory}"]);

        Assert.Equal(Path.GetFullPath(directory), result.ProjectDirectory);
        Assert.Empty(result.RemainingArguments);
    }

    [Fact]
    public void Parse_RejectsMissingDirectory()
    {
        var missing = Path.Combine(TestPaths.NewDirectoryPath("missing-project"), "not-created");

        var exception = Assert.Throws<DirectoryNotFoundException>(() =>
            RuntimeLaunchArguments.Parse(["-d", missing]));

        Assert.Contains(Path.GetFullPath(missing), exception.Message);
    }

    private static string CreateDirectory(string prefix)
    {
        var directory = TestPaths.NewDirectoryPath(prefix);
        Directory.CreateDirectory(directory);
        return directory;
    }
}
