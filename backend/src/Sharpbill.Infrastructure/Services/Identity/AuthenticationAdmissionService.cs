using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Identity;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthenticationAdmissionService
{
    private const string AdministratorRole = "admin";
    private const string DefaultRole = "user";

    private readonly IIdentityRepository _identityRepository;
    private readonly IUserRepository _userRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly IClock _clock;
    private readonly AuthenticationPolicy _policy;

    public AuthenticationAdmissionService(
        IIdentityRepository identityRepository,
        IUserRepository userRepository,
        IRoleRepository roleRepository,
        IClock clock,
        AuthenticationPolicy policy)
    {
        _identityRepository = identityRepository ??
            throw new ArgumentNullException(nameof(identityRepository));
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
        _roleRepository = roleRepository ?? throw new ArgumentNullException(nameof(roleRepository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<AuthenticationAdmissionResult> FindOrProvisionAsync(
        SiteSettings site,
        VerifiedIdentity identity,
        CancellationToken cancellationToken)
    {
        DateTime now = _clock.UtcNow;
        string providerName = AuthenticationPolicy.ProviderName(identity.Provider);
        string identityNamespace = AuthenticationPolicy.IdentityNamespace(identity);
        UserIdentity? storedIdentity = await _identityRepository.FindAsync(
            providerName,
            identityNamespace,
            identity.Subject,
            false,
            cancellationToken).ConfigureAwait(false);
        if (storedIdentity is not null)
        {
            User user = await _userRepository.FindForAuthenticationAsync(
                storedIdentity.UserId,
                cancellationToken).ConfigureAwait(false)
                ?? throw ApiException.Forbidden("ACCOUNT_DISABLED", "This account is unavailable");
            AuthenticationPolicy.RequireAuthenticatable(user);
            var updatedIdentity = storedIdentity with
            {
                ProviderTenantId = identity.TenantId,
                ProviderHostedDomain = identity.HostedDomain,
                UpdatedAt = now,
            };
            await _identityRepository.UpdateEvidenceAsync(
                updatedIdentity,
                cancellationToken).ConfigureAwait(false);
            user = user with
            {
                LastLoginAt = now,
                LastSeenAt = now,
                UpdatedAt = now,
                Identities = user.Identities
                    .Select(item => item.Id == updatedIdentity.Id ? updatedIdentity : item)
                    .ToArray(),
            };
            await _userRepository.UpdateAsync(user, cancellationToken).ConfigureAwait(false);
            return new AuthenticationAdmissionResult(user, false);
        }

        bool administratorBootstrap = _policy.IsAdministratorBootstrap(identity);
        if (site.SignupMode == SignupMode.Closed && !administratorBootstrap)
        {
            throw ApiException.Forbidden("SIGNUP_CLOSED", "Sign-ups are currently closed");
        }

        Role role = administratorBootstrap
            ? await FindRoleOrDefaultAsync(AdministratorRole, cancellationToken).ConfigureAwait(false)
            : await FindDefaultRoleAsync(site.DefaultRoleId, cancellationToken).ConfigureAwait(false);
        bool approved = administratorBootstrap || site.SignupMode == SignupMode.Open;
        var newUser = new User
        {
            Id = 0,
            Email = identity.Email.ToLowerInvariant(),
            DisplayName = identity.DisplayName,
            RoleId = role.Id,
            RoleName = role.Name,
            IsActive = true,
            IsApproved = approved,
            AccessVersion = 1,
            LastLoginAt = now,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            RolePermissionKeys = role.PermissionKeys,
        };
        int userId = await _userRepository.AddAsync(newUser, cancellationToken).ConfigureAwait(false);
        var newIdentity = new UserIdentity
        {
            Id = 0,
            UserId = userId,
            Provider = AuthenticationPolicy.ToDomainProvider(identity.Provider),
            ProviderNamespace = identityNamespace,
            ProviderSubject = identity.Subject,
            ProviderTenantId = identity.TenantId,
            ProviderHostedDomain = identity.HostedDomain,
            CreatedAt = now,
            UpdatedAt = now,
        };
        int identityId = await _identityRepository.AddAsync(
            newIdentity,
            cancellationToken).ConfigureAwait(false);
        return new AuthenticationAdmissionResult(
            newUser with
            {
                Id = userId,
                Identities = [newIdentity with { Id = identityId }],
            },
            true);
    }

    public async Task<Role> FindRoleOrDefaultAsync(
        string roleName,
        CancellationToken cancellationToken)
    {
        Role? role = await _roleRepository.FindByNameAsync(
            roleName,
            false,
            cancellationToken).ConfigureAwait(false);
        role ??= await _roleRepository.FindByNameAsync(
            DefaultRole,
            false,
            cancellationToken).ConfigureAwait(false);
        return role ?? throw new ApiException(500, "INTERNAL_ERROR", "Default role is missing");
    }

    private async Task<Role> FindDefaultRoleAsync(
        int defaultRoleId,
        CancellationToken cancellationToken)
    {
        Role? role = await _roleRepository.FindAsync(
            defaultRoleId,
            false,
            cancellationToken).ConfigureAwait(false);
        return role ?? await FindRoleOrDefaultAsync(DefaultRole, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AuthenticationAdmissionResult(User User, bool WasCreated);
