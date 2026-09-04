using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Core;

namespace MuAgents.UnitTests;

public sealed class AgentRuntimeTests
{
    [Fact]
    public async Task CompactAsync_PersistsCheckpointWithinOneThirdOfMaximum()
    {
        var store = new MemoryConversationStore();
        var conversation = await store.CreateAsync("tenant", "user", null);
        for (var index = 0; index < 30; index++)
            await store.AppendMessageAsync(
                "tenant",
                conversation.Id,
                AgentMessage.Text(index % 2 == 0 ? AgentRole.User : AgentRole.Assistant, new string('x', 400)));
        var contextOptions = Options.Create(new ContextOptions
        {
            MaxContextTokens = 600,
            ReservedOutputTokens = 64,
            SafetyMarginTokens = 32
        });
        var runtime = new AgentRuntime(
            new DelegateChatModel((_, _) => Empty()),
            new FakeGateway(),
            store,
            new ContextManager(new ApproximateTokenEstimator(), contextOptions),
            Options.Create(new AgentOptions()),
            contextOptions,
            NullLogger<AgentRuntime>.Instance);

        var status = await runtime.CompactAsync(
            "tenant",
            conversation.Id,
            new ModelParameters("test"));

        Assert.Equal(200, status.CompactTargetTokens);
        Assert.InRange(status.CurrentTokens, 1, status.CompactTargetTokens);
        var checkpoint = Assert.Single(await store.GetMessagesAsync("tenant", conversation.Id));
        Assert.Equal("compaction-checkpoint", checkpoint.Metadata?.Properties?["kind"]);
    }

    [Fact]
    public async Task RunAsync_ExecutesToolAndContinuesModelLoop()
    {
        var store = new MemoryConversationStore();
        var conversation = await store.CreateAsync("tenant", "user", null);
        var completions = 0;
        var model = new DelegateChatModel((_, token) => Complete(++completions, token));
        var runtime = new AgentRuntime(
            model,
            new FakeGateway(),
            store,
            new ContextManager(new ApproximateTokenEstimator(), Options.Create(new ContextOptions())),
            Options.Create(new AgentOptions { MaxToolIterations = 2 }),
            Options.Create(new ContextOptions()),
            NullLogger<AgentRuntime>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(new AgentRunRequest(
                           "tenant", "user", conversation.Id, "What time?", new ModelParameters("test"))))
        {
            events.Add(item);
        }

        Assert.Equal(2, completions);
        Assert.Contains(events, item => item is ToolCallStartedEvent);
        Assert.Contains(events, item => item is ToolCallCompletedEvent);
        Assert.Contains(events, item => item is TextDeltaEvent { Delta: "done" });
        Assert.Equal(4, (await store.GetMessagesAsync("tenant", conversation.Id)).Count);
    }

    private static async IAsyncEnumerable<ModelEvent> Complete(
        int invocation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation == 1)
        {
            yield return new ModelToolCall("call-1", "test.tool", "{}");
            yield return new ModelCompleted("tool_calls");
        }
        else
        {
            yield return new ModelTextDelta("done");
            yield return new ModelCompleted("stop");
        }
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ModelEvent> Empty()
    {
        await Task.Yield();
        yield break;
    }

    private sealed class FakeGateway : IToolGateway
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [];
        public Task<IReadOnlyList<ToolInvocationResult>> InvokeAsync(
            IReadOnlyList<ToolInvocation> calls,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolInvocationResult>>(
                calls.Select(call => new ToolInvocationResult(
                    call.CallId, call.Name, new ToolResult("tool output"), TimeSpan.Zero)).ToArray());
    }

    private sealed class MemoryConversationStore : IConversationStore
    {
        private readonly Dictionary<(string Tenant, string Id), (Conversation Conversation, List<AgentMessage> Messages)> _items = [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Conversation> CreateAsync(string tenantId, string userId, string? title, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var conversation = new Conversation(Guid.NewGuid().ToString("N"), tenantId, userId, title, now, now);
            _items[(tenantId, conversation.Id)] = (conversation, []);
            return Task.FromResult(conversation);
        }
        public Task<Conversation?> GetAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue((tenantId, conversationId), out var item) ? item.Conversation : null);
        public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentMessage>>(_items[(tenantId, conversationId)].Messages.ToArray());
        public Task AppendMessageAsync(string tenantId, string conversationId, AgentMessage message, CancellationToken cancellationToken = default)
        {
            _items[(tenantId, conversationId)].Messages.Add(message);
            return Task.CompletedTask;
        }
        public Task ReplaceMessagesAsync(string tenantId, string conversationId, IReadOnlyList<AgentMessage> messages, CancellationToken cancellationToken = default)
        {
            var item = _items[(tenantId, conversationId)];
            item.Messages.Clear();
            item.Messages.AddRange(messages);
            return Task.CompletedTask;
        }
    }
}
