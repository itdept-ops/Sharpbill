using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;

namespace Sharpbill.Infrastructure.Repositories;

public sealed class UserRepository(
    DatabaseSession session,
    IOptions<SharpbillOptions> options) : DapperRepository(session), IUserRepository
{
    private const int OnlineWindowSeconds = 90;
    private const string UserColumns = """
        u.id, u.email, u.display_name, u.title, u.department, u.phone, u.location, u.timezone,
        u.bio, u.role_id, u.is_active, u.is_approved, u.access_version,
        u.last_login_at, u.last_seen_at, u.session_valid_after, u.deactivated_at,
        u.erasure_requested_at, u.erasure_due_at, u.erased_at, u.last_latitude,
        u.last_longitude, u.last_location_accuracy, u.last_location_at,
        u.location_retention_until, u.accent_color, u.ui_prefs AS ui_preferences_json,
        u.created_at, u.updated_at
        """;
    private const string Columns = UserColumns + ", r.name AS role_name";

    private readonly RetentionOptions _retention = options.Value.Retention;

    public Task<User?> FindAsync(int userId, bool forUpdate, CancellationToken cancellationToken) =>
        FindCoreAsync("u.id = @Value", userId, forUpdate, cancellationToken);

    public async Task<User?> FindForAuthenticationAsync(
        int userId,
        CancellationToken cancellationToken) =>
        await FindForAuthenticationCoreAsync(
            "u.id = @Value",
            userId,
            cancellationToken).ConfigureAwait(false);

    public async Task<User?> FindByEmailForAuthenticationAsync(
        string email,
        CancellationToken cancellationToken) =>
        await FindForAuthenticationCoreAsync(
            "u.email = @Value",
            email,
            cancellationToken).ConfigureAwait(false);

    private async Task<User?> FindForAuthenticationCoreAsync(
        string predicate,
        object value,
        CancellationToken cancellationToken) =>
        await FindWithAccessLockAsync(
            predicate,
            value,
            "FOR SHARE",
            cancellationToken).ConfigureAwait(false);

    public Task<User?> FindByEmailAsync(
        string email,
        bool forUpdate,
        CancellationToken cancellationToken) =>
        FindCoreAsync("u.email = @Value", email, forUpdate, cancellationToken);

    public async Task<(IReadOnlyList<User> Items, int Total)> ListAsync(
        UserQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int limit = Math.Clamp(query.Limit, 1, 500);
        int offset = Math.Clamp(query.Offset, 0, 10_000);
        (string where, DynamicParameters parameters) = BuildFilters(query);
        parameters.Add("Limit", limit);
        parameters.Add("Offset", offset);

        string countSql = $"""
            SELECT COUNT(*)
            FROM users u
            WHERE {where}
            """;
        string pageSql = $"""
            SELECT {Columns}
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE {where}
            ORDER BY u.created_at, u.id
            LIMIT @Limit OFFSET @Offset
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        int total = await connection.ExecuteScalarAsync<int>(Command(
            countSql,
            parameters,
            cancellationToken)).ConfigureAwait(false);
        UserRow[] rows = (await connection.QueryAsync<UserRow>(Command(
            pageSql,
            parameters,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        IReadOnlyList<User> items = await HydrateAsync(rows, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<IReadOnlyList<User>> ListForExportAsync(
        UserQuery query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        int boundedLimit = Math.Clamp(limit, 1, 10_001);
        (string where, DynamicParameters parameters) = BuildFilters(query);
        parameters.Add("Limit", boundedLimit);
        string sql = $"""
            SELECT {Columns}
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE {where}
            ORDER BY u.created_at, u.id
            LIMIT @Limit
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        UserRow[] rows = (await connection.QueryAsync<UserRow>(Command(
            sql,
            parameters,
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        return await HydrateAsync(rows, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountActiveAdministratorsAsync(
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        string sql = $"""
            SELECT u.id
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE r.name = 'admin' AND u.is_active = 1 AND u.is_approved = 1
            ORDER BY u.id
            {(forUpdate ? "FOR UPDATE" : string.Empty)}
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<int> ids = await connection.QueryAsync<int>(Command(
            sql,
            null,
            cancellationToken)).ConfigureAwait(false);
        return ids.Count();
    }

    public async Task<int> AddAsync(User user, CancellationToken cancellationToken)
    {
        const string insertSql = """
            INSERT INTO users
                (email, display_name, is_active, last_login_at, created_at, updated_at, role_id,
                 last_seen_at, session_valid_after, title, department, phone, location, timezone,
                 bio, is_approved, last_latitude, last_longitude, last_location_accuracy,
                 last_location_at, accent_color, ui_prefs, access_version, deactivated_at,
                 erasure_requested_at, erasure_due_at, erased_at, location_retention_until)
            VALUES
                (@Email, @DisplayName, @IsActive, @LastLoginAt, @CreatedAt, @UpdatedAt, @RoleId,
                 @LastSeenAt, @SessionValidAfter, @Title, @Department, @Phone, @Location, @Timezone,
                 @Bio, @IsApproved, @LastLatitude, @LastLongitude, @LastLocationAccuracy,
                 @LastLocationAt, @AccentColor, @UiPreferencesJson, @AccessVersion, @DeactivatedAt,
                 @ErasureRequestedAt, @ErasureDueAt, @ErasedAt, @LocationRetentionUntil)
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(insertSql, Parameters(user), cancellationToken))
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(Command(
            "SELECT LAST_INSERT_ID()",
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE users
            SET email = @Email,
                display_name = @DisplayName,
                is_active = @IsActive,
                last_login_at = @LastLoginAt,
                updated_at = @UpdatedAt,
                role_id = @RoleId,
                last_seen_at = @LastSeenAt,
                session_valid_after = @SessionValidAfter,
                title = @Title,
                department = @Department,
                phone = @Phone,
                location = @Location,
                timezone = @Timezone,
                bio = @Bio,
                is_approved = @IsApproved,
                last_latitude = @LastLatitude,
                last_longitude = @LastLongitude,
                last_location_accuracy = @LastLocationAccuracy,
                last_location_at = @LastLocationAt,
                accent_color = @AccentColor,
                ui_prefs = @UiPreferencesJson,
                access_version = @AccessVersion,
                deactivated_at = @DeactivatedAt,
                erasure_requested_at = @ErasureRequestedAt,
                erasure_due_at = @ErasureDueAt,
                erased_at = @ErasedAt,
                location_retention_until = @LocationRetentionUntil
            WHERE id = @Id
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        _ = await connection.ExecuteAsync(Command(sql, Parameters(user), cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task ReplaceDirectPermissionsAsync(
        int userId,
        IReadOnlyCollection<int> permissionIds,
        CancellationToken cancellationToken)
    {
        int[] ids = permissionIds.Distinct().Order().ToArray();
        await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            _ = await connection.ExecuteAsync(TransactionalCommand(
                "DELETE FROM user_permissions WHERE user_id = @UserId",
                new { UserId = userId },
                transaction,
                token)).ConfigureAwait(false);
            if (ids.Length > 0)
            {
                const string insertSql = """
                    INSERT INTO user_permissions (user_id, permission_id)
                    VALUES (@UserId, @PermissionId)
                    """;
                _ = await connection.ExecuteAsync(TransactionalCommand(
                    insertSql,
                    ids.Select(permissionId => new { UserId = userId, PermissionId = permissionId }),
                    transaction,
                    token)).ConfigureAwait(false);
            }

            int versioned = await connection.ExecuteAsync(TransactionalCommand(
                "UPDATE users SET access_version = access_version + 1 WHERE id = @UserId",
                new { UserId = userId },
                transaction,
                token)).ConfigureAwait(false);
            if (versioned != 1)
            {
                throw new DBConcurrencyException(
                    "The user disappeared before direct permissions could be replaced.");
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ClearExpiredLocationsAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 5_000);
        DateTime policyCutoff = now.AddHours(-_retention.PreciseLocationHours);
        return await Session.ExecuteTransactionallyAsync(async (connection, transaction, token) =>
        {
            if (await RetentionSql.IsHoldActiveAsync(connection, transaction, token).ConfigureAwait(false))
            {
                return 0;
            }

            const string selectSql = """
                SELECT id
                FROM users
                WHERE location_retention_until <= @Now
                   OR last_location_at <= @PolicyCutoff
                   OR (last_location_at IS NULL AND
                       (last_latitude IS NOT NULL OR last_longitude IS NOT NULL OR
                        last_location_accuracy IS NOT NULL))
                ORDER BY
                    CASE
                        WHEN location_retention_until <= @Now THEN 0
                        WHEN last_location_at <= @PolicyCutoff THEN 1
                        ELSE 2
                    END,
                    COALESCE(location_retention_until, last_location_at), id
                LIMIT @Limit
                FOR UPDATE SKIP LOCKED
                """;
            int[] ids = (await connection.QueryAsync<int>(TransactionalCommand(
                selectSql,
                new
                {
                    Now = RepositoryMapping.ToDatabaseUtc(now),
                    PolicyCutoff = RepositoryMapping.ToDatabaseUtc(policyCutoff),
                    Limit = boundedLimit,
                },
                transaction,
                token)).ConfigureAwait(false)).AsList().ToArray();
            if (ids.Length == 0)
            {
                return 0;
            }

            const string updateSql = """
                UPDATE users
                SET last_latitude = NULL,
                    last_longitude = NULL,
                    last_location_accuracy = NULL,
                    last_location_at = NULL,
                    location_retention_until = NULL
                WHERE id IN @Ids
                """;
            return await connection.ExecuteAsync(TransactionalCommand(
                updateSql,
                new { Ids = ids },
                transaction,
                token)).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<User>> ClaimDueForAnonymizationAsync(
        DateTime now,
        int limit,
        CancellationToken cancellationToken)
    {
        var transaction = Session.Transaction ?? throw new InvalidOperationException(
            "Claiming accounts for anonymization requires an active unit-of-work transaction.");
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (await RetentionSql.IsHoldActiveAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false))
        {
            return [];
        }

        DateTime pendingCutoff = now.AddDays(-_retention.PendingAccountDays);
        DateTime disabledCutoff = now.AddDays(-_retention.DisabledAccountDays);
        int boundedLimit = Math.Clamp(limit, 1, 1_000);
        string sql = $"""
            SELECT {Columns}
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE u.erased_at IS NULL
              AND r.name <> 'admin'
              AND (
                    u.erasure_due_at <= @Now
                 OR (u.is_approved = 0 AND u.created_at <= @PendingCutoff)
                 OR (u.is_active = 0 AND u.deactivated_at IS NOT NULL
                     AND u.deactivated_at <= @DisabledCutoff)
              )
            ORDER BY
                CASE
                    WHEN u.erasure_due_at <= @Now THEN 0
                    WHEN u.is_approved = 0 AND u.created_at <= @PendingCutoff THEN 1
                    ELSE 2
                END,
                u.id
            LIMIT @Limit
            FOR UPDATE SKIP LOCKED
            """;
        UserRow[] rows = (await connection.QueryAsync<UserRow>(Command(sql, new
        {
            Now = RepositoryMapping.ToDatabaseUtc(now),
            PendingCutoff = RepositoryMapping.ToDatabaseUtc(pendingCutoff),
            DisabledCutoff = RepositoryMapping.ToDatabaseUtc(disabledCutoff),
            Limit = boundedLimit,
        }, cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        return await HydrateAsync(rows, "FOR UPDATE", cancellationToken).ConfigureAwait(false);
    }

    private async Task<User?> FindCoreAsync(
        string predicate,
        object value,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        if (forUpdate)
        {
            return await FindWithAccessLockAsync(
                predicate,
                value,
                "FOR UPDATE",
                cancellationToken).ConfigureAwait(false);
        }

        string sql = $"""
            SELECT {Columns}
            FROM users u
            INNER JOIN roles r ON r.id = u.role_id
            WHERE {predicate}
            ORDER BY u.id
            LIMIT 1
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        UserRow? row = await connection.QuerySingleOrDefaultAsync<UserRow>(Command(
            sql,
            new { Value = value },
            cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        IReadOnlyList<User> users = await HydrateAsync(
            [row],
            string.Empty,
            cancellationToken)
            .ConfigureAwait(false);
        return users[0];
    }

    private async Task<User?> FindWithAccessLockAsync(
        string predicate,
        object value,
        string accessLockClause,
        CancellationToken cancellationToken)
    {
        string preliminarySql = $"""
            SELECT u.id, u.role_id
            FROM users u
            WHERE {predicate}
            ORDER BY u.id
            LIMIT 1
            """;
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        PreliminaryUserRow? preliminary =
            await connection.QuerySingleOrDefaultAsync<PreliminaryUserRow>(Command(
                preliminarySql,
                new { Value = value },
                cancellationToken)).ConfigureAwait(false);
        if (preliminary is null)
        {
            return null;
        }

        string roleSql = $"""
            SELECT name
            FROM roles
            WHERE id = @RoleId
            {accessLockClause}
            """;
        string? roleName = await connection.QuerySingleOrDefaultAsync<string>(Command(
            roleSql,
            new { preliminary.RoleId },
            cancellationToken)).ConfigureAwait(false);
        if (roleName is null)
        {
            throw new InvalidOperationException("A user references an unavailable role.");
        }

        string rolePermissionsSql = $"""
            SELECT p.id
            FROM role_permissions rp
            INNER JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.role_id = @RoleId
            ORDER BY p.id
            {accessLockClause}
            """;
        _ = (await connection.QueryAsync<int>(Command(
            rolePermissionsSql,
            new { preliminary.RoleId },
            cancellationToken)).ConfigureAwait(false)).AsList();
        string directPermissionsSql = $"""
            SELECT p.id
            FROM user_permissions up
            INNER JOIN permissions p ON p.id = up.permission_id
            WHERE up.user_id = @UserId
            ORDER BY p.id
            {accessLockClause}
            """;
        _ = (await connection.QueryAsync<int>(Command(
            directPermissionsSql,
            new { UserId = preliminary.Id },
            cancellationToken)).ConfigureAwait(false)).AsList();

        string userSql = $"""
            SELECT {UserColumns}
            FROM users u
            WHERE u.id = @UserId
            LIMIT 1
            {accessLockClause}
            """;
        UserRow? row = await connection.QuerySingleOrDefaultAsync<UserRow>(Command(
            userSql,
            new { UserId = preliminary.Id },
            cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (row.RoleId != preliminary.RoleId)
        {
            throw new DBConcurrencyException(
                "The user's role changed while its access snapshot was being locked.");
        }

        row.RoleName = roleName;
        IReadOnlyList<User> users = await HydrateAsync(
            [row],
            accessLockClause,
            cancellationToken).ConfigureAwait(false);
        return users[0];
    }

    private async Task<IReadOnlyList<User>> HydrateAsync(
        IReadOnlyList<UserRow> rows,
        string lockClause,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        int[] userIds = rows.Select(static row => row.Id).ToArray();
        int[] roleIds = rows.Select(static row => row.RoleId).Distinct().ToArray();
        var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        string identitySql = $"""
            SELECT id, user_id, provider, provider_namespace, provider_subject,
                   provider_tenant_id, provider_hosted_domain, created_at, updated_at
            FROM user_identities
            WHERE user_id IN @UserIds
            ORDER BY user_id, id
            {lockClause}
            """;
        string rolePermissionSql = $"""
            SELECT rp.role_id AS owner_id, p.`key`
            FROM role_permissions rp
            INNER JOIN permissions p ON p.id = rp.permission_id
            WHERE rp.role_id IN @RoleIds
            ORDER BY rp.role_id, p.`key`
            {lockClause}
            """;
        string directPermissionSql = $"""
            SELECT up.user_id AS owner_id, p.`key`
            FROM user_permissions up
            INNER JOIN permissions p ON p.id = up.permission_id
            WHERE up.user_id IN @UserIds
            ORDER BY up.user_id, p.`key`
            {lockClause}
            """;
        IdentityRow[] identityRows = (await connection.QueryAsync<IdentityRow>(Command(
            identitySql,
            new { UserIds = userIds },
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        PermissionKeyRow[] rolePermissionRows = (await connection.QueryAsync<PermissionKeyRow>(Command(
            rolePermissionSql,
            new { RoleIds = roleIds },
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();
        PermissionKeyRow[] directPermissionRows = (await connection.QueryAsync<PermissionKeyRow>(Command(
            directPermissionSql,
            new { UserIds = userIds },
            cancellationToken)).ConfigureAwait(false)).AsList().ToArray();

        IReadOnlyDictionary<int, UserIdentity[]> identities = identityRows
            .GroupBy(static row => row.UserId)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(RepositoryMapping.ToEntity).ToArray());
        IReadOnlyDictionary<int, HashSet<string>> rolePermissions = ToPermissionLookup(rolePermissionRows);
        IReadOnlyDictionary<int, HashSet<string>> directPermissions = ToPermissionLookup(directPermissionRows);

        return rows.Select(row => ToEntity(
            row,
            identities.GetValueOrDefault(row.Id, []),
            rolePermissions.GetValueOrDefault(row.RoleId, new HashSet<string>(StringComparer.Ordinal)),
            directPermissions.GetValueOrDefault(row.Id, new HashSet<string>(StringComparer.Ordinal))))
            .ToArray();
    }

    private static Dictionary<int, HashSet<string>> ToPermissionLookup(
        IEnumerable<PermissionKeyRow> rows) => rows
        .GroupBy(static row => row.OwnerId)
        .ToDictionary(
            static group => group.Key,
            static group => group.Select(static row => row.Key).ToHashSet(StringComparer.Ordinal));

    private static (string Where, DynamicParameters Parameters) BuildFilters(UserQuery query)
    {
        List<string> conditions = ["1 = 1"];
        DynamicParameters parameters = new();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add("(u.email LIKE @Search ESCAPE '\\\\' OR COALESCE(u.display_name, '') LIKE @Search ESCAPE '\\\\')");
            parameters.Add("Search", $"%{RepositoryMapping.EscapeLike(query.Search.Trim())}%");
        }

        if (query.RoleId is not null)
        {
            conditions.Add("u.role_id = @RoleId");
            parameters.Add("RoleId", query.RoleId.Value);
        }

        switch (query.Status)
        {
            case "active":
                conditions.Add("u.is_active = 1 AND u.is_approved = 1");
                break;
            case "pending":
                conditions.Add("u.is_approved = 0");
                break;
            case "disabled":
                conditions.Add("u.is_active = 0 AND u.is_approved = 1");
                break;
        }

        if (query.Online is not null)
        {
            DateTime cutoff = DateTime.UtcNow.AddSeconds(-OnlineWindowSeconds);
            parameters.Add("OnlineCutoff", RepositoryMapping.ToDatabaseUtc(cutoff));
            conditions.Add(query.Online.Value
                ? "u.is_active = 1 AND u.is_approved = 1 AND u.last_seen_at >= @OnlineCutoff"
                : "(u.last_seen_at IS NULL OR u.last_seen_at < @OnlineCutoff)");
        }

        return (string.Join(" AND ", conditions), parameters);
    }

    private static object Parameters(User user) => new
    {
        user.Id,
        user.Email,
        user.DisplayName,
        user.IsActive,
        LastLoginAt = ToDatabaseUtc(user.LastLoginAt),
        CreatedAt = RepositoryMapping.ToDatabaseUtc(user.CreatedAt),
        UpdatedAt = RepositoryMapping.ToDatabaseUtc(user.UpdatedAt),
        user.RoleId,
        LastSeenAt = ToDatabaseUtc(user.LastSeenAt),
        SessionValidAfter = ToDatabaseUtc(user.SessionValidAfter),
        user.Title,
        user.Department,
        user.Phone,
        user.Location,
        user.Timezone,
        user.Bio,
        user.IsApproved,
        user.LastLatitude,
        user.LastLongitude,
        user.LastLocationAccuracy,
        LastLocationAt = ToDatabaseUtc(user.LastLocationAt),
        user.AccentColor,
        UiPreferencesJson = user.UiPreferences is null ? null : RepositoryMapping.Serialize(user.UiPreferences),
        user.AccessVersion,
        DeactivatedAt = ToDatabaseUtc(user.DeactivatedAt),
        ErasureRequestedAt = ToDatabaseUtc(user.ErasureRequestedAt),
        ErasureDueAt = ToDatabaseUtc(user.ErasureDueAt),
        ErasedAt = ToDatabaseUtc(user.ErasedAt),
        LocationRetentionUntil = ToDatabaseUtc(user.LocationRetentionUntil),
    };

    private static DateTime? ToDatabaseUtc(DateTime? value) =>
        value is null ? null : RepositoryMapping.ToDatabaseUtc(value.Value);

    private static User ToEntity(
        UserRow row,
        IReadOnlyList<UserIdentity> identities,
        IReadOnlySet<string> rolePermissions,
        IReadOnlySet<string> directPermissions) => new()
        {
            Id = row.Id,
            Email = row.Email,
            DisplayName = row.DisplayName,
            Title = row.Title,
            Department = row.Department,
            Phone = row.Phone,
            Location = row.Location,
            Timezone = row.Timezone,
            Bio = row.Bio,
            RoleId = row.RoleId,
            RoleName = row.RoleName,
            IsActive = row.IsActive,
            IsApproved = row.IsApproved,
            AccessVersion = row.AccessVersion,
            LastLoginAt = RepositoryMapping.FromDatabaseUtc(row.LastLoginAt),
            LastSeenAt = RepositoryMapping.FromDatabaseUtc(row.LastSeenAt),
            SessionValidAfter = RepositoryMapping.FromDatabaseUtc(row.SessionValidAfter),
            DeactivatedAt = RepositoryMapping.FromDatabaseUtc(row.DeactivatedAt),
            ErasureRequestedAt = RepositoryMapping.FromDatabaseUtc(row.ErasureRequestedAt),
            ErasureDueAt = RepositoryMapping.FromDatabaseUtc(row.ErasureDueAt),
            ErasedAt = RepositoryMapping.FromDatabaseUtc(row.ErasedAt),
            LastLatitude = row.LastLatitude,
            LastLongitude = row.LastLongitude,
            LastLocationAccuracy = row.LastLocationAccuracy,
            LastLocationAt = RepositoryMapping.FromDatabaseUtc(row.LastLocationAt),
            LocationRetentionUntil = RepositoryMapping.FromDatabaseUtc(row.LocationRetentionUntil),
            AccentColor = row.AccentColor,
            UiPreferences = RepositoryMapping.DeserializeUiPreferences(row.UiPreferencesJson),
            CreatedAt = RepositoryMapping.FromDatabaseUtc(row.CreatedAt),
            UpdatedAt = RepositoryMapping.FromDatabaseUtc(row.UpdatedAt),
            Identities = identities,
            RolePermissionKeys = rolePermissions,
            DirectPermissionKeys = directPermissions,
        };

    private sealed class UserRow
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string? Title { get; set; }
        public string? Department { get; set; }
        public string? Phone { get; set; }
        public string? Location { get; set; }
        public string? Timezone { get; set; }
        public string? Bio { get; set; }
        public int RoleId { get; set; }
        public string RoleName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool IsApproved { get; set; }
        public int AccessVersion { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime? SessionValidAfter { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public DateTime? ErasureRequestedAt { get; set; }
        public DateTime? ErasureDueAt { get; set; }
        public DateTime? ErasedAt { get; set; }
        public double? LastLatitude { get; set; }
        public double? LastLongitude { get; set; }
        public double? LastLocationAccuracy { get; set; }
        public DateTime? LastLocationAt { get; set; }
        public DateTime? LocationRetentionUntil { get; set; }
        public string? AccentColor { get; set; }
        public string? UiPreferencesJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    private sealed class PreliminaryUserRow
    {
        public int Id { get; set; }
        public int RoleId { get; set; }
    }

    private sealed class PermissionKeyRow
    {
        public int OwnerId { get; set; }
        public string Key { get; set; } = string.Empty;
    }
}
