using Microsoft.AspNetCore.Mvc.Filters;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Api.Filters;

/// <summary>Returns the request's DB connection before MVC begins transmitting its result.</summary>
public sealed class DatabaseConnectionReleaseFilter(DatabaseSession session) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            _ = await next().ConfigureAwait(false);
        }
        finally
        {
            await session.ReleaseConnectionAsync().ConfigureAwait(false);
        }
    }
}
