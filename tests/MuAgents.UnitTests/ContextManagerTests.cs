using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Core;

namespace MuAgents.UnitTests;

public sealed class ContextManagerTests
{
    [Fact]
    public void CompactToTarget_ReducesContextToRequestedOneThirdBudget()
    {
        var options = Options.Create(new ContextOptions
        {
            MaxContextTokens = 600,
            ReservedOutputTokens = 64,
            SafetyMarginTokens = 16,
            RecentTurnsToKeep = 2
        });
        var manager = new ContextManager(new ApproximateTokenEstimator(), options);
        var messages = Enumerable.Range(0, 30)
            .Select(index => AgentMessage.Text(
                index % 2 == 0 ? AgentRole.User : AgentRole.Assistant,
                new string((char)('a' + index % 20), 160)))
            .ToArray();

        var plan = manager.CompactToTarget(messages, [], new ModelParameters("test"), 200);

        Assert.True(plan.WasCompacted);
        Assert.True(plan.EstimatedTokens <= 200);
        Assert.Single(plan.Messages);
    }

    [Fact]
    public void Prepare_Compacts_WhenThresholdIsReached()
    {
        var manager = new ContextManager(
            new ApproximateTokenEstimator(),
            Options.Create(new ContextOptions
            {
                MaxContextTokens = 200,
                ReservedOutputTokens = 20,
                SafetyMarginTokens = 10,
                CompactionRatio = 0.5,
                RecentTurnsToKeep = 1
            }));
        var messages = new[]
        {
            AgentMessage.Text(AgentRole.User, new string('a', 240)),
            AgentMessage.Text(AgentRole.Assistant, new string('b', 240)),
            AgentMessage.Text(AgentRole.User, "current")
        };

        var result = manager.Prepare(messages, [], new ModelParameters("test", 20));

        Assert.True(result.WasCompacted);
        Assert.True(result.EstimatedTokens < result.OriginalEstimatedTokens);
        Assert.Contains(result.Messages, message =>
            message.Metadata?.Properties?.GetValueOrDefault("kind") == "compaction-checkpoint");
        Assert.Equal("current", ((TextPart)result.Messages[^1].Parts[0]).Text);
    }

    [Fact]
    public void Prepare_RejectsSingleInputThatExceedsBudget()
    {
        var manager = new ContextManager(
            new ApproximateTokenEstimator(),
            Options.Create(new ContextOptions
            {
                MaxContextTokens = 100,
                ReservedOutputTokens = 20,
                SafetyMarginTokens = 10
            }));

        var exception = Assert.Throws<MuAgentException>(() => manager.Prepare(
            [AgentMessage.Text(AgentRole.User, new string('x', 400))],
            [],
            new ModelParameters("test", 20)));

        Assert.Equal(MuAgentErrorCategory.ContentFailure, exception.Category);
    }

    [Fact]
    public void Prepare_KeepsCompleteRecentUserTurnIncludingAllToolMessages()
    {
        var manager = new ContextManager(
            new ApproximateTokenEstimator(),
            Options.Create(new ContextOptions
            {
                MaxContextTokens = 2_000,
                ReservedOutputTokens = 100,
                SafetyMarginTokens = 50,
                CompactionRatio = 0.5,
                RecentTurnsToKeep = 1
            }));
        var messages = new List<AgentMessage>
        {
            AgentMessage.Text(AgentRole.User, new string('o', 2_000)),
            AgentMessage.Text(AgentRole.Assistant, new string('a', 2_000)),
            AgentMessage.Text(AgentRole.User, "modify the previous project")
        };
        for (var index = 0; index < 6; index++)
        {
            messages.Add(new AgentMessage($"call-{index}", AgentRole.Assistant,
                [new ToolCallPart($"tool-{index}", "local.write_file", "{\"path\":\"file.txt\"}")], DateTimeOffset.UtcNow));
            messages.Add(new AgentMessage($"result-{index}", AgentRole.Tool,
                [new ToolResultPart($"tool-{index}", "written", false)], DateTimeOffset.UtcNow));
        }

        var plan = manager.Prepare(messages, [], new ModelParameters("test"));

        Assert.True(plan.WasCompacted);
        var recent = plan.Messages.SkipWhile(message => message.Role == AgentRole.System).ToArray();
        Assert.Equal("modify the previous project", Assert.IsType<TextPart>(recent[0].Parts[0]).Text);
        Assert.Equal(6, recent.SelectMany(message => message.Parts).OfType<ToolCallPart>().Count());
        Assert.Equal(6, recent.SelectMany(message => message.Parts).OfType<ToolResultPart>().Count());
    }
}
