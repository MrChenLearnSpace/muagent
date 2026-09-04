namespace MuAgents.UnitTests;

public sealed class SlashCommandCompletionTests
{
    [Fact]
    public void Complete_UniquePrefixCompletesCommand()
    {
        var result = SlashCommandCatalog.Complete("/mcp_d");

        Assert.Equal("/mcp_disable ", result.Text);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Complete_AmbiguousPrefixReturnsAllCandidates()
    {
        var result = SlashCommandCatalog.Complete("/skills_");

        Assert.Equal("/skills_", result.Text);
        Assert.Contains(result.Candidates, candidate => candidate.Name == "/skills_add");
        Assert.Contains(result.Candidates, candidate => candidate.Name == "/skills_disable");
        Assert.Contains(result.Candidates, candidate => candidate.Name == "/skills_enable");
        Assert.Contains(result.Candidates, candidate => candidate.Name == "/skills_list");
        Assert.Contains(result.Candidates, candidate => candidate.Name == "/skills_remove");
    }

    [Fact]
    public void Complete_DoesNotModifyArgumentsOrNormalText()
    {
        Assert.Equal("/add src", SlashCommandCatalog.Complete("/add src").Text);
        Assert.Empty(SlashCommandCatalog.Complete("hello").Candidates);
    }
}
