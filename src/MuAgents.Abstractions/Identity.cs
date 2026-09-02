namespace MuAgents.Abstractions;

public sealed record UserAccount(
    string Id,
    string UserName,
    string NormalizedUserName,
    string PasswordHash,
    bool IsSystemAdmin,
    bool IsDisabled,
    string SecurityStamp,
    DateTimeOffset CreatedAt);

public sealed record TenantMembership(
    string TenantId,
    string TenantName,
    string UserId,
    string Role,
    DateTimeOffset CreatedAt);

public sealed record BootstrapIdentityResult(UserAccount User, TenantMembership Membership);

public interface IIdentityStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> HasUsersAsync(CancellationToken cancellationToken = default);
    Task<BootstrapIdentityResult> BootstrapAsync(
        string userName,
        string passwordHash,
        string tenantName,
        CancellationToken cancellationToken = default);
    Task<UserAccount?> FindUserAsync(string userName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TenantMembership>> GetMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default);
    Task UpdatePasswordHashAsync(
        string userId,
        string passwordHash,
        CancellationToken cancellationToken = default);
    Task RecordAuthenticationEventAsync(
        string? userId,
        string eventName,
        bool succeeded,
        string? remoteAddress,
        CancellationToken cancellationToken = default);
}
