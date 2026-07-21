using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sharpbill.Domain.Constants;

namespace Sharpbill.Infrastructure.Services.Operations;

internal static partial class SecurityMetadataSanitizer
{
    public static IReadOnlyDictionary<string, object?> Sanitize(IReadOnlyDictionary<string, object?> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var clean = (Dictionary<string, object?>)CleanDictionary(metadata, 0);
        if (JsonSerializer.SerializeToUtf8Bytes(clean).Length > DomainLimits.MaxSecurityEventMetadataBytes)
        {
            throw new ArgumentException("Security-event metadata exceeds 4096 encoded bytes.", nameof(metadata));
        }

        return clean;
    }

    private static Dictionary<string, object?> CleanDictionary(
        IEnumerable<KeyValuePair<string, object?>> values,
        int depth)
    {
        EnsureDepth(depth);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string rawKey, object? value) in values)
        {
            if (ForbiddenKey().IsMatch(rawKey))
            {
                throw new ArgumentException($"Forbidden security-event metadata key: {rawKey}", nameof(values));
            }

            result[rawKey[..Math.Min(rawKey.Length, 100)]] = CleanValue(value, depth + 1);
        }

        return result;
    }

    private static object? CleanValue(object? value, int depth)
    {
        EnsureDepth(depth);
        return value switch
        {
            null => null,
            bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => value,
            float number when float.IsFinite(number) => number,
            double number when double.IsFinite(number) => number,
            string text => text[..Math.Min(text.Length, 500)],
            IReadOnlyDictionary<string, object?> dictionary => CleanDictionary(dictionary, depth),
            IDictionary dictionary => CleanDictionary(
                dictionary.Cast<DictionaryEntry>().Select(static entry =>
                    new KeyValuePair<string, object?>(entry.Key?.ToString() ?? string.Empty, entry.Value)),
                depth),
            IEnumerable sequence when value is not string => CleanSequence(sequence, depth),
            JsonElement element => CleanJsonElement(element, depth),
            _ => throw new ArgumentException(
                $"Unsupported security-event metadata type: {value.GetType().Name}",
                nameof(value)),
        };
    }

    private static object?[] CleanSequence(IEnumerable sequence, int depth)
    {
        object?[] values = sequence.Cast<object?>().Take(51).ToArray();
        if (values.Length > 50)
        {
            throw new ArgumentException("Security-event metadata list is too large.", nameof(sequence));
        }

        return values.Select(value => CleanValue(value, depth + 1)).ToArray();
    }

    private static object? CleanJsonElement(JsonElement element, int depth) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => CleanValue(element.GetString(), depth),
        JsonValueKind.Number when element.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number when element.TryGetDouble(out double number) && double.IsFinite(number) => number,
        JsonValueKind.Array => CleanSequence(element.EnumerateArray(), depth),
        JsonValueKind.Object => CleanDictionary(
            element.EnumerateObject().Select(static property =>
                new KeyValuePair<string, object?>(property.Name, property.Value)),
            depth),
        _ => throw new ArgumentException("Unsupported JSON security-event metadata.", nameof(element)),
    };

    private static void EnsureDepth(int depth)
    {
        if (depth > DomainLimits.MaxSecurityEventMetadataDepth)
        {
            throw new ArgumentException("Security-event metadata nesting is too deep.", nameof(depth));
        }
    }

    [GeneratedRegex(
        "(?:authorization|cookie|credential|id[_-]?token|jwt|nonce|password|provider[_-]?subject|secret|session[_-]?token|access[_-]?token|refresh[_-]?token)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenKey();
}
