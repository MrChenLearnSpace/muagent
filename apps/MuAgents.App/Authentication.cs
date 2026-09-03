using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MuAgents.Abstractions;

public sealed class AuthenticationOptions
{
    public string Issuer { get; set; } = "MuAgents";
    public string Audience { get; set; } = "MuAgents.Api";
    public string JwtSigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;
    public int CookieDays { get; set; } = 7;
    public int MinimumPasswordLength { get; set; } = 12;
    public string DataProtectionKeysPath { get; set; } = "data/keys";
}

public sealed record AuthenticatedSession(
    UserAccount User,
    TenantMembership Membership,
    ClaimsPrincipal Principal,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed class LocalAuthenticationService(
    IIdentityStore store,
    IPasswordHasher<UserAccount> passwordHasher,
    IOptions<AuthenticationOptions> options)
{
    private readonly AuthenticationOptions _options = options.Value;
    private readonly UserAccount _dummyUser = new(
        "dummy", "dummy", "DUMMY", "", false, true, "dummy", DateTimeOffset.UnixEpoch);
    private readonly string _dummyPasswordHash = passwordHasher.HashPassword(
        new UserAccount("dummy", "dummy", "DUMMY", "", false, true, "dummy", DateTimeOffset.UnixEpoch),
        "not-a-real-password-value");

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
        var membership = tenantId is null
            ? memberships.Count == 1 ? memberships[0] : null
            : memberships.FirstOrDefault(item => item.TenantId == tenantId);
        if (membership is null)
        {
            await store.RecordAuthenticationEventAsync(user.Id, "login", false, remoteAddress, cancellationToken).ConfigureAwait(false);
            return null;
        }

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
