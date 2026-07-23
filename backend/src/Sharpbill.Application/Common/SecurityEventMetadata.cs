using System.Security.Cryptography;
using System.Text;

namespace Sharpbill.Application.Common;

public static class SecurityEventMetadata
{
    public static IReadOnlyDictionary<string, object?> SummarizeStrings(
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string[] normalized = values
            .Select(static value => value.Length > 100 ? value[..100] : value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        byte[] canonical = Encoding.UTF8.GetBytes(string.Join('\n', normalized));
        string digest = Convert.ToHexStringLower(SHA256.HashData(canonical));
        return new Dictionary<string, object?>
        {
            ["count"] = normalized.Length,
            ["sha256"] = digest,
            ["sample"] = normalized.Take(8).ToArray(),
            ["sample_truncated"] = normalized.Length > 8,
        };
    }
}
