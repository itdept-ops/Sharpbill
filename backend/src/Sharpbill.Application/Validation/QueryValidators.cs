using Sharpbill.Application.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Application.Validation;

public sealed class UserQueryValidator : IValidator<UserQuery>
{
    private static readonly HashSet<string> Statuses = new(
        ["active", "pending", "disabled"],
        StringComparer.Ordinal);

    public ValidationResult Validate(UserQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = Pagination(value.Limit, value.Offset, 500);
        if (value.Search is not null && value.Search.Length is < 2 or > 100)
        {
            errors.Add(new("search", "INVALID_LENGTH", "search must be 2-100 characters"));
        }

        if (value.Status is not null && !Statuses.Contains(value.Status))
        {
            errors.Add(new("status", "INVALID_VALUE", "status must be active, pending, or disabled"));
        }

        if (value.RoleId is < 1)
        {
            errors.Add(new("role_id", "OUT_OF_RANGE", "role_id must be positive"));
        }

        return Result(errors);
    }

    internal static List<ValidationFailure> Pagination(int limit, int offset, int maxLimit)
    {
        var errors = new List<ValidationFailure>();
        if (limit < 1 || limit > maxLimit)
        {
            errors.Add(new("limit", "OUT_OF_RANGE", $"limit must be between 1 and {maxLimit}"));
        }

        if (offset is < 0 or > 10_000)
        {
            errors.Add(new("offset", "OUT_OF_RANGE", "offset must be between 0 and 10000"));
        }

        return errors;
    }

    internal static ValidationResult Result(List<ValidationFailure> errors) =>
        errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors);
}

public sealed class RequestLogQueryValidator : IValidator<RequestLogQuery>
{
    public ValidationResult Validate(RequestLogQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = UserQueryValidator.Pagination(value.Limit, value.Offset, 500);
        if (value.Offset != 0)
        {
            errors.Add(new(
                "offset",
                "CURSOR_REQUIRED",
                "offset paging is not supported; use before_id from next_cursor"));
        }

        if (value.BeforeId is < 1)
        {
            errors.Add(new("before_id", "OUT_OF_RANGE", "before_id must be positive"));
        }

        if (value.Search?.Length > 100)
        {
            errors.Add(new("search", "INVALID_LENGTH", "search cannot exceed 100 characters"));
        }

        if (value.Method?.Length > 10)
        {
            errors.Add(new("method", "INVALID_LENGTH", "method cannot exceed 10 characters"));
        }

        return UserQueryValidator.Result(errors);
    }
}

public sealed class SecurityEventQueryValidator : IValidator<SecurityEventQuery>
{
    private static readonly HashSet<string> Outcomes = new(
        ["success", "failure", "denied"],
        StringComparer.Ordinal);

    public ValidationResult Validate(SecurityEventQuery value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = UserQueryValidator.Pagination(value.Limit, 0, 500);
        if (value.BeforeId is < 1)
        {
            errors.Add(new("before_id", "OUT_OF_RANGE", "before_id must be positive"));
        }

        Length(errors, value.EventType, 3, 80, "event_type");
        if (value.Outcome is not null && !Outcomes.Contains(value.Outcome))
        {
            errors.Add(new(
                "outcome",
                "INVALID_VALUE",
                "outcome must be success, failure, or denied"));
        }

        Length(errors, value.Severity, 16, "severity");
        Length(errors, value.RequestId, 1, 64, "request_id");
        if (value.ActorUserId is < 1)
        {
            errors.Add(new("actor_user_id", "OUT_OF_RANGE", "actor_user_id must be positive"));
        }

        return UserQueryValidator.Result(errors);
    }

    private static void Length(
        List<ValidationFailure> errors,
        string? value,
        int minimum,
        int maximum,
        string field)
    {
        if (value is not null && value.Length < minimum)
        {
            errors.Add(new(
                field,
                "INVALID_LENGTH",
                $"{field} must be at least {minimum} characters"));
            return;
        }

        Length(errors, value, maximum, field);
    }

    private static void Length(
        List<ValidationFailure> errors,
        string? value,
        int maximum,
        string field)
    {
        if (value?.Length > maximum)
        {
            errors.Add(new(field, "INVALID_LENGTH", $"{field} cannot exceed {maximum} characters"));
        }
    }
}
