using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;

namespace MuAgents.Persistence;

public sealed class SqliteIdentityStore(IOptions<PersistenceOptions> options) : IIdentityStore
{
    private readonly string _connectionString = options.Value.ConnectionString;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

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
                CREATE TABLE IF NOT EXISTS users (
                    id TEXT PRIMARY KEY,
                    user_name TEXT NOT NULL,
                    normalized_user_name TEXT NOT NULL UNIQUE,
                    password_hash TEXT NOT NULL,
                    is_system_admin INTEGER NOT NULL DEFAULT 0,
                    is_disabled INTEGER NOT NULL DEFAULT 0,
                    security_stamp TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS tenants (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    normalized_name TEXT NOT NULL UNIQUE,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS tenant_memberships (
                    tenant_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    PRIMARY KEY (tenant_id, user_id),
                    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
                    FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS authentication_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id TEXT NULL,
                    event_name TEXT NOT NULL,
                    succeeded INTEGER NOT NULL,
                    remote_address TEXT NULL,
                    occurred_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_authentication_audit_user_time
                    ON authentication_audit(user_id, occurred_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initializeLock.Release(); }
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM users LIMIT 1);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    public async Task<BootstrapIdentityResult> BootstrapAsync(
        string userName,
        string passwordHash,
        string tenantName,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var userId = Guid.NewGuid().ToString("N");
        var tenantId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT COUNT(*) FROM users;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            throw new InvalidOperationException("Identity bootstrap has already been completed.");

        command.CommandText = """
            INSERT INTO users(id, user_name, normalized_user_name, password_hash, is_system_admin, is_disabled, security_stamp, created_at)
            VALUES($id, $name, $normalized, $hash, 1, 0, $stamp, $created);
            """;
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$name", userName);
        command.Parameters.AddWithValue("$normalized", Normalize(userName));
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$stamp", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO tenants(id, name, normalized_name, created_at) VALUES($id, $name, $normalized, $created);
            INSERT INTO tenant_memberships(tenant_id, user_id, role, created_at) VALUES($id, $user, 'Owner', $created);
            """;
        command.Parameters.AddWithValue("$id", tenantId);
        command.Parameters.AddWithValue("$name", tenantName);
        command.Parameters.AddWithValue("$normalized", Normalize(tenantName));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var user = await FindUserAsync(userName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Bootstrapped user could not be read.");
        return new BootstrapIdentityResult(user, new TenantMembership(tenantId, tenantName, userId, "Owner", now));
    }

    public async Task<UserAccount> CreateUserAsync(
        string userName,
        string passwordHash,
        bool isSystemAdmin = false,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var user = new UserAccount(
            Guid.NewGuid().ToString("N"),
            userName,
            Normalize(userName),
            passwordHash,
            isSystemAdmin,
            false,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users(id, user_name, normalized_user_name, password_hash, is_system_admin, is_disabled, security_stamp, created_at)
            VALUES($id, $name, $normalized, $hash, $admin, 0, $stamp, $created);
            """;
        command.Parameters.AddWithValue("$id", user.Id);
        command.Parameters.AddWithValue("$name", user.UserName);
        command.Parameters.AddWithValue("$normalized", user.NormalizedUserName);
        command.Parameters.AddWithValue("$hash", user.PasswordHash);
        command.Parameters.AddWithValue("$admin", user.IsSystemAdmin);
        command.Parameters.AddWithValue("$stamp", user.SecurityStamp);
        command.Parameters.AddWithValue("$created", user.CreatedAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("A user with this name already exists.", exception);
        }
        return user;
    }

    public async Task<TenantAccount> CreateTenantAsync(
        string tenantName,
        string ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var tenant = new TenantAccount(Guid.NewGuid().ToString("N"), tenantName, DateTimeOffset.UtcNow);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO tenants(id, name, normalized_name, created_at)
            VALUES($id, $name, $normalized, $created);
            INSERT INTO tenant_memberships(tenant_id, user_id, role, created_at)
            VALUES($id, $owner, 'Owner', $created);
            """;
        command.Parameters.AddWithValue("$id", tenant.Id);
        command.Parameters.AddWithValue("$name", tenant.Name);
        command.Parameters.AddWithValue("$normalized", Normalize(tenant.Name));
        command.Parameters.AddWithValue("$owner", ownerUserId);
        command.Parameters.AddWithValue("$created", tenant.CreatedAt.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "The tenant name is already in use or the owner does not exist.", exception);
        }
        return tenant;
    }

    public async Task<TenantMembership> SetMembershipAsync(
        string tenantId,
        string userId,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (role is not ("Owner" or "Admin" or "Member"))
            throw new ArgumentOutOfRangeException(nameof(role), "Role must be Owner, Admin, or Member.");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        if (role != "Owner")
        {
            command.CommandText = """
                SELECT CASE WHEN
                    EXISTS(SELECT 1 FROM tenant_memberships
                           WHERE tenant_id = $tenant AND user_id = $user AND role = 'Owner')
                    AND (SELECT COUNT(*) FROM tenant_memberships
                         WHERE tenant_id = $tenant AND role = 'Owner') <= 1
                THEN 1 ELSE 0 END;
                """;
            command.Parameters.AddWithValue("$tenant", tenantId);
            command.Parameters.AddWithValue("$user", userId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1)
                throw new InvalidOperationException("A tenant must retain at least one owner.");
            command.Parameters.Clear();
        }
        command.CommandText = """
            INSERT INTO tenant_memberships(tenant_id, user_id, role, created_at)
            VALUES($tenant, $user, $role, $created)
            ON CONFLICT(tenant_id, user_id) DO UPDATE SET role = excluded.role;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("The tenant or user does not exist.", exception);
        }

        command.Parameters.Clear();
        command.CommandText = """
            SELECT m.tenant_id, t.name, m.user_id, m.role, m.created_at
            FROM tenant_memberships m JOIN tenants t ON t.id = m.tenant_id
            WHERE m.tenant_id = $tenant AND m.user_id = $user;
            """;
        command.Parameters.AddWithValue("$tenant", tenantId);
        command.Parameters.AddWithValue("$user", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The membership could not be read after it was saved.");
        var membership = new TenantMembership(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4)));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return membership;
    }

    public async Task<UserAccount?> FindUserAsync(string userName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, user_name, normalized_user_name, password_hash, is_system_admin, is_disabled, security_stamp, created_at
            FROM users WHERE normalized_user_name = $name;
            """;
        command.Parameters.AddWithValue("$name", Normalize(userName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new UserAccount(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.GetString(6), ParseDate(reader.GetString(7)))
            : null;
    }

    public async Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.tenant_id, t.name, m.user_id, m.role, m.created_at
            FROM tenant_memberships m JOIN tenants t ON t.id = m.tenant_id
            WHERE m.user_id = $user ORDER BY t.name;
            """;
        command.Parameters.AddWithValue("$user", userId);
        var memberships = new List<TenantMembership>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            memberships.Add(new TenantMembership(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseDate(reader.GetString(4))));
        return memberships;
    }

    public async Task UpdatePasswordHashAsync(string userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET password_hash = $hash WHERE id = $id;";
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAuthenticationEventAsync(
        string? userId,
        string eventName,
        bool succeeded,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO authentication_audit(user_id, event_name, succeeded, remote_address, occurred_at)
            VALUES($user, $event, $succeeded, $remote, $time);
            """;
        command.Parameters.AddWithValue("$user", (object?)userId ?? DBNull.Value);
        command.Parameters.AddWithValue("$event", eventName);
        command.Parameters.AddWithValue("$succeeded", succeeded);
        command.Parameters.AddWithValue("$remote", (object?)remoteAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        var source = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:") return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(source));
        if (directory is not null) Directory.CreateDirectory(directory);
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
