using Dapper;
using MySqlConnector;

namespace Sharpbill.Migrator;

internal sealed record DemoSeedResult(int NewUsers, int TotalUsers, int NewRoles);

internal static class DemoSeeder
{
    public static bool IsAllowed()
    {
        string? appEnvironment = Environment.GetEnvironmentVariable("APP_ENV");
        if (string.Equals(appEnvironment, "local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return bool.TryParse(
                   Environment.GetEnvironmentVariable("SHARPBILL_ALLOW_DEMO_SEED"),
                   out bool explicitlyAllowed)
               && explicitlyAllowed;
    }

    public static async Task<DemoSeedResult> SeedAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        try
        {
            (long managerId, bool managerCreated) = await EnsureRoleAsync(
                connection,
                transaction,
                "Manager",
                "Team lead: reads the directory and can kick sessions",
                ["users.read", "presence.view", "presence.kick"],
                cancellationToken);
            (long auditorId, bool auditorCreated) = await EnsureRoleAsync(
                connection,
                transaction,
                "Auditor",
                "Read-only oversight: directory + request log",
                ["users.read", "logs.view", "presence.view"],
                cancellationToken);
            long userRoleId = await RequireRoleAsync(
                connection,
                transaction,
                "user",
                cancellationToken);

            var roleIds = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["Manager"] = managerId,
                ["Auditor"] = auditorId,
                ["user"] = userRoleId,
            };

            DateTime now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            int newUsers = 0;
            foreach (DemoPerson person in People)
            {
                string email = $"{person.First}.{person.Last}@example.com".ToLowerInvariant();
                long? existingId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                    "SELECT id FROM users WHERE email = @Email ORDER BY id LIMIT 1",
                    new { Email = email },
                    transaction,
                    cancellationToken: cancellationToken));
                if (existingId is not null)
                {
                    continue;
                }

                DateTime createdAt = now.AddDays(-person.DaysAgo).AddHours(-person.HoursAgo);
                bool active = !string.Equals(person.Status, "disabled", StringComparison.Ordinal);
                bool approved = !string.Equals(person.Status, "pending", StringComparison.Ordinal);
                DateTime? deactivatedAt = active ? null : createdAt;
                DateTime? lastSeenAt = string.Equals(
                    person.Status,
                    "active",
                    StringComparison.Ordinal)
                    ? now
                    : null;

                await connection.ExecuteAsync(new CommandDefinition(
                    InsertUserSql,
                    new
                    {
                        Email = email,
                        DisplayName = $"{person.First} {person.Last}",
                        person.Title,
                        person.Department,
                        person.Location,
                        Timezone = "UTC",
                        Bio = $"{person.Title} on the {person.Department} team.",
                        RoleId = roleIds[person.RoleName],
                        IsActive = active,
                        DeactivatedAt = deactivatedAt,
                        IsApproved = approved,
                        CreatedAt = createdAt,
                        LastLoginAt = createdAt,
                        LastSeenAt = lastSeenAt,
                    },
                    transaction,
                    cancellationToken: cancellationToken));
                long userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT LAST_INSERT_ID()",
                    transaction: transaction,
                    cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO user_identities "
                    + "(user_id, provider, provider_subject, provider_namespace) "
                    + "VALUES (@UserId, 'dev', @Email, '')",
                    new { UserId = userId, Email = email },
                    transaction,
                    cancellationToken: cancellationToken));
                newUsers++;
            }

            int totalUsers = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COUNT(*) FROM users",
                transaction: transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new DemoSeedResult(
                newUsers,
                totalUsers,
                (managerCreated ? 1 : 0) + (auditorCreated ? 1 : 0));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<(long Id, bool Created)> EnsureRoleAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string name,
        string description,
        string[] permissionKeys,
        CancellationToken cancellationToken)
    {
        long? existingId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM roles WHERE name = @Name ORDER BY id LIMIT 1",
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));
        if (existingId is not null)
        {
            return (existingId.Value, false);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO roles (name, description, is_system) VALUES (@Name, @Description, 0)",
            new { Name = name, Description = description },
            transaction,
            cancellationToken: cancellationToken));
        long roleId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT LAST_INSERT_ID()",
            transaction: transaction,
            cancellationToken: cancellationToken));
        int grants = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO role_permissions (role_id, permission_id) "
            + "SELECT @RoleId, id FROM permissions WHERE `key` IN @PermissionKeys",
            new { RoleId = roleId, PermissionKeys = permissionKeys },
            transaction,
            cancellationToken: cancellationToken));
        if (grants != permissionKeys.Length)
        {
            throw new InvalidOperationException(
                $"Cannot create demo role '{name}': one or more canonical permissions are missing.");
        }

        return (roleId, true);
    }

    private static async Task<long> RequireRoleAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string name,
        CancellationToken cancellationToken)
    {
        long? id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM roles WHERE name = @Name ORDER BY id LIMIT 1",
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));
        return id ?? throw new InvalidOperationException(
            $"Cannot seed demo users because the canonical role '{name}' is missing.");
    }

    private const string InsertUserSql =
        """
        INSERT INTO users (
          email, display_name, title, department, location, timezone, bio, role_id,
          is_active, deactivated_at, is_approved, created_at, last_login_at, last_seen_at
        ) VALUES (
          @Email, @DisplayName, @Title, @Department, @Location, @Timezone, @Bio, @RoleId,
          @IsActive, @DeactivatedAt, @IsApproved, @CreatedAt, @LastLoginAt, @LastSeenAt
        )
        """;

    private static readonly DemoPerson[] People =
    [
        new("Maria", "Gonzalez", "Operations", "Ops Lead", "Manager", "active", 10, 3, "Remote"),
        new("David", "Okafor", "Support", "Support Rep", "user", "active", 11, 8, "Austin TX"),
        new("Priya", "Patel", "Success", "CSM", "user", "active", 3, 4, "Miami FL"),
        new("Jordan", "Lee", "Security", "Security Analyst", "Auditor", "active", 1, 21, "Miami FL"),
        new("Sam", "Rivera", "Support", "Support Rep", "user", "active", 8, 2, "Seattle WA"),
        new("Elena", "Novak", "Marketing", "Coordinator", "user", "active", 6, 1, "Remote"),
        new("Marcus", "Brooks", "Operations", "Coordinator", "user", "pending", 1, 6, "Austin TX"),
        new("Hana", "Ito", "Success", "CSM", "user", "pending", 8, 19, "Remote"),
        new("Diego", "Costa", "Support", "Support Rep", "user", "disabled", 8, 6, "Miami FL"),
        new("Nora", "Haddad", "Finance", "Analyst", "Manager", "active", 10, 22, "Seattle WA"),
        new("Owen", "Bauer", "Operations", "Coordinator", "user", "active", 6, 7, "NYC"),
        new("Ruby", "Flores", "Security", "Security Analyst", "Auditor", "active", 9, 8, "Chicago IL"),
    ];

    private sealed record DemoPerson(
        string First,
        string Last,
        string Department,
        string Title,
        string RoleName,
        string Status,
        int DaysAgo,
        int HoursAgo,
        string Location);
}
