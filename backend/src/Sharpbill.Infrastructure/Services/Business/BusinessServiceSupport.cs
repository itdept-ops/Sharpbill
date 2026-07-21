using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Infrastructure.Services.Business;

internal static class BusinessServiceSupport
{
    private const int MaximumTransactionAttempts = 3;
    public const int OnlineWindowSeconds = 90;

    public static async Task<T> InTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        Func<Task<T>> operation,
        CancellationToken cancellationToken) =>
        await InTransactionAsync(
            unitOfWork,
            operation,
            IsRetryableTransactionException,
            DelayBeforeRetryAsync,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<T> InTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        Func<Task<T>> operation,
        Func<Exception, bool> isRetryable,
        Func<int, CancellationToken, Task> delayBeforeRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(isRetryable);
        ArgumentNullException.ThrowIfNull(delayBeforeRetry);

        for (int attempt = 1; attempt <= MaximumTransactionAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                T result = await operation().ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception)
            {
                await RollbackPreservingOriginalErrorAsync(unitOfWork).ConfigureAwait(false);
                if (attempt == MaximumTransactionAttempts ||
                    cancellationToken.IsCancellationRequested ||
                    !isRetryable(exception))
                {
                    throw;
                }

                await delayBeforeRetry(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new UnreachableException();
    }

    public static async Task InTransactionAsync(
        IUnitOfWork unitOfWork,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await InTransactionAsync(
            unitOfWork,
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsRetryableTransactionError(MySqlErrorCode errorCode) =>
        errorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout;

    private static bool IsRetryableTransactionException(Exception exception) =>
        exception is MySqlException mysqlException &&
        IsRetryableTransactionError(mysqlException.ErrorCode);

    private static Task DelayBeforeRetryAsync(int failedAttempt, CancellationToken cancellationToken)
    {
        int exponentialMilliseconds = Math.Min(100, 25 * (1 << (failedAttempt - 1)));
        int jitterMilliseconds = Random.Shared.Next(0, 26);
        return Task.Delay(
            TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds),
            cancellationToken);
    }

    private static async Task RollbackPreservingOriginalErrorAsync(IUnitOfWork unitOfWork)
    {
        try
        {
            await unitOfWork.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The initiating database error is the actionable failure. DatabaseSession releases
            // the failed transaction in a finally block so a retry can still start cleanly.
        }
    }

    public static ApiException SettingsNotInitialized() =>
        new(500, "SETTINGS_NOT_INITIALIZED", "Site settings are not initialized");

    public static bool IsAuthenticatable(User? user) =>
        user is { IsActive: true, IsApproved: true, ErasedAt: null };

    public static UserResponse ToUserResponse(
        User user,
        User viewer,
        DateTime now,
        bool? includeLocation = null,
        bool? includeIdentitySubjects = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(viewer);

        bool showLocation = includeLocation ??
            (user.Id == viewer.Id || viewer.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage));
        bool showIdentitySubjects = includeIdentitySubjects ??
            (user.Id == viewer.Id || RbacHierarchyPolicy.IsAdministrator(viewer));
        IReadOnlyList<ProviderContract> providers = user.Identities
            .Select(static identity => ToProviderContract(identity.Provider))
            .Distinct()
            .ToArray();

        return new UserResponse
        {
            Id = user.Id,
            Email = user.Email,
            DisplayName = user.DisplayName,
            Title = user.Title,
            Department = user.Department,
            Phone = user.Phone,
            Location = showLocation ? user.Location : null,
            Timezone = showLocation ? user.Timezone : null,
            Bio = user.Bio,
            AccentColor = user.AccentColor,
            UiPreferences = ToUiPreferencesContract(user.UiPreferences),
            Role = user.RoleName,
            RoleId = user.RoleId,
            Permissions = user.EffectivePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            RolePermissions = user.RolePermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            DirectPermissions = user.DirectPermissionKeys.Order(StringComparer.Ordinal).ToArray(),
            AccessVersion = user.AccessVersion,
            IsActive = user.IsActive,
            IsApproved = user.IsApproved,
            Status = ToUserStatusContract(user.Status),
            Identities = showIdentitySubjects
                ? user.Identities.Select(static identity => new IdentityResponse
                {
                    Provider = ToProviderContract(identity.Provider),
                    Namespace = string.IsNullOrEmpty(identity.ProviderNamespace)
                        ? null
                        : identity.ProviderNamespace,
                    Subject = identity.ProviderSubject,
                }).ToArray()
                : [],
            AuthProviders = providers,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            LastSeenAt = user.LastSeenAt,
            Online = user.LastSeenAt is { } lastSeen &&
                lastSeen >= now.AddSeconds(-OnlineWindowSeconds),
            LastLatitude = showLocation ? user.LastLatitude : null,
            LastLongitude = showLocation ? user.LastLongitude : null,
            LastLocationAccuracy = showLocation ? user.LastLocationAccuracy : null,
            LastLocationAt = showLocation ? user.LastLocationAt : null,
        };
    }

    public static RoleResponse ToRoleResponse(Role role, int userCount) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        IsSystem = role.IsSystem,
        Permissions = role.Permissions
            .OrderBy(static permission => permission.Key, StringComparer.Ordinal)
            .Select(ToPermissionResponse)
            .ToArray(),
        UserCount = userCount,
        Version = role.Version,
    };

    public static PermissionResponse ToPermissionResponse(Permission permission) => new()
    {
        Id = permission.Id,
        Key = permission.Key,
        Description = permission.Description,
        IsSystem = permission.IsSystem,
    };

    public static IReadOnlyDictionary<string, object?> SummarizeStrings(IEnumerable<string> values)
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

    public static SecurityEventWrite SecurityEvent(
        IRequestContextAccessor requestContextAccessor,
        string eventType,
        int actorUserId,
        string targetType,
        object? targetId,
        IReadOnlyDictionary<string, object?> metadata,
        string severity = "info")
    {
        ArgumentNullException.ThrowIfNull(requestContextAccessor);
        RequestContext context = requestContextAccessor.Current;
        return new SecurityEventWrite
        {
            EventType = eventType,
            Outcome = "success",
            Severity = severity,
            ActorUserId = actorUserId,
            TargetType = targetType,
            TargetId = targetId is null
                ? null
                : Convert.ToString(targetId, CultureInfo.InvariantCulture),
            RequestId = context.RequestId,
            SourceIp = context.IpAddress,
            Metadata = metadata,
        };
    }

    public static string CsvSafe(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? $"'{value}"
            : value;

    public static string CsvCell(string value)
    {
        string safe = CsvSafe(value);
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : safe;
    }

    public static string IsoTimestamp(DateTime? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        string rendered = value.Value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffff",
            CultureInfo.InvariantCulture);
        return rendered.TrimEnd('0').TrimEnd('.');
    }

    private static ProviderContract ToProviderContract(IdentityProvider provider) => provider switch
    {
        IdentityProvider.Google => ProviderContract.Google,
        IdentityProvider.Microsoft => ProviderContract.Microsoft,
        IdentityProvider.Dev => ProviderContract.Dev,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider"),
    };

    private static UserStatusContract ToUserStatusContract(UserStatus status) => status switch
    {
        UserStatus.Active => UserStatusContract.Active,
        UserStatus.Pending => UserStatusContract.Pending,
        UserStatus.Disabled => UserStatusContract.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown user status"),
    };

    private static UiPreferencesContract? ToUiPreferencesContract(UiPreferences? preferences)
    {
        if (preferences is null)
        {
            return null;
        }

        var contract = new UiPreferencesContract();
        if (preferences.BaseTone is not null)
        {
            contract = contract with { BaseTone = preferences.BaseTone };
        }

        if (preferences.BackgroundDepth is not null)
        {
            contract = contract with { BackgroundDepth = preferences.BackgroundDepth };
        }

        if (preferences.BorderGlow is not null)
        {
            contract = contract with { BorderGlow = preferences.BorderGlow };
        }

        if (preferences.GlowIntensity is not null)
        {
            contract = contract with { GlowIntensity = preferences.GlowIntensity };
        }

        if (preferences.Scanlines is not null)
        {
            contract = contract with { Scanlines = preferences.Scanlines };
        }

        if (preferences.CornerRadius is not null)
        {
            contract = contract with { CornerRadius = preferences.CornerRadius };
        }

        if (preferences.Motion is not null)
        {
            contract = contract with { Motion = preferences.Motion };
        }

        if (preferences.RainDensity is not null)
        {
            contract = contract with { RainDensity = preferences.RainDensity };
        }

        if (preferences.RainSpeed is not null)
        {
            contract = contract with { RainSpeed = preferences.RainSpeed };
        }

        if (preferences.RainGlyphs is not null)
        {
            contract = contract with { RainGlyphs = preferences.RainGlyphs };
        }

        if (preferences.FontFamily is not null)
        {
            contract = contract with { FontFamily = preferences.FontFamily };
        }

        if (preferences.TextScale is not null)
        {
            contract = contract with { TextScale = preferences.TextScale };
        }

        if (preferences.Density is not null)
        {
            contract = contract with { Density = preferences.Density };
        }

        if (preferences.HighContrastText is not null)
        {
            contract = contract with { HighContrastText = preferences.HighContrastText };
        }

        if (preferences.ReduceTransparency is not null)
        {
            contract = contract with { ReduceTransparency = preferences.ReduceTransparency };
        }

        if (preferences.FocusRing is not null)
        {
            contract = contract with { FocusRing = preferences.FocusRing };
        }

        if (preferences.ZebraRows is not null)
        {
            contract = contract with { ZebraRows = preferences.ZebraRows };
        }

        if (preferences.LinkUnderlines is not null)
        {
            contract = contract with { LinkUnderlines = preferences.LinkUnderlines };
        }

        if (preferences.Version is not null)
        {
            contract = contract with { Version = preferences.Version };
        }

        return contract;
    }
}
