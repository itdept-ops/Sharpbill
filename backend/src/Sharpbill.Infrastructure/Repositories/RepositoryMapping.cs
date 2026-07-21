using System.Text.Json;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Infrastructure.Repositories;

internal static class RepositoryMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static DateTime ToDatabaseUtc(DateTime value)
    {
        DateTime utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
    }

    public static DateTime FromDatabaseUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? FromDatabaseUtc(DateTime? value) =>
        value is null ? null : FromDatabaseUtc(value.Value);

    public static DateTime ToDatabaseDate(DateOnly value) =>
        value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static UiPreferences? DeserializeUiPreferences(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : JsonSerializer.Deserialize<UiPreferences>(value, JsonOptions);

    public static IReadOnlyDictionary<string, JsonElement> DeserializeJsonElements(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value, JsonOptions)
                ?? new Dictionary<string, JsonElement>();

    public static IReadOnlyDictionary<string, object?> DeserializeObjects(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(value, JsonOptions)
                ?? new Dictionary<string, object?>();

    public static string Provider(IdentityProvider provider) => provider switch
    {
        IdentityProvider.Google => "google",
        IdentityProvider.Microsoft => "microsoft",
        IdentityProvider.Dev => "dev",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static IdentityProvider Provider(string provider) => provider switch
    {
        "google" => IdentityProvider.Google,
        "microsoft" => IdentityProvider.Microsoft,
        "dev" => IdentityProvider.Dev,
        _ => throw new InvalidOperationException($"Unsupported identity provider '{provider}'."),
    };

    public static string SignupMode(SignupMode mode) => mode switch
    {
        Sharpbill.Domain.Enums.SignupMode.Open => "open",
        Sharpbill.Domain.Enums.SignupMode.Approval => "approval",
        Sharpbill.Domain.Enums.SignupMode.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static SignupMode SignupMode(string mode) => mode switch
    {
        "open" => Sharpbill.Domain.Enums.SignupMode.Open,
        "approval" => Sharpbill.Domain.Enums.SignupMode.Approval,
        "closed" => Sharpbill.Domain.Enums.SignupMode.Closed,
        _ => throw new InvalidOperationException($"Unsupported signup mode '{mode}'."),
    };

    public static string Outcome(SecurityEventOutcome outcome) => outcome switch
    {
        SecurityEventOutcome.Success => "success",
        SecurityEventOutcome.Failure => "failure",
        SecurityEventOutcome.Denied => "denied",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static string Severity(SecurityEventSeverity severity) => severity switch
    {
        SecurityEventSeverity.Info => "info",
        SecurityEventSeverity.Warning => "warning",
        SecurityEventSeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };

    public static string LegalAction(LegalAcceptanceAction action) => action switch
    {
        LegalAcceptanceAction.Agreement => "agreement",
        LegalAcceptanceAction.Acknowledgement => "acknowledgement",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static LegalAcceptanceAction LegalAction(string action) => action switch
    {
        "agreement" => LegalAcceptanceAction.Agreement,
        "acknowledgement" => LegalAcceptanceAction.Acknowledgement,
        _ => throw new InvalidOperationException($"Unsupported legal action '{action}'."),
    };

    public static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public static UserIdentity ToEntity(IdentityRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        Provider = Provider(row.Provider),
        ProviderNamespace = row.ProviderNamespace,
        ProviderSubject = row.ProviderSubject,
        ProviderTenantId = row.ProviderTenantId,
        ProviderHostedDomain = row.ProviderHostedDomain,
        CreatedAt = FromDatabaseUtc(row.CreatedAt),
        UpdatedAt = FromDatabaseUtc(row.UpdatedAt),
    };

    public static Permission ToEntity(PermissionRow row) => new()
    {
        Id = row.Id,
        Key = row.Key,
        Description = row.Description,
        IsSystem = row.IsSystem,
        CreatedAt = FromDatabaseUtc(row.CreatedAt),
        UpdatedAt = FromDatabaseUtc(row.UpdatedAt),
    };
}

internal sealed class IdentityRow
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderNamespace { get; set; } = string.Empty;
    public string ProviderSubject { get; set; } = string.Empty;
    public string? ProviderTenantId { get; set; }
    public string? ProviderHostedDomain { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

internal class PermissionRow
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
