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
            Options.Create(new AgentOptions { DefaultSystemInstruction = "test agent" }),
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

    [Fact]
    public async Task RunAsync_AlwaysAddsCodingAgentInstruction()
    {
        var store = new MemoryConversationStore();
        var conversation = await store.CreateAsync("tenant", "user", null);
        AgentRequest? captured = null;
        var model = new DelegateChatModel((request, _) =>
        {
            captured = request;
            return DelegateChatModel.FromEvents([new ModelCompleted("stop")]);
        });
        var runtime = new AgentRuntime(
            model,
            new FakeGateway(),
            store,
            new ContextManager(new ApproximateTokenEstimator(), Options.Create(new ContextOptions())),
            Options.Create(new AgentOptions()),
            Options.Create(new ContextOptions()),
            NullLogger<AgentRuntime>.Instance);

        await foreach (var _ in runtime.RunAsync(new AgentRunRequest(
                           "tenant", "user", conversation.Id, "Create a game", new ModelParameters("test")))) { }

        Assert.NotNull(captured);
        Assert.Contains("local.write_file", captured.Parameters.SystemInstruction);
        Assert.Contains("Do not merely paste proposed code", captured.Parameters.SystemInstruction);
    }

    [Fact]
    public async Task RunAsync_ToolIterationLimit_DoesNotPersistDanglingToolCall()
    {
        var store = new MemoryConversationStore();
        var conversation = await store.CreateAsync("tenant", "user", null);
        var model = new DelegateChatModel((_, _) => DelegateChatModel.FromEvents(
            [new ModelToolCall("last-call", "test.tool", "{}"), new ModelCompleted("tool_calls")]));
        var runtime = new AgentRuntime(
            model,
            new FakeGateway(),
            store,
            new ContextManager(new ApproximateTokenEstimator(), Options.Create(new ContextOptions())),
            Options.Create(new AgentOptions { MaxToolIterations = 1 }),
            Options.Create(new ContextOptions()),
            NullLogger<AgentRuntime>.Instance);

        var events = new List<AgentEvent>();
        await foreach (var item in runtime.RunAsync(new AgentRunRequest(
                           "tenant", "user", conversation.Id, "do work", new ModelParameters("test"))))
            events.Add(item);

        var history = await store.GetMessagesAsync("tenant", conversation.Id);
        Assert.Contains(history.SelectMany(message => message.Parts),
            part => part is ToolCallPart { CallId: "last-call" });
        Assert.Contains(history.SelectMany(message => message.Parts),
            part => part is ToolResultPart { CallId: "last-call" });
        Assert.Contains(events, item => item is CompletedEvent { FinishReason: "max_tool_iterations" });
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
        public Task<IReadOnlyList<Conversation>> ListAsync(
            string tenantId,
            string createdByUserId,
            int limit = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Conversation>>(_items.Values
                .Select(item => item.Conversation)
                .Where(item => item.TenantId == tenantId && item.CreatedByUserId == createdByUserId)
                .OrderByDescending(item => item.UpdatedAt)
                .Take(limit)
                .ToArray());
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
