using System.Text.Json;
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
}
