using System.Security.Cryptography;
using System.Text;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Infrastructure.Services.Operations;

public sealed class EventOutboxService(IEventOutboxRepository repository, TimeProvider timeProvider)
    : IEventOutboxService
{
    public Task<IReadOnlyList<EventDeliveryEnvelope>> ClaimAsync(
        string workerId,
        int limit,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        string owner = WorkerId(workerId);
        if (limit is < 1 or > 500 || lease < TimeSpan.FromSeconds(5) || lease > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Delivery limits are outside their allowed range.");
        }

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        return repository.ClaimAsync(owner, limit, now, now.Add(lease), cancellationToken);
    }

    public Task<bool> MarkDeliveredAsync(long eventId, string workerId, CancellationToken cancellationToken) =>
        repository.MarkDeliveredAsync(
            eventId,
            WorkerId(workerId),
            timeProvider.GetUtcNow().UtcDateTime,
            cancellationToken);

    public Task<bool> MarkFailedAsync(
        long eventId,
        string workerId,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        string fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(failureMessage)))[..16];
        return repository.MarkFailedAsync(
            eventId,
            WorkerId(workerId),
            now,
            now.AddSeconds(2),
            $"sink_delivery_failed:{fingerprint}",
            10,
            cancellationToken);
    }

    private static string WorkerId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string worker = value.Trim();
        return worker[..Math.Min(worker.Length, 64)];
    }
}
