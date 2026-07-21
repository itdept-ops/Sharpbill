using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Sharpbill.Migrator;

internal sealed record SchemaSnapshotResource(
    string Sql,
    string Sha256,
    IReadOnlyList<string> Statements)
{
    private const string ResourceName = "Sharpbill.Migrator.schema-0021.sql";

    public static async Task<SchemaSnapshotResource> LoadAsync(
        CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(SchemaSnapshotResource).Assembly;
        await using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"The embedded schema resource '{ResourceName}' is missing.");

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();
        string sql = Encoding.UTF8.GetString(bytes);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        IReadOnlyList<string> statements = SqlScriptSplitter.Split(sql);
        if (statements.Count == 0)
        {
            throw new InvalidOperationException("The embedded schema snapshot is empty.");
        }

        return new SchemaSnapshotResource(sql, sha256, statements);
    }
}
