using System.Text;
using Sharpbill.Application.Common;

namespace Sharpbill.Infrastructure.Services;

internal static class CsvExportWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static void EnsureWithinLimit(
        IEnumerable<IReadOnlyList<string>> rows,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);

        long encodedBytes = 0;
        foreach (IReadOnlyList<string> row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < row.Count; index++)
            {
                if (index > 0)
                {
                    encodedBytes++;
                }

                encodedBytes += Utf8.GetByteCount(Cell(row[index]));
                if (encodedBytes + 2 > maximumBytes)
                {
                    throw new ApiException(
                        413,
                        "EXPORT_TOO_LARGE",
                        $"The generated CSV exceeds the {maximumBytes:N0}-byte export limit; narrow the filters and retry");
                }
            }

            encodedBytes += 2;
            if (encodedBytes > maximumBytes)
            {
                throw new ApiException(
                    413,
                    "EXPORT_TOO_LARGE",
                    $"The generated CSV exceeds the {maximumBytes:N0}-byte export limit; narrow the filters and retry");
            }
        }
    }

    public static async Task WriteAsync(
        Stream destination,
        IEnumerable<IReadOnlyList<string>> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(rows);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("CSV destination must be writable.", nameof(destination));
        }

        await using var writer = new StreamWriter(destination, Utf8, 16 * 1024, leaveOpen: true);
        foreach (IReadOnlyList<string> row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int index = 0; index < row.Count; index++)
            {
                if (index > 0)
                {
                    await writer.WriteAsync(",".AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.WriteAsync(Cell(row[index]).AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await writer.WriteAsync("\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Cell(string value)
    {
        string safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{value}"
            : value;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safe;
    }
}
