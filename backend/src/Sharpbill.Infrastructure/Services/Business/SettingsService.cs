using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Settings;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Business;

public sealed class SettingsService : ISettingsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly ISettingsRepository _settings;
    private readonly IHealthRepository _health;
    private readonly ISecurityEventService _securityEvents;
    private readonly IClock _clock;
    private readonly IRequestContextAccessor _requestContextAccessor;
    private readonly IdentityProviderOptions _providerOptions;

    public SettingsService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        IRoleRepository roles,
        ISettingsRepository settings,
        IHealthRepository health,
        ISecurityEventService securityEvents,
        IClock clock,
        IRequestContextAccessor requestContextAccessor,
        IOptions<SharpbillOptions> options)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _securityEvents = securityEvents ?? throw new ArgumentNullException(nameof(securityEvents));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _requestContextAccessor = requestContextAccessor ??
            throw new ArgumentNullException(nameof(requestContextAccessor));
        ArgumentNullException.ThrowIfNull(options);
        _providerOptions = options.Value.IdentityProviders;
    }

    public async Task<SiteSettingsResponse> GetAsync(
        int actorUserId,
        CancellationToken cancellationToken)
    {
        _ = await RequireActorAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        SiteSettings settings = await _settings.GetAsync(false, cancellationToken)
            .ConfigureAwait(false)
            ?? throw BusinessServiceSupport.SettingsNotInitialized();
        return await ToResponseAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SiteSettingsResponse> UpdateAsync(
        int actorUserId,
        SiteSettingsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SiteSettings updated = await BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                SiteSettings current = await _settings.GetAsync(true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw BusinessServiceSupport.SettingsNotInitialized();
                Role? requestedDefaultRole = null;
                if (request.DefaultRoleId.HasValue && request.DefaultRoleId.Value is not null)
                {
                    requestedDefaultRole = await _roles.FindAsync(
                        request.DefaultRoleId.Value.Value,
                        true,
                        cancellationToken).ConfigureAwait(false)
                        ?? throw ApiException.BadRequest("UNKNOWN_ROLE", "No such role");
                }

                User actor = await RequireActorAsync(actorUserId, true, cancellationToken)
                    .ConfigureAwait(false);
                if (requestedDefaultRole is not null)
                {
                    EnsureDefaultRoleChangeAllowed(actor, requestedDefaultRole);
                }

                bool providerTransition = IsProviderTransition(request);
                ValidateProviderTransition(current, request, providerTransition);
                SiteSettings candidate = Apply(
                    current,
                    request,
                    requestedDefaultRole,
                    current.UpdatedAt);
                Dictionary<string, object?> changes = BuildChanges(current, candidate);
                SiteSettings changed = changes.Count == 0
                    ? current
                    : candidate with { UpdatedAt = _clock.UtcNow };
                if (changes.Count > 0)
                {
                    await _settings.UpdateAsync(changed, cancellationToken).ConfigureAwait(false);
                }

                if (providerTransition &&
                    !await _health.HasReachableAdministratorAsync(cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw ApiException.BadRequest(
                        "ADMIN_ACCESS_STRANDED",
                        "At least one enabled provider must retain an administrator or bootstrap path");
                }

                await _securityEvents.RecordAsync(
                    BusinessServiceSupport.SecurityEvent(
                        _requestContextAccessor,
                        "settings.updated",
                        actor.Id,
                        "site_settings",
                        changed.Id,
                        new Dictionary<string, object?>
                        {
                            ["changes"] = changes,
                        }),
                    cancellationToken).ConfigureAwait(false);
                return changed;
            },
            cancellationToken).ConfigureAwait(false);
        return await ToResponseAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<User> RequireActorAsync(
        int actorUserId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        User? actor = await _users.FindAsync(actorUserId, forUpdate, cancellationToken)
            .ConfigureAwait(false);
        if (!BusinessServiceSupport.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        RbacHierarchyPolicy.RequirePermission(actor!, PermissionKeys.SettingsManage);
        return actor!;
    }

    private async Task<SiteSettingsResponse> ToResponseAsync(
        SiteSettings settings,
        CancellationToken cancellationToken)
    {
        Role? defaultRole = await _roles.FindAsync(
            settings.DefaultRoleId,
            false,
            cancellationToken).ConfigureAwait(false);
        return new SiteSettingsResponse
        {
            SignupMode = ToContract(settings.SignupMode),
            AllowGoogle = settings.AllowGoogle,
            AllowMicrosoft = settings.AllowMicrosoft,
            DefaultRoleId = settings.DefaultRoleId,
            DefaultRoleName = defaultRole?.Name ?? string.Empty,
            CalmMode = settings.CalmMode,
            UpdatedAt = settings.UpdatedAt,
        };
    }

    private void ValidateProviderTransition(
        SiteSettings current,
        SiteSettingsUpdateRequest request,
        bool providerTransition)
    {
        if (!providerTransition)
        {
            return;
        }

        bool google = request.AllowGoogle.HasValue && request.AllowGoogle.Value is not null
            ? request.AllowGoogle.Value.Value
            : current.AllowGoogle;
        bool microsoft = request.AllowMicrosoft.HasValue && request.AllowMicrosoft.Value is not null
            ? request.AllowMicrosoft.Value.Value
            : current.AllowMicrosoft;
        bool effectiveGoogle = google &&
            !string.IsNullOrWhiteSpace(_providerOptions.GoogleClientId);
        bool effectiveMicrosoft = microsoft &&
            !string.IsNullOrWhiteSpace(_providerOptions.MicrosoftClientId);
        if (!effectiveGoogle && !effectiveMicrosoft)
        {
            throw ApiException.BadRequest(
                "NO_PROVIDER_ENABLED",
                "At least one configured sign-in provider must stay enabled");
        }
    }

    private static bool IsProviderTransition(SiteSettingsUpdateRequest request) =>
        request.AllowGoogle.HasValue && request.AllowGoogle.Value is not null ||
        request.AllowMicrosoft.HasValue && request.AllowMicrosoft.Value is not null;

    private static void EnsureDefaultRoleChangeAllowed(User actor, Role role)
    {
        if (!actor.EffectivePermissionKeys.Contains(PermissionKeys.RolesManage))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "Changing the signup default requires both settings.manage and roles.manage");
        }

        if (string.Equals(role.Name, SystemRoleNames.Administrator, StringComparison.Ordinal))
        {
            throw ApiException.Forbidden(
                "PROTECTED_DEFAULT_ROLE",
                "The administrator role cannot be used as the signup default");
        }

        if (!RbacHierarchyPolicy.IsAdministrator(actor) &&
            !role.PermissionKeys.IsSubsetOf(actor.EffectivePermissionKeys))
        {
            throw ApiException.Forbidden(
                "INSUFFICIENT_PRIVILEGE",
                "You cannot set a default role with permissions you do not hold");
        }
    }

    private static SiteSettings Apply(
        SiteSettings current,
        SiteSettingsUpdateRequest request,
        Role? requestedDefaultRole,
        DateTime now) =>
        current with
        {
            SignupMode = request.SignupMode.HasValue && request.SignupMode.Value is not null
                ? ToDomain(request.SignupMode.Value.Value)
                : current.SignupMode,
            AllowGoogle = request.AllowGoogle.HasValue && request.AllowGoogle.Value is not null
                ? request.AllowGoogle.Value.Value
                : current.AllowGoogle,
            AllowMicrosoft = request.AllowMicrosoft.HasValue && request.AllowMicrosoft.Value is not null
                ? request.AllowMicrosoft.Value.Value
                : current.AllowMicrosoft,
            DefaultRoleId = requestedDefaultRole?.Id ?? current.DefaultRoleId,
            CalmMode = request.CalmMode.HasValue && request.CalmMode.Value is not null
                ? request.CalmMode.Value.Value
                : current.CalmMode,
            UpdatedAt = now,
        };

    private static Dictionary<string, object?> BuildChanges(
        SiteSettings before,
        SiteSettings after)
    {
        var changes = new Dictionary<string, object?>();
        AddChange(changes, "signup_mode", ToWire(before.SignupMode), ToWire(after.SignupMode));
        AddChange(changes, "allow_google", before.AllowGoogle, after.AllowGoogle);
        AddChange(changes, "allow_microsoft", before.AllowMicrosoft, after.AllowMicrosoft);
        AddChange(changes, "default_role_id", before.DefaultRoleId, after.DefaultRoleId);
        AddChange(changes, "calm_mode", before.CalmMode, after.CalmMode);
        return changes;
    }

    private static void AddChange<T>(
        Dictionary<string, object?> changes,
        string field,
        T before,
        T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            changes[field] = new Dictionary<string, object?>
            {
                ["from"] = before,
                ["to"] = after,
            };
        }
    }

    private static SignupModeContract ToContract(SignupMode mode) => mode switch
    {
        SignupMode.Open => SignupModeContract.Open,
        SignupMode.Approval => SignupModeContract.Approval,
        SignupMode.Closed => SignupModeContract.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown signup mode"),
    };

    private static SignupMode ToDomain(SignupModeContract mode) => mode switch
    {
        SignupModeContract.Open => SignupMode.Open,
        SignupModeContract.Approval => SignupMode.Approval,
        SignupModeContract.Closed => SignupMode.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown signup mode"),
    };

    private static string ToWire(SignupMode mode) => mode switch
    {
        SignupMode.Open => "open",
        SignupMode.Approval => "approval",
        SignupMode.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown signup mode"),
    };
}
