namespace Sharpbill.Application.Common;

public interface IClock
{
    DateTime UtcNow { get; }
}

/// <summary>Adapts the framework TimeProvider for deterministic application tests.</summary>
public sealed class TimeProviderClock(TimeProvider timeProvider) : IClock
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
}

public sealed record RequestContext
{
    public string? RequestId { get; init; }
    public string? ClientRequestId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public Guid? SessionJti { get; init; }
    public int? SessionUserId { get; init; }
}

/// <summary>Exposes request evidence to application services without coupling them to ASP.NET Core.</summary>
public interface IRequestContextAccessor
{
    RequestContext Current { get; set; }
}

public sealed record ExportDocument(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

public sealed record GeoPlace(string? Place, string? Timezone);

public sealed record ValidationFailure(string Field, string Code, string Message);

public sealed record ValidationResult
{
    private ValidationResult(IReadOnlyList<ValidationFailure> errors)
    {
        Errors = errors;
    }

    public IReadOnlyList<ValidationFailure> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public static ValidationResult Success { get; } = new([]);

    public static ValidationResult Failure(params ValidationFailure[] errors) => new(errors);

    public static ValidationResult Failure(IEnumerable<ValidationFailure> errors) =>
        new(errors.ToArray());

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new ApiException(
                422,
                "VALIDATION_ERROR",
                "Invalid request",
                validationFailures: Errors);
        }
    }
}

public interface IValidator<in T>
{
    ValidationResult Validate(T value);
}
