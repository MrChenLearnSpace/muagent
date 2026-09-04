namespace MuAgents.Abstractions;

/// <summary>本地用户账户。密码只保存哈希，SecurityStamp 用于使旧身份凭据失效。</summary>
public sealed record UserAccount(
    string Id,
    string UserName,
    string NormalizedUserName,
    string PasswordHash,
    bool IsSystemAdmin,
    bool IsDisabled,
    string SecurityStamp,
    DateTimeOffset CreatedAt);

/// <summary>用户在指定租户中的成员关系和角色。</summary>
public sealed record TenantMembership(
    string TenantId,
    string TenantName,
    string UserId,
    string Role,
    DateTimeOffset CreatedAt);

/// <summary>租户实体；租户是会话数据和授权判断的主要隔离边界。</summary>
public sealed record TenantAccount(
    string Id,
    string Name,
    DateTimeOffset CreatedAt);

/// <summary>首次初始化原子创建的管理员账户及其租户成员关系。</summary>
public sealed record BootstrapIdentityResult(UserAccount User, TenantMembership Membership);

/// <summary>
/// 身份持久化契约。实现必须保证 BootstrapAsync 只能成功一次，并对用户名和成员关系执行唯一性约束。
/// </summary>
public interface IIdentityStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> HasUsersAsync(CancellationToken cancellationToken = default);
    Task<BootstrapIdentityResult> BootstrapAsync(
        string userName,
        string passwordHash,
        string tenantName,
        CancellationToken cancellationToken = default);
    Task<UserAccount> CreateUserAsync(
        string userName,
        string passwordHash,
        bool isSystemAdmin = false,
        CancellationToken cancellationToken = default);
    Task<TenantAccount> CreateTenantAsync(
        string tenantName,
        string ownerUserId,
        CancellationToken cancellationToken = default);
    Task<TenantMembership> SetMembershipAsync(
        string tenantId,
        string userId,
        string role,
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
