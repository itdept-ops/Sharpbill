using Sharpbill.Application.Common;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Operations;

internal static class ServiceAuthorization
{
    public static void Require(User? actor, string permission)
    {
        if (actor is null || !actor.IsActive || !actor.IsApproved || actor.ErasedAt is not null)
        {
            throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        }

        if (!actor.EffectivePermissionKeys.Contains(permission))
        {
            throw ApiException.Forbidden("FORBIDDEN", $"Missing permission: {permission}");
        }
    }
}
