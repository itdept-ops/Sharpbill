using System.Diagnostics;
using System.Runtime.CompilerServices;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Application.Users;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Services.Business;

internal static class BusinessServiceSupport
{
    private const int MaximumTransactionAttempts = MySqlTransientRetryExecutor.MaximumAttempts;

    public static async Task<T> InTransactionAsync<T>(
        IUnitOfWork unitOfWork,
        Func<Task<T>> operation,
        CancellationToken cancellationToken,
        [CallerMemberName] string operationName = "business.transaction")
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(operation);
        MySqlTransientRetryExecutor retryExecutor = unitOfWork is DatabaseSession session
            ? session.RetryExecutor
            : MySqlTransientRetryExecutor.Default;
        return await retryExecutor.ExecuteTransactionAsync(
            unitOfWork,
            operationName,
            _ => operation(),
            cancellationToken).ConfigureAwait(false);
    }

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
        CancellationToken cancellationToken,
        [CallerMemberName] string operationName = "business.transaction")
    {
        ArgumentNullException.ThrowIfNull(operation);
        await InTransactionAsync(
            unitOfWork,
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            operationName).ConfigureAwait(false);
    }

    internal static bool IsRetryableTransactionError(MySqlErrorCode errorCode) =>
        MySqlTransientRetryExecutor.IsRetryableError(errorCode);

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
        BusinessErrors.SettingsNotInitialized();

    public static bool IsAuthenticatable(User? user) =>
        UserAccountPolicy.IsAuthenticatable(user);

    public static UserResponse ToUserResponse(
        User user,
        User viewer,
        DateTime now,
        bool? includeLocation = null,
        bool? includeIdentitySubjects = null) =>
        UserResponseMapper.ToResponse(
            user,
            viewer,
            now,
            includeLocation,
            includeIdentitySubjects);

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

    public static IReadOnlyDictionary<string, object?> SummarizeStrings(
        IEnumerable<string> values) =>
        SecurityEventMetadata.SummarizeStrings(values);

    public static SecurityEventWrite SecurityEvent(
        IRequestContextAccessor requestContextAccessor,
        string eventType,
        int actorUserId,
        string targetType,
        object? targetId,
        IReadOnlyDictionary<string, object?> metadata,
        string severity = "info") =>
        SecurityEventWriteFactory.Create(
            requestContextAccessor,
            eventType,
            actorUserId,
            targetType,
            targetId,
            metadata,
            severity);

    public static string IsoTimestamp(DateTime? value) =>
        InvariantTimestamp.Format(value);
}
