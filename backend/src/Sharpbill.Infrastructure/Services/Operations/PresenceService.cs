using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed class PresenceService(
    IPresenceRepository repository,
    IUserRepository users,
    IClock clock) : IPresenceService
{
    private const int WindowSeconds = 90;

    public async Task<PresenceResponse> GetOnlineAsync(int actorUserId, CancellationToken cancellationToken)
    {
        User? actor = await users.FindAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        ServiceAuthorization.Require(actor, PermissionKeys.PresenceView);
        return await repository.GetOnlineAsync(
            clock.UtcNow.AddSeconds(-WindowSeconds),
            DomainLimits.MaxPresenceRoster,
            WindowSeconds,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(int userId, CancellationToken cancellationToken)
    {
        User? user = await users.FindAsync(userId, false, cancellationToken).ConfigureAwait(false);
        if (user is null || !user.IsActive || !user.IsApproved || user.ErasedAt is not null)
        {
            throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        }

        return new HeartbeatResponse { UserId = userId };
    }
}
