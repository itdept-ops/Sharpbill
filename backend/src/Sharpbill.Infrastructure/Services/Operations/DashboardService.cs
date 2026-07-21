using Microsoft.Extensions.Caching.Memory;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed class DashboardService(
    IDashboardRepository repository,
    IUserRepository users,
    IClock clock,
    IMemoryCache cache) : IDashboardService
{
    private const string AnalyticsCacheKey = "sharpbill:dashboard:analytics";
    private const int OnlineWindowSeconds = 90;

    public async Task<DashboardResponse> GetAsync(int userId, CancellationToken cancellationToken)
    {
        await RequireUserAsync(userId, null, cancellationToken).ConfigureAwait(false);
        return await repository.GetAsync(
            clock.UtcNow.AddSeconds(-OnlineWindowSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AnalyticsResponse> GetAnalyticsAsync(int userId, CancellationToken cancellationToken)
    {
        await RequireUserAsync(userId, PermissionKeys.UsersRead, cancellationToken).ConfigureAwait(false);
        if (cache.TryGetValue(AnalyticsCacheKey, out AnalyticsResponse? cached) && cached is not null)
        {
            return cached;
        }

        DateTime now = clock.UtcNow;
        AnalyticsResponse response = await repository.GetAnalyticsAsync(
            now.AddSeconds(-OnlineWindowSeconds),
            DateOnly.FromDateTime(now.AddDays(-13)),
            cancellationToken).ConfigureAwait(false);
        cache.Set(AnalyticsCacheKey, response, TimeSpan.FromSeconds(15));
        return response;
    }

    private async Task RequireUserAsync(
        int userId,
        string? permission,
        CancellationToken cancellationToken)
    {
        User? user = await users.FindAsync(userId, false, cancellationToken).ConfigureAwait(false);
        if (permission is null)
        {
            if (user is null || !user.IsActive || !user.IsApproved || user.ErasedAt is not null)
            {
                throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
            }
        }
        else
        {
            ServiceAuthorization.Require(user, permission);
        }
    }
}
