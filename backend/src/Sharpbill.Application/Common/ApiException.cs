namespace Sharpbill.Application.Common;

/// <summary>A transport-safe application failure translated by the API exception middleware.</summary>
public sealed class ApiException : Exception
{
    public ApiException(
        int statusCode,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<ValidationFailure>? validationFailures = null)
        : base(message)
    {
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        StatusCode = statusCode;
        Code = code;
        Headers = headers ?? new Dictionary<string, string>();
        ValidationFailures = validationFailures;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public IReadOnlyList<ValidationFailure>? ValidationFailures { get; }

    public static ApiException BadRequest(string code, string message) => new(400, code, message);
    public static ApiException Unauthorized(string code, string message) => new(401, code, message);
    public static ApiException Forbidden(string code, string message) => new(403, code, message);
    public static ApiException NotFound(string message) => new(404, "NOT_FOUND", message);
    public static ApiException Conflict(string code, string message) => new(409, code, message);
    public static ApiException PreconditionRequired(string code, string message) =>
        new(428, code, message);
}
