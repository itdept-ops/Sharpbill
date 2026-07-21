using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed partial class RequestLogService(
    IRequestLogRepository repository,
    IUserRepository users,
    IRequestLogBuffer buffer,
    IValidator<RequestLogQuery> queryValidator,
    ILogger<RequestLogService> logger) : IRequestLogService
{
    public async Task<RequestLogListResponse> ListAsync(
        RequestLogQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        queryValidator.Validate(query).ThrowIfInvalid();
        User? actor = await users.FindAsync(actorUserId, false, cancellationToken).ConfigureAwait(false);
        ServiceAuthorization.Require(actor, PermissionKeys.LogsView);
        try
        {
            await buffer.FlushAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogFlushUnavailable(logger);
        }
        catch (TimeoutException)
        {
            LogFlushUnavailable(logger);
        }
        catch (ChannelClosedException)
        {
            LogFlushUnavailable(logger);
        }

        return await repository.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public RequestLogMetricsResponse GetMetrics() => buffer.GetMetrics();

    [LoggerMessage(
        EventId = 2300,
        Level = LogLevel.Warning,
        Message = "Request-log visibility flush was unavailable; returning the latest persisted rows")]
    private static partial void LogFlushUnavailable(ILogger logger);
}
