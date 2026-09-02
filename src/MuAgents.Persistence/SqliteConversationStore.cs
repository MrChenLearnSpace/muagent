using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Persistence;

public sealed class SqliteConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public SqliteConversationStore(IOptions<PersistenceOptions> options)
    {
        _connectionString = options.Value.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            EnsureDataDirectory();
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS conversations (
                    tenant_id TEXT NOT NULL,
                    id TEXT NOT NULL,
                    created_by_user_id TEXT NOT NULL,
                    title TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    version INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (tenant_id, id)
                );

                CREATE TABLE IF NOT EXISTS messages (
                    tenant_id TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    id TEXT NOT NULL,
                    role INTEGER NOT NULL,
                    parts_json TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    PRIMARY KEY (tenant_id, conversation_id, sequence),
                    UNIQUE (tenant_id, conversation_id, id),
                    FOREIGN KEY (tenant_id, conversation_id)
                        REFERENCES conversations (tenant_id, id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_messages_tenant_conversation
                    ON messages (tenant_id, conversation_id, sequence);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<Conversation> CreateAsync(
        string tenantId,
        string userId,
        string? title,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(tenantId, nameof(tenantId));
        ValidateIdentity(userId, nameof(userId));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(
            Guid.NewGuid().ToString("N"), tenantId, userId, title, now, now);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations
                (tenant_id, id, created_by_user_id, title, created_at, updated_at, version)
            VALUES ($tenant, $id, $user, $title, $created, $updated, 0);
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task<Conversation?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, tenant_id, created_by_user_id, title, created_at, updated_at, version
            FROM conversations WHERE tenant_id = $tenant AND id = $id;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new Conversation(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseDate(reader.GetString(4)), ParseDate(reader.GetString(5)), reader.GetInt64(6));
    }

    public async Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, parts_json, created_at, metadata_json
            FROM messages
            WHERE tenant_id = $tenant AND conversation_id = $conversation
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        var messages = new List<AgentMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new AgentMessage(
                reader.GetString(0),
                (AgentRole)reader.GetInt32(1),
                JsonSerializer.Deserialize<IReadOnlyList<MessagePart>>(reader.GetString(2), JsonOptions) ?? [],
                ParseDate(reader.GetString(3)),
                reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<MessageMetadata>(reader.GetString(4), JsonOptions)));
        }
        return messages;
    }

    public async Task AppendMessageAsync(
        string tenantId,
        string conversationId,
        AgentMessage message,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO messages
                (tenant_id, conversation_id, sequence, id, role, parts_json, created_at, metadata_json)
            SELECT $tenant, $conversation,
                   COALESCE((SELECT MAX(sequence) FROM messages
                             WHERE tenant_id = $tenant AND conversation_id = $conversation), 0) + 1,
                   $id, $role, $parts, $created, $metadata
            WHERE EXISTS (
                SELECT 1 FROM conversations
                WHERE tenant_id = $tenant AND id = $conversation
            );
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$id", message.Id);
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$parts", JsonSerializer.Serialize(message.Parts, JsonOptions));
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$metadata", message.Metadata is null
            ? DBNull.Value
            : JsonSerializer.Serialize(message.Metadata, JsonOptions));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new KeyNotFoundException("Conversation was not found in this tenant.");
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE conversations
            SET updated_at = $updated, version = version + 1
            WHERE tenant_id = $tenant AND id = $conversation;
            """;
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void EnsureDataDirectory()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:") return;
        var fullPath = Path.GetFullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is not null) Directory.CreateDirectory(directory);
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            throw new ArgumentException("Identity must contain 1-128 characters.", name);
        }
    }
}
