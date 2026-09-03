using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MuAgents.Abstractions;
using MuAgents.Persistence;

namespace MuAgents.UnitTests;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task BootstrapCanOnlyRunOnceAndLoginCarriesTenantClaim()
    {
        var database = Path.Combine(Path.GetTempPath(), $"muagents-auth-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteIdentityStore(Options.Create(new PersistenceOptions
            {
                ConnectionString = $"Data Source={database};Pooling=False"
            }));
            var passwordHasher = new PasswordHasher<UserAccount>(Options.Create(new PasswordHasherOptions()));
            var authentication = new LocalAuthenticationService(
                store,
                passwordHasher,
                Options.Create(new AuthenticationOptions
                {
                    JwtSigningKey = "test-signing-key-with-at-least-32-characters",
                    MinimumPasswordLength = 12
                }));

            var bootstrap = await authentication.BootstrapAsync(
                "Administrator", "correct horse battery staple", "Primary", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => authentication.BootstrapAsync(
                "SecondAdmin", "correct horse battery staple", "Second", CancellationToken.None));

            var session = await authentication.LoginAsync(
                "administrator", "correct horse battery staple", bootstrap.Membership.TenantId,
                "127.0.0.1", CancellationToken.None);

            Assert.NotNull(session);
            Assert.Equal(bootstrap.Membership.TenantId, session.Membership.TenantId);
            var payload = session.AccessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var token = JsonDocument.Parse(Convert.FromBase64String(payload));
            Assert.Equal(bootstrap.Membership.TenantId, token.RootElement.GetProperty("tenant_id").GetString());
            Assert.Null(await authentication.LoginAsync(
                "administrator", "wrong-password", bootstrap.Membership.TenantId,
                "127.0.0.1", CancellationToken.None));
        }
        finally
        {
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SystemAdministratorCanProvisionUsersTenantsAndMemberships()
    {
        var database = Path.Combine(Path.GetTempPath(), $"muagents-auth-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteIdentityStore(Options.Create(new PersistenceOptions
            {
                ConnectionString = $"Data Source={database};Pooling=False"
            }));
            var authentication = new LocalAuthenticationService(
                store,
                new PasswordHasher<UserAccount>(Options.Create(new PasswordHasherOptions())),
                Options.Create(new AuthenticationOptions
                {
                    JwtSigningKey = "test-signing-key-with-at-least-32-characters",
                    MinimumPasswordLength = 12
                }));

            var bootstrap = await authentication.BootstrapAsync(
                "Administrator", "correct horse battery staple", "Primary", CancellationToken.None);
            var user = await authentication.CreateUserAsync(
                "Operator", "another unique password", false, CancellationToken.None);
            var tenant = await authentication.CreateTenantAsync(
                "Secondary", bootstrap.User.Id, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => authentication.SetMembershipAsync(
                tenant.Id, bootstrap.User.UserName, "Member", CancellationToken.None));
            var membership = await authentication.SetMembershipAsync(
                tenant.Id, user.UserName, "admin", CancellationToken.None);

            Assert.Equal("Admin", membership.Role);
            Assert.Equal(tenant.Id, membership.TenantId);
            var session = await authentication.LoginAsync(
                "operator", "another unique password", tenant.Id, "127.0.0.1", CancellationToken.None);
            Assert.NotNull(session);
            Assert.Equal("Admin", session.Membership.Role);
            await Assert.ThrowsAsync<InvalidOperationException>(() => authentication.CreateUserAsync(
                "operator", "another unique password", false, CancellationToken.None));
        }
        finally
        {
            foreach (var path in new[] { database, database + "-wal", database + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void TenantMemberManagementRequiresSystemAdminOrMatchingOwner()
    {
        var owner = Principal(
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.Role, "Owner"));
        var member = Principal(
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.Role, "Member"));
        var systemAdmin = Principal(new Claim("system_admin", "true"));

        Assert.True(TenantAccess.CanManageMembers(owner, "tenant-a"));
        Assert.False(TenantAccess.CanManageMembers(owner, "tenant-b"));
        Assert.False(TenantAccess.CanManageMembers(member, "tenant-a"));
        Assert.True(TenantAccess.CanManageMembers(systemAdmin, "tenant-b"));
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));
}
