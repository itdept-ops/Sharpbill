using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Exports;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Users;

public sealed class UserQueryService : IUserQueryService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionExecutor _transactions;
    private readonly IUserRepository _users;
    private readonly UserOperationContext _context;
    private readonly UserAuditWriter _audit;
    private readonly IClock _clock;
    private readonly int _exportMaxBytes;
    private readonly IValidator<UserQuery> _queryValidator;

    public UserQueryService(
        IUnitOfWork unitOfWork,
        ITransactionExecutor transactions,
        IUserRepository users,
        UserOperationContext context,
        UserAuditWriter audit,
        IClock clock,
        UserUseCaseOptions options,
        IValidator<UserQuery> queryValidator)
    {
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        _exportMaxBytes = options.ExportMaxBytes;
        _queryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
    }

    public async Task<UserListResponse> ListAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        _queryValidator.Validate(query).ThrowIfInvalid();
        User actor = await _context.RequireActorAsync(
            actorUserId,
            PermissionKeys.UsersRead,
            false,
            cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<User> items, int total) =
            await _users.ListAsync(query, cancellationToken).ConfigureAwait(false);
        DateTime now = _clock.UtcNow;
        return new UserListResponse
        {
            Items = items
                .Select(user => UserResponseMapper.ToResponse(user, actor, now))
                .ToArray(),
            Total = total,
        };
    }

    public async Task<UserResponse> GetAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        User actor = await _context.RequireActorAsync(
            actorUserId,
            null,
            false,
            cancellationToken).ConfigureAwait(false);
        if (userId != actorUserId)
        {
            RbacHierarchyPolicy.RequirePermission(actor, PermissionKeys.UsersRead);
        }

        User target = await _context.FindUserAsync(userId, false, cancellationToken)
            .ConfigureAwait(false);
        return UserResponseMapper.ToResponse(target, actor, _clock.UtcNow);
    }

    public Task<ExportDocument> ExportAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        UserQuery validationQuery = query with { Limit = 100, Offset = 0 };
        _queryValidator.Validate(validationQuery).ThrowIfInvalid();
        return _transactions.ExecuteTransactionAsync(
            _unitOfWork,
            nameof(ExportAsync),
            async transactionToken =>
            {
                User actor = await _context.RequireActorAsync(
                    actorUserId,
                    PermissionKeys.UsersExport,
                    false,
                    transactionToken).ConfigureAwait(false);
                IReadOnlyList<User> users = await _users.ListForExportAsync(
                    query,
                    DomainLimits.MaxExportRows + 1,
                    transactionToken).ConfigureAwait(false);
                if (users.Count > DomainLimits.MaxExportRows)
                {
                    throw new ApiException(
                        413,
                        "EXPORT_TOO_LARGE",
                        $"The export exceeds {DomainLimits.MaxExportRows:N0} rows; narrow the filters and retry");
                }

                bool includeLocation = actor.EffectivePermissionKeys.Contains(PermissionKeys.UsersManage);
                IEnumerable<IReadOnlyList<string>> csvRows = BuildCsvRows(users, includeLocation);
                CsvExportWriter.EnsureWithinLimit(
                    csvRows,
                    _exportMaxBytes,
                    transactionToken);
                await _audit.RecordAsync(
                    "users.exported",
                    actor.Id,
                    "user_collection",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["exported_count"] = users.Count,
                        ["filters_applied"] = query.Search is not null ||
                            query.RoleId is not null ||
                            query.Status is not null ||
                            query.Online is not null,
                    },
                    transactionToken).ConfigureAwait(false);
                return new ExportDocument(
                    "users.csv",
                    "text/csv; charset=utf-8",
                    (destination, writeCancellationToken) => CsvExportWriter.WriteAsync(
                        destination,
                        csvRows,
                        writeCancellationToken));
            },
            cancellationToken);
    }

    private static IEnumerable<IReadOnlyList<string>> BuildCsvRows(
        IReadOnlyList<User> users,
        bool includeLocation)
    {
        yield return
        [
            "id", "email", "display_name", "role", "status", "title", "department",
            "location", "created_at", "last_login_at",
        ];
        foreach (User user in users)
        {
            yield return
            [
                user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                user.Email,
                user.DisplayName ?? string.Empty,
                user.RoleName,
                user.Status.ToString().ToLowerInvariant(),
                user.Title ?? string.Empty,
                user.Department ?? string.Empty,
                includeLocation ? user.Location ?? string.Empty : string.Empty,
                InvariantTimestamp.Format(user.CreatedAt),
                InvariantTimestamp.Format(user.LastLoginAt),
            ];
        }
    }
}
