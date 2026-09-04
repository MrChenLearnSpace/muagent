using Microsoft.Extensions.Options;
using MuAgents.Mcp;
using MuAgents.Skills;

namespace MuAgents.UnitTests;

public sealed class DynamicConfigurationTests
{
    [Fact]
    public void SkillConfiguration_DeduplicatesBoundDefaultDirectories()
    {
        var path = Path.Combine(TestPaths.NewDirectoryPath("skill-defaults"), "skills.json");
        var options = Options.Create(new SkillOptions { Directories = ["skills", "skills", "SKILLS"] });

        var configuration = new SkillConfigurationStore(options, path);

        Assert.Single(configuration.Snapshot().Directories);
    }

    [Fact]
    public void McpConfiguration_PersistsAddDisableAndRemove()
    {
        var path = Path.Combine(TestPaths.NewDirectoryPath("mcp-config"), "mcp.json");
        var store = new McpConfigurationStore(Options.Create(new McpOptions()), path);

        store.Upsert(new McpServerProfile
        {
            Name = "demo",
            Transport = McpTransport.StreamableHttp,
            Url = "https://example.test/mcp"
        });
        Assert.True(store.SetEnabled("demo", false));

        var reloaded = new McpConfigurationStore(Options.Create(new McpOptions()), path);
        var profile = Assert.Single(reloaded.Snapshot().Servers);
        Assert.Equal("demo", profile.Name);
        Assert.False(profile.Enabled);
        Assert.True(reloaded.Remove("demo"));
        Assert.Empty(reloaded.Snapshot().Servers);
    }

    [Fact]
    public async Task SkillConfiguration_PersistsDirectoryAndEnableState()
    {
        var root = TestPaths.NewDirectoryPath("skill-config");
        var skillDirectory = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), """
            ---
            name: demo
            description: Demo skill
            version: 1.0.0
            ---
            Follow the demo instructions.
            """);
        var path = Path.Combine(root, "config", "skills.json");
        var options = Options.Create(new SkillOptions { Directories = [] });
        var configuration = new SkillConfigurationStore(options, path);
        configuration.AddDirectory(skillDirectory);
        var catalog = new FileSystemSkillCatalog(options, configuration);

        Assert.Single(await catalog.DiscoverAsync());
        configuration.SetEnabled("demo", false);
        Assert.Empty(await catalog.DiscoverAsync());
        var entry = Assert.Single(await catalog.DiscoverAllAsync());
        Assert.False(entry.Enabled);

        var reloaded = new SkillConfigurationStore(options, path);
        Assert.False(reloaded.IsEnabled("demo"));
        Assert.Single(reloaded.Snapshot().Directories);
        Assert.True(reloaded.RemoveDirectory(skillDirectory));
    }
}
