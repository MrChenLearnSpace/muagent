using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Persistence;

namespace MuAgents.UnitTests;

public sealed class SqliteConversationStoreTests
{
    [Fact]
    public async Task QueriesAreTenantScoped()
    {
        var database = Path.Combine(Path.GetTempPath(), $"muagents-{Guid.NewGuid():N}.db");
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
}
