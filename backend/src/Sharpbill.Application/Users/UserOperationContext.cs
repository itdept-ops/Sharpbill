using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Users;

public sealed class UserOperationContext
{
    private readonly IUserRepository _users;
    private readonly ISettingsRepository _settings;
    private readonly IHealthRepository _health;

    public UserOperationContext(
        IUserRepository users,
        ISettingsRepository settings,
        IHealthRepository health)
    {
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }

    public async Task<User> RequireActorAsync(
        int actorUserId,
        string? permission,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        User? actor = await _users.FindAsync(actorUserId, forUpdate, cancellationToken)
            .ConfigureAwait(false);
        if (!UserAccountPolicy.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        if (permission is not null)
        {
            RbacHierarchyPolicy.RequirePermission(actor!, permission);
        }

        return actor!;
    }

    public async Task<User> FindUserAsync(
        int userId,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await _users.FindAsync(userId, forUpdate, cancellationToken).ConfigureAwait(false)
            ?? throw ApiException.NotFound("User not found");

    public async Task<(User Actor, User Target)> LoadActorAndTargetForUpdateAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId == targetUserId)
        {
            User same = await FindUserAsync(actorUserId, true, cancellationToken)
                .ConfigureAwait(false);
            if (!UserAccountPolicy.IsAuthenticatable(same))
            {
                throw ApiException.Forbidden(
                    "FORBIDDEN",
                    "Your account can no longer perform this action");
            }

            return (same, same);
        }

        int firstId = Math.Min(actorUserId, targetUserId);
        int secondId = Math.Max(actorUserId, targetUserId);
        User? first = await _users.FindAsync(firstId, true, cancellationToken)
            .ConfigureAwait(false);
        User? second = await _users.FindAsync(secondId, true, cancellationToken)
            .ConfigureAwait(false);
        User? actor = actorUserId == firstId ? first : second;
        User? target = targetUserId == firstId ? first : second;
        if (!UserAccountPolicy.IsAuthenticatable(actor))
        {
            throw ApiException.Forbidden(
                "FORBIDDEN",
                "Your account can no longer perform this action");
        }

        return (actor!, target ?? throw ApiException.NotFound("User not found"));
    }

    public async Task<SiteSettings> RequireSettingsAsync(
        bool forUpdate,
        CancellationToken cancellationToken) =>
        await _settings.GetAsync(forUpdate, cancellationToken).ConfigureAwait(false)
            ?? throw BusinessErrors.SettingsNotInitialized();

    public async Task EnsureAdministrationAvailableAsync(CancellationToken cancellationToken)
    {
        if (!await _health.HasReachableAdministratorAsync(cancellationToken).ConfigureAwait(false))
        {
            throw ApiException.Conflict(
                "ADMIN_ACCESS_STRANDED",
                "This change would leave no reachable administrator or bootstrap path");
        }
    }
}
