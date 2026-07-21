using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Operations;

public interface IRequestLogBuffer
{
    bool TryWrite(RequestLog requestLog);
    Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken);
    RequestLogMetricsResponse GetMetrics();
}
