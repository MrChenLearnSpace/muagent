using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Persistence;

namespace MuAgents.UnitTests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task ReplaceMessages_IsAtomicAndPreservesRequestedOrder()
    {
        var database = TestPaths.NewFile(".db");
        try
        {
            var store = new SqliteConversationStore(Options.Create(
                new PersistenceOptions { ConnectionString = $"Data Source={database};Pooling=False" }));
            var conversation = await store.CreateAsync("tenant", "user", "compact");
            await store.AppendMessageAsync("tenant", conversation.Id, AgentMessage.Text(AgentRole.User, "old"));
            var replacement = new[]
            {
                AgentMessage.Text(AgentRole.System, "checkpoint"),
                AgentMessage.Text(AgentRole.User, "recent")
            };

            await store.ReplaceMessagesAsync("tenant", conversation.Id, replacement);

            var actual = await store.GetMessagesAsync("tenant", conversation.Id);
            Assert.Equal(replacement.Select(message => message.Id), actual.Select(message => message.Id));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                store.ReplaceMessagesAsync("other-tenant", conversation.Id, replacement));
        }
        finally
        {
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task QueriesAreTenantScoped()
    {
        var database = TestPaths.NewFile(".db");
        try
        {
            var store = new SqliteConversationStore(Options.Create(
                new PersistenceOptions { ConnectionString = $"Data Source={database};Pooling=False" }));
            var conversation = await store.CreateAsync("tenant-a", "user-a", "private");
            await store.AppendMessageAsync(
                "tenant-a", conversation.Id, AgentMessage.Text(AgentRole.User, "secret"));

            Assert.Null(await store.GetAsync("tenant-b", conversation.Id));
            Assert.Empty(await store.GetMessagesAsync("tenant-b", conversation.Id));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AppendMessageAsync(
                "tenant-b", conversation.Id, AgentMessage.Text(AgentRole.User, "cross-tenant")));
        }
        finally
        {
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyCurrentUsersMostRecentConversations()
    {
        var database = TestPaths.NewFile(".db");
        try
        {
            var store = new SqliteConversationStore(Options.Create(
                new PersistenceOptions { ConnectionString = $"Data Source={database};Pooling=False" }));
            var older = await store.CreateAsync("tenant", "user", "older");
            var otherUser = await store.CreateAsync("tenant", "other-user", "other");
            var newer = await store.CreateAsync("tenant", "user", "newer");
            await store.AppendMessageAsync("tenant", older.Id, AgentMessage.Text(AgentRole.User, "updated last"));

            var listed = await store.ListAsync("tenant", "user", 1);

            var resumed = Assert.Single(listed);
            Assert.Equal(older.Id, resumed.Id);
            Assert.DoesNotContain(listed, item => item.Id == otherUser.Id || item.Id == newer.Id);
        }
        finally
        {
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
