namespace MuAgents.UnitTests;

public sealed class FileReferenceSetTests
{
    [Fact]
    public async Task AddDirectory_RecursivelyIncludesTextAndExcludesGeneratedOrSensitiveFiles()
    {
        var root = TestPaths.NewDirectoryPath("file-references");
        var subdirectory = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var generated = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var projectState = Directory.CreateDirectory(Path.Combine(root, ".muagent", "config")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "readme");
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "Program.cs"), "class Program { }");
        await File.WriteAllTextAsync(Path.Combine(generated, "generated.cs"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(root, "muagents.settings.local.json"), "secret");
        await File.WriteAllTextAsync(Path.Combine(projectState, "muagents.settings.json"), "project-secret");

        var references = new FileReferenceSet(root);
        var result = await references.AddAsync(".");

        Assert.Equal(2, result.Added);
        Assert.Equal(2, references.Count);
        Assert.Contains(references.Snapshot(), file => file.Path == "README.md");
        Assert.Contains(references.Snapshot(), file => file.Path == Path.Combine("src", "Program.cs"));
        Assert.DoesNotContain(references.Snapshot(), file => file.Content.Contains("secret", StringComparison.Ordinal));
        Assert.DoesNotContain(references.Snapshot(), file => file.Content.Contains("project-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(references.Snapshot(), file => file.Content.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoveDirectory_RemovesAllReferencesBelowIt()
    {
        var root = TestPaths.NewDirectoryPath("file-reference-remove");
        var subdirectory = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "guide.md"), "guide");
        var references = new FileReferenceSet(root);
        await references.AddAsync(root);

        var removed = references.Remove(subdirectory);

        Assert.Equal(1, removed);
        Assert.Single(references.Snapshot());
        Assert.Equal("root.txt", references.Snapshot()[0].Path);
    }
}
