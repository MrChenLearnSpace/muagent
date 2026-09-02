using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Core;

namespace MuAgents.UnitTests;

public sealed class ContextManagerTests
{
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
}
