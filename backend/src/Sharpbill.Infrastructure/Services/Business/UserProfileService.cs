using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Business;

internal sealed class UserProfileService : IUserProfileService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IUserRepository _users;
    private readonly UserOperationContext _context;
    private readonly IGeoService _geo;
    private readonly IClock _clock;
    private readonly RetentionOptions _retention;
    private readonly IValidator<ProfileUpdateRequest> _profileValidator;
    private readonly IValidator<LocationUpdateRequest> _locationValidator;

    public UserProfileService(
        IUnitOfWork unitOfWork,
        IUserRepository users,
        UserOperationContext context,
        IGeoService geo,
        IClock clock,
        IOptions<SharpbillOptions> options,
        IValidator<ProfileUpdateRequest> profileValidator,
        IValidator<LocationUpdateRequest> locationValidator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _geo = geo ?? throw new ArgumentNullException(nameof(geo));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _retention = options.Value.Retention;
        _profileValidator = profileValidator ?? throw new ArgumentNullException(nameof(profileValidator));
        _locationValidator = locationValidator ?? throw new ArgumentNullException(nameof(locationValidator));
    }

    public Task<UserResponse> UpdateProfileAsync(
        int userId,
        int actorUserId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _profileValidator.Validate(request).ThrowIfInvalid();
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                (User actor, User target) = await _context.LoadActorAndTargetForUpdateAsync(
                    actorUserId,
                    userId,
                    cancellationToken).ConfigureAwait(false);
                if (userId != actorUserId &&
                    !actor.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage))
                {
                    throw ApiException.Forbidden(
                        "FORBIDDEN",
                        "You can only edit your own profile");
                }

                RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
                User updated = ApplyProfilePatch(target, request) with { UpdatedAt = _clock.UtcNow };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
                return BusinessServiceSupport.ToUserResponse(
                    updated,
                    actor,
                    _clock.UtcNow,
                    includeLocation: true);
            },
            cancellationToken);
    }

    public Task UpdateLocationAsync(
        int userId,
        LocationUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _locationValidator.Validate(request).ThrowIfInvalid();
        return BusinessServiceSupport.InTransactionAsync(
            _unitOfWork,
            async () =>
            {
                User user = await _users.FindAsync(userId, true, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                if (!BusinessServiceSupport.IsAuthenticatable(user))
                {
                    throw ApiException.Unauthorized(
                        "INVALID_SESSION",
                        "Session invalid or expired");
                }

                DateTime now = _clock.UtcNow;
                GeoPlace place = _geo.Resolve(request.Latitude, request.Longitude);
                User updated = user with
                {
                    LastLatitude = request.Latitude,
                    LastLongitude = request.Longitude,
                    LastLocationAccuracy = request.Accuracy,
                    LastLocationAt = now,
                    LocationRetentionUntil = now.AddHours(_retention.PreciseLocationHours),
                    Location = place.Place ?? user.Location,
                    Timezone = place.Timezone ?? user.Timezone,
                    UpdatedAt = now,
                };
                await _users.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static User ApplyProfilePatch(User user, ProfileUpdateRequest request)
    {
        var updated = user with
        {
            DisplayName = Apply(user.DisplayName, request.DisplayName),
            Title = Apply(user.Title, request.Title),
            Department = Apply(user.Department, request.Department),
            Phone = Apply(user.Phone, request.Phone),
            Location = Apply(user.Location, request.Location),
            Timezone = Apply(user.Timezone, request.Timezone),
            Bio = Apply(user.Bio, request.Bio),
            AccentColor = Apply(user.AccentColor, request.AccentColor),
        };
        if (!request.UiPreferences.HasValue)
        {
            return updated;
        }

        return updated with
        {
            UiPreferences = request.UiPreferences.Value is null
                ? null
                : UiPreferencesPolicy.ApplyPatch(user.UiPreferences, request.UiPreferences.Value),
        };
    }

    private static string? Apply(string? current, PatchField<string?> patch) =>
        patch.HasValue ? patch.Value : current;
}
