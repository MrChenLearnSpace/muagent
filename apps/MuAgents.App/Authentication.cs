using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MuAgents.Abstractions;

/// <summary>本地 JWT、Cookie、密码规则和 Data Protection 密钥目录配置。</summary>
public sealed class AuthenticationOptions
{
    /// <summary>JWT 签发者。</summary>
    public string Issuer { get; set; } = "MuAgents";
    /// <summary>JWT 目标受众。</summary>
    public string Audience { get; set; } = "MuAgents.Api";
    /// <summary>HMAC-SHA256 签名密钥，启动验证要求至少 32 个字符。</summary>
    public string JwtSigningKey { get; set; } = string.Empty;
    /// <summary>访问令牌有效分钟数。</summary>
    public int AccessTokenMinutes { get; set; } = 60;
    /// <summary>持久 Cookie 有效天数。</summary>
    public int CookieDays { get; set; } = 7;
    /// <summary>新密码最少字符数。</summary>
    public int MinimumPasswordLength { get; set; } = 12;
    /// <summary>Data Protection 密钥目录，必须位于程序根目录内。</summary>
    public string DataProtectionKeysPath { get; set; } = "data/keys";
}

/// <summary>一次成功登录生成的用户、租户身份、ClaimsPrincipal 和 JWT。</summary>
public sealed record AuthenticatedSession(
    UserAccount User,
    TenantMembership Membership,
    ClaimsPrincipal Principal,
    string AccessToken,
    DateTimeOffset ExpiresAt);

/// <summary>负责输入校验、密码哈希、租户选择、登录审计和 JWT 签发的本地认证服务。</summary>
public sealed class LocalAuthenticationService(
    IIdentityStore store,
    IPasswordHasher<UserAccount> passwordHasher,
    IOptions<AuthenticationOptions> options)
{
    private readonly AuthenticationOptions _options = options.Value;
    // 未找到用户时仍执行一次真实哈希校验，缩小“用户名是否存在”的计时侧信道。
    private readonly UserAccount _dummyUser = new(
        "dummy", "dummy", "DUMMY", "", false, true, "dummy", DateTimeOffset.UnixEpoch);
    private readonly string _dummyPasswordHash = passwordHasher.HashPassword(
        new UserAccount("dummy", "dummy", "DUMMY", "", false, true, "dummy", DateTimeOffset.UnixEpoch),
        "not-a-real-password-value");

    /// <summary>创建系统管理员及首个租户；存储层保证该操作只成功一次。</summary>
    public async Task<BootstrapIdentityResult> BootstrapAsync(
        string userName,
        string password,
        string tenantName,
        CancellationToken cancellationToken)
    {
        ValidateUserName(userName);
        ValidatePassword(password);
        ValidateTenantName(tenantName);
        var provisional = new UserAccount("", userName.Trim(), userName.Trim().ToUpperInvariant(), "", true, false, "", DateTimeOffset.UtcNow);
        var hash = passwordHasher.HashPassword(provisional, password);
        return await store.BootstrapAsync(userName.Trim(), hash, tenantName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建本地用户并在落库前完成输入校验和密码哈希。</summary>
    public async Task<UserAccount> CreateUserAsync(
        string userName,
        string password,
        bool isSystemAdmin,
        CancellationToken cancellationToken)
    {
        ValidateUserName(userName);
        ValidatePassword(password);
        var trimmedName = userName.Trim();
        var provisional = new UserAccount(
            "", trimmedName, trimmedName.ToUpperInvariant(), "", isSystemAdmin, false, "", DateTimeOffset.UtcNow);
        var hash = passwordHasher.HashPassword(provisional, password);
        return await store.CreateUserAsync(trimmedName, hash, isSystemAdmin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建租户并把指定用户设置为所有者。</summary>
    public Task<TenantAccount> CreateTenantAsync(
        string tenantName,
        string ownerUserId,
        CancellationToken cancellationToken)
    {
        ValidateTenantName(tenantName);
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new BadHttpRequestException("A tenant owner is required.");
        return store.CreateTenantAsync(tenantName.Trim(), ownerUserId, cancellationToken);
    }

    /// <summary>创建或更新租户成员关系，并把角色规范化为固定值。</summary>
    public async Task<TenantMembership> SetMembershipAsync(
        string tenantId,
        string userName,
        string role,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new BadHttpRequestException("A tenant ID is required.");
        ValidateUserName(userName);
        var normalizedRole = role?.Trim().ToUpperInvariant() switch
        {
            "OWNER" => "Owner",
            "ADMIN" => "Admin",
            "MEMBER" => "Member",
            _ => throw new BadHttpRequestException("Role must be Owner, Admin, or Member.")
        };
        var user = await store.FindUserAsync(userName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The requested user was not found.");
        return await store.SetMembershipAsync(tenantId, user.Id, normalizedRole, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>验证凭据、选择唯一租户并签发只对该租户有效的身份令牌。</summary>
    public async Task<AuthenticatedSession?> LoginAsync(
        string userName,
        string password,
        string? tenantId,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 64 || password is null || password.Length > 256)
        {
            _ = passwordHasher.VerifyHashedPassword(
                _dummyUser, _dummyPasswordHash,
                password is null ? string.Empty : password[..Math.Min(password.Length, 256)]);
            return null;
        }
        var user = await store.FindUserAsync(userName, cancellationToken).ConfigureAwait(false);
        var verification = user is null
            ? passwordHasher.VerifyHashedPassword(_dummyUser, _dummyPasswordHash, password)
            : passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (user is null || user.IsDisabled || verification == PasswordVerificationResult.Failed)
        {
            await store.RecordAuthenticationEventAsync(user?.Id, "login", false, remoteAddress, cancellationToken).ConfigureAwait(false);
            return null;
        }
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
            await store.UpdatePasswordHashAsync(user.Id, passwordHasher.HashPassword(user, password), cancellationToken).ConfigureAwait(false);
        var memberships = await store.GetMembershipsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        // 多租户用户必须显式选择租户，避免服务端悄悄使用错误租户签发令牌。
        var membership = tenantId is null
            ? memberships.Count == 1 ? memberships[0] : null
            : memberships.FirstOrDefault(item => item.TenantId == tenantId);
        if (membership is null)
        {
            await store.RecordAuthenticationEventAsync(user.Id, "login", false, remoteAddress, cancellationToken).ConfigureAwait(false);
            return null;
        }

        // tenant_id 来自持久化成员关系而非请求头，是后续所有数据隔离判断的可信来源。
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, membership.Role),
            new("tenant_id", membership.TenantId),
            new("tenant_name", membership.TenantName),
            new("security_stamp", user.SecurityStamp)
        };
        if (user.IsSystemAdmin) claims.Add(new Claim("system_admin", "true"));
        var identity = new ClaimsIdentity(claims, "MuAgents");
        var principal = new ClaimsPrincipal(identity);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSigningKey)),
                SecurityAlgorithms.HmacSha256));
        await store.RecordAuthenticationEventAsync(user.Id, "login", true, remoteAddress, cancellationToken).ConfigureAwait(false);
        return new AuthenticatedSession(user, membership, principal,
            new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    private void ValidatePassword(string password)
    {
        if (password is null || password.Length < _options.MinimumPasswordLength || password.Length > 256)
            throw new BadHttpRequestException($"Password must contain {_options.MinimumPasswordLength}-256 characters.");
    }

    private static void ValidateUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length is < 3 or > 64 || userName.Any(char.IsControl))
            throw new BadHttpRequestException("User name must contain 3-64 printable characters.");
    }

    private static void ValidateTenantName(string tenantName)
    {
        if (string.IsNullOrWhiteSpace(tenantName) || tenantName.Length > 128 || tenantName.Any(char.IsControl))
            throw new BadHttpRequestException("Tenant name must contain 1-128 printable characters.");
    }
}
