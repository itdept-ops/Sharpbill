using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Application.Policies;

public static partial class SecurityEventPolicy
{
    [GeneratedRegex("^[a-z][a-z0-9_.-]{2,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypeRegex();

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetTypeRegex();

    [GeneratedRegex(
        "(?:authorization|cookie|credential|id[_-]?token|jwt|nonce|password|provider[_-]?subject|secret|session[_-]?token|access[_-]?token|refresh[_-]?token)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenMetadataKeyRegex();

    public static ValidationResult Validate(SecurityEventWrite value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var errors = new List<ValidationFailure>();
        if (!EventTypeRegex().IsMatch(value.EventType))
        {
            errors.Add(new("event_type", "INVALID_EVENT_TYPE", "event_type has an invalid format"));
        }

        if (value.TargetType is not null && !TargetTypeRegex().IsMatch(value.TargetType))
        {
            errors.Add(new("target_type", "INVALID_TARGET_TYPE", "target_type has an invalid format"));
        }

        try
        {
            ValidateMetadata(value.Metadata, 0);
            if (JsonSerializer.SerializeToUtf8Bytes(value.Metadata).Length >
                DomainLimits.MaxSecurityEventMetadataBytes)
            {
                errors.Add(new(
                    "metadata",
                    "METADATA_TOO_LARGE",
                    "security-event metadata exceeds 4096 encoded bytes"));
            }
        }
        catch (ArgumentException exception)
        {
            errors.Add(new("metadata", "INVALID_METADATA", exception.Message));
        }

        return errors.Count == 0 ? ValidationResult.Success : ValidationResult.Failure(errors);
    }

    public static string ErrorFingerprint(string failureMessage)
    {
        ArgumentNullException.ThrowIfNull(failureMessage);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(failureMessage));
        return $"sink_delivery_failed:{Convert.ToHexStringLower(bytes)[..16]}";
    }

    public static TimeSpan RetryDelay(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        return TimeSpan.FromSeconds(Math.Min(3_600, Math.Pow(2, Math.Min(attempts, 12))));
    }

    private static void ValidateMetadata(object? value, int depth)
    {
        if (depth > DomainLimits.MaxSecurityEventMetadataDepth)
        {
            throw new ArgumentException("security-event metadata nesting is too deep", nameof(value));
        }

        switch (value)
        {
            case null or bool or int or long or decimal or string:
                return;
            case float number when float.IsFinite(number):
                return;
            case double number when double.IsFinite(number):
                return;
            case IReadOnlyDictionary<string, object?> map:
                foreach (var pair in map)
                {
                    if (ForbiddenMetadataKeyRegex().IsMatch(pair.Key))
                    {
                        throw new ArgumentException(
                            $"forbidden security-event metadata key: {pair.Key}",
                            nameof(value));
                    }

                    ValidateMetadata(pair.Value, depth + 1);
                }

                return;
            case IEnumerable<object?> sequence:
                var items = sequence.Take(DomainLimits.MaxSecurityEventListItems + 1).ToArray();
                if (items.Length > DomainLimits.MaxSecurityEventListItems)
                {
                    throw new ArgumentException(
                        "security-event metadata list is too large",
                        nameof(value));
                }

                foreach (var item in items)
                {
                    ValidateMetadata(item, depth + 1);
                }

                return;
            default:
                throw new ArgumentException(
                    $"unsupported security-event metadata type: {value.GetType().Name}",
                    nameof(value));
        }
    }
}
