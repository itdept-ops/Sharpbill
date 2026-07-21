using System.Text;

namespace Sharpbill.Migrator;

internal sealed record ValidationIssue(string Category, string Kind, string Fact)
{
    public override string ToString()
    {
        return $"{Category}: {Kind} {Fact}";
    }
}

internal sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

internal static class SchemaComparison
{
    public static IReadOnlyList<ValidationIssue> CompareExact(
        string category,
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        var expectedSet = new SortedSet<string>(expected, StringComparer.Ordinal);
        var actualSet = new SortedSet<string>(actual, StringComparer.Ordinal);
        var issues = new List<ValidationIssue>();

        foreach (string missing in expectedSet.Except(actualSet, StringComparer.Ordinal))
        {
            issues.Add(new ValidationIssue(category, "missing", missing));
        }

        foreach (string unexpected in actualSet.Except(expectedSet, StringComparer.Ordinal))
        {
            issues.Add(new ValidationIssue(category, "unexpected", unexpected));
        }

        return issues;
    }
}

internal static class SchemaValidator
{
    private const string HistoryTable = "sharpbill_schema_history";
    private const int MaximumReportedIssues = 50;

    public static ValidationResult Validate(DatabaseSchema actual, SeedSnapshot seeds)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(seeds);

        var issues = new List<ValidationIssue>();
        Add(issues, SchemaComparison.CompareExact(
            "tables",
            ExpectedTables,
            actual.Tables
                .Where(table => !IsHistoryTable(table.Name))
                .Select(TableFact)));
        Add(issues, SchemaComparison.CompareExact(
            "columns",
            ExpectedColumnNames,
            actual.Columns
                .Where(column => !IsHistoryTable(column.TableName))
                .Select(ColumnNameFact)));
        Add(issues, ValidateCriticalColumns(actual.Columns));
        Add(issues, SchemaComparison.CompareExact(
            "indexes",
            ExpectedIndexes,
            actual.Indexes
                .Where(index => !IsHistoryTable(index.TableName))
                .Select(IndexFact)));
        Add(issues, SchemaComparison.CompareExact(
            "foreign keys",
            ExpectedForeignKeys,
            actual.ForeignKeys
                .Where(key => !IsHistoryTable(key.TableName))
                .Select(ForeignKeyFact)));
        Add(issues, SchemaComparison.CompareExact(
            "check constraints",
            ExpectedChecks,
            actual.Checks
                .Where(check => !IsHistoryTable(check.TableName))
                .Select(CheckFact)));
        Add(issues, ValidateSeeds(seeds));

        return new ValidationResult(issues.Take(MaximumReportedIssues).ToArray());
    }

    private static IReadOnlyList<ValidationIssue> ValidateCriticalColumns(
        IReadOnlyList<ColumnMetadata> actualColumns)
    {
        var actualFacts = actualColumns
            .Where(column => CriticalColumnKeys.Contains(
                $"{column.TableName}.{column.Name}",
                StringComparer.Ordinal))
            .Select(ColumnDefinitionFact);
        return SchemaComparison.CompareExact(
            "critical column definitions",
            CriticalColumns.Select(ColumnDefinitionFact),
            actualFacts);
    }

    private static List<ValidationIssue> ValidateSeeds(SeedSnapshot seeds)
    {
        var issues = new List<ValidationIssue>();
        Add(issues, SchemaComparison.CompareExact(
            "system permission seeds",
            ExpectedPermissions,
            seeds.Permissions.Select(PermissionFact)));
        Add(issues, SchemaComparison.CompareExact(
            "system role seeds",
            ExpectedRoles,
            seeds.Roles.Select(RoleFact)));
        Add(issues, SchemaComparison.CompareExact(
            "system role grants",
            ExpectedRolePermissions,
            seeds.RolePermissions.Select(grant => $"{grant.RoleId}|{grant.PermissionId}")));

        if (seeds.SiteSettings.Count != 1)
        {
            issues.Add(new ValidationIssue(
                "site settings seed",
                "invalid",
                $"expected singleton id=1; found {seeds.SiteSettings.Count} rows"));
            return issues;
        }

        SiteSettingsSeed settings = seeds.SiteSettings[0];
        if (settings.Id != 1)
        {
            issues.Add(new ValidationIssue(
                "site settings seed",
                "invalid",
                $"expected id=1; found id={settings.Id}"));
        }

        RoleSeed? defaultRole = seeds.Roles.SingleOrDefault(role => role.Id == settings.DefaultRoleId);
        if (defaultRole is null || string.Equals(defaultRole.Name, "admin", StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue(
                "site settings seed",
                "invalid",
                "default_role_id must reference the non-admin built-in user role"));
        }

        return issues;
    }

    private static string TableFact(TableMetadata table)
    {
        return $"{table.Name}|{table.Engine}|{table.Collation}";
    }

    private static string ColumnNameFact(ColumnMetadata column)
    {
        return $"{column.TableName}|{column.Ordinal:D3}|{column.Name}";
    }

    private static string ColumnDefinitionFact(ColumnMetadata column)
    {
        return string.Join(
            '|',
            column.TableName,
            column.Ordinal.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            column.Name,
            column.ColumnType.ToLowerInvariant(),
            column.IsNullable ? "YES" : "NO",
            NormalizeDefault(column.DefaultValue),
            NormalizeWhitespace(column.Extra),
            column.Collation ?? "<NULL>");
    }

    private static string IndexFact(IndexMetadata index)
    {
        return string.Join(
            '|',
            index.TableName,
            index.Name,
            index.IsUnique ? "UNIQUE" : "NONUNIQUE",
            index.Type.ToUpperInvariant(),
            string.Join(',', index.Columns));
    }

    private static string ForeignKeyFact(ForeignKeyMetadata foreignKey)
    {
        return string.Join(
            '|',
            foreignKey.TableName,
            foreignKey.Name,
            string.Join(',', foreignKey.Columns),
            foreignKey.ReferencedTable,
            string.Join(',', foreignKey.ReferencedColumns),
            foreignKey.DeleteRule.ToUpperInvariant(),
            foreignKey.UpdateRule.ToUpperInvariant());
    }

    private static string CheckFact(CheckMetadata check)
    {
        return $"{check.TableName}|{check.Name}|{NormalizeCheckClause(check.Clause)}";
    }

    private static string PermissionFact(PermissionSeed permission)
    {
        return string.Join(
            '|',
            permission.Id,
            permission.Key,
            permission.Description ?? "<NULL>",
            permission.IsSystem ? "1" : "0");
    }

    private static string RoleFact(RoleSeed role)
    {
        return string.Join(
            '|',
            role.Id,
            role.Name,
            role.Description ?? "<NULL>",
            role.IsSystem ? "1" : "0",
            role.Version);
    }

    private static string NormalizeDefault(string? value)
    {
        return value is null ? "<NULL>" : NormalizeWhitespace(value).ToLowerInvariant();
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool previousWasWhitespace = false;
        foreach (char character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }

                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static string NormalizeCheckClause(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (!char.IsWhiteSpace(character)
                && character is not '`' and not '(' and not ')' and not '\\')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder
            .Replace("_utf8mb4", string.Empty)
            .Replace("dateaccepted_at", "castaccepted_atasdate")
            .ToString();
    }

    private static bool IsHistoryTable(string tableName)
    {
        return string.Equals(tableName, HistoryTable, StringComparison.Ordinal);
    }

    private static void Add(List<ValidationIssue> destination, IEnumerable<ValidationIssue> source)
    {
        destination.AddRange(source);
    }

    private static readonly string[] ExpectedTables =
    [
        "alembic_version|InnoDB|utf8mb4_0900_ai_ci",
        "legal_acceptances|InnoDB|utf8mb4_0900_ai_ci",
        "login_nonces|InnoDB|utf8mb4_0900_ai_ci",
        "permissions|InnoDB|utf8mb4_0900_ai_ci",
        "request_logs|InnoDB|utf8mb4_0900_ai_ci",
        "role_permissions|InnoDB|utf8mb4_0900_ai_ci",
        "roles|InnoDB|utf8mb4_0900_ai_ci",
        "security_event_deliveries|InnoDB|utf8mb4_0900_ai_ci",
        "security_events|InnoDB|utf8mb4_0900_ai_ci",
        "site_settings|InnoDB|utf8mb4_0900_ai_ci",
        "user_identities|InnoDB|utf8mb4_0900_ai_ci",
        "user_permissions|InnoDB|utf8mb4_0900_ai_ci",
        "user_sessions|InnoDB|utf8mb4_0900_ai_ci",
        "users|InnoDB|utf8mb4_0900_ai_ci",
    ];

    private static readonly string[] ExpectedColumnNames = BuildExpectedColumnNames();

    private static readonly ColumnMetadata[] CriticalColumns =
    [
        C("alembic_version", 1, "version_num", "varchar(32)", false, null, "", "utf8mb4_0900_ai_ci"),
        C("permissions", 1, "id", "int", false, null, "auto_increment", null),
        C("permissions", 2, "key", "varchar(100)", false, null, "", "utf8mb4_0900_ai_ci"),
        C("permissions", 4, "is_system", "tinyint(1)", false, "0", "", null),
        C("roles", 1, "id", "int", false, null, "auto_increment", null),
        C("roles", 2, "name", "varchar(50)", false, null, "", "utf8mb4_0900_ai_ci"),
        C("roles", 4, "is_system", "tinyint(1)", false, "0", "", null),
        C("roles", 7, "version", "int", false, "1", "", null),
        C("users", 1, "id", "int", false, null, "auto_increment", null),
        C("users", 2, "email", "varchar(255)", false, null, "", "utf8mb4_0900_ai_ci"),
        C("users", 4, "is_active", "tinyint(1)", false, "1", "", null),
        C("users", 8, "role_id", "int", false, null, "", null),
        C("users", 17, "is_approved", "tinyint(1)", false, "1", "", null),
        C("users", 21, "last_location_at", "datetime(6)", true, null, "", null),
        C("users", 23, "ui_prefs", "json", true, null, "", null),
        C("users", 24, "access_version", "int", false, "1", "", null),
        C("users", 25, "deactivated_at", "datetime(6)", true, null, "", null),
        C("users", 26, "erasure_requested_at", "datetime(6)", true, null, "", null),
        C("users", 27, "erasure_due_at", "datetime(6)", true, null, "", null),
        C("users", 28, "erased_at", "datetime(6)", true, null, "", null),
        C("users", 29, "location_retention_until", "datetime(6)", true, null, "", null),
        C("user_identities", 4, "provider_subject", "varchar(255)", false, null, "", "utf8mb4_0900_bin"),
        C("user_identities", 9, "provider_namespace", "varchar(255)", false, "", "", "utf8mb4_0900_bin"),
        C("user_sessions", 3, "jti", "varchar(36)", false, null, "", "utf8mb4_0900_ai_ci"),
        C("user_sessions", 9, "expires_at", "datetime(6)", false, null, "", null),
        C("login_nonces", 1, "nonce", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("login_nonces", 3, "expires_at", "datetime(6)", false, null, "", null),
        C("request_logs", 1, "id", "bigint", false, null, "auto_increment", null),
        C("security_events", 1, "id", "bigint", false, null, "auto_increment", null),
        C("security_events", 10, "metadata", "json", false, null, "", null),
        C("security_events", 12, "retention_until", "datetime(6)", false, null, "", null),
        C("security_event_deliveries", 1, "event_id", "bigint", false, null, "", null),
        C("site_settings", 1, "id", "int", false, null, "", null),
        C("site_settings", 5, "default_role_id", "int", false, null, "", null),
        C("site_settings", 8, "retention_hold", "tinyint(1)", false, "0", "", null),
        C("legal_acceptances", 1, "id", "bigint", false, null, "auto_increment", null),
        C("legal_acceptances", 3, "bundle_version", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 8, "terms_sha256", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 9, "eula_sha256", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 10, "acceptable_use_sha256", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 11, "privacy_sha256", "varchar(64)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 12, "accepted_at", "datetime(6)", false, null, "", null),
        C("legal_acceptances", 13, "retention_until", "datetime(6)", false, null, "", null),
        C("legal_acceptances", 18, "bundle_effective_date", "date", false, null, "", null),
        C("legal_acceptances", 19, "acceptance_label", "varchar(500)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 20, "terms_action", "varchar(16)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 21, "eula_action", "varchar(16)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 22, "acceptable_use_action", "varchar(16)", false, null, "", "utf8mb4_0900_bin"),
        C("legal_acceptances", 23, "privacy_action", "varchar(16)", false, null, "", "utf8mb4_0900_bin"),
    ];

    private static readonly string[] CriticalColumnKeys = CriticalColumns
        .Select(column => $"{column.TableName}.{column.Name}")
        .ToArray();

    private static readonly string[] ExpectedIndexes =
    [
        I("alembic_version", "PRIMARY", true, "version_num"),
        I("legal_acceptances", "PRIMARY", true, "id"),
        I("legal_acceptances", "ix_legal_acceptances_accepted_id", false, "accepted_at,id"),
        I("legal_acceptances", "ix_legal_acceptances_retention_id", false, "retention_until,id"),
        I("legal_acceptances", "ix_legal_acceptances_user_accepted_id", false, "user_id,accepted_at,id"),
        I("login_nonces", "PRIMARY", true, "nonce"),
        I("login_nonces", "ix_login_nonces_expires_at", false, "expires_at"),
        I("permissions", "PRIMARY", true, "id"),
        I("permissions", "uq_permissions_key", true, "key"),
        I("request_logs", "PRIMARY", true, "id"),
        I("request_logs", "ix_request_logs_created_at", false, "created_at"),
        I("request_logs", "ix_request_logs_method_id", false, "method,id"),
        I("request_logs", "ix_request_logs_user_id_id", false, "user_id,id"),
        I("role_permissions", "PRIMARY", true, "role_id,permission_id"),
        I("role_permissions", "fk_role_permissions_permission_id_permissions", false, "permission_id"),
        I("roles", "PRIMARY", true, "id"),
        I("roles", "uq_roles_name", true, "name"),
        I("security_event_deliveries", "PRIMARY", true, "event_id"),
        I("security_event_deliveries", "ix_security_event_deliveries_dispatch", false, "status,next_attempt_at,event_id"),
        I("security_event_deliveries", "ix_security_event_deliveries_lease", false, "lease_expires_at"),
        I("security_events", "PRIMARY", true, "id"),
        I("security_events", "ix_security_events_actor_id", false, "actor_user_id,id"),
        I("security_events", "ix_security_events_occurred_id", false, "occurred_at,id"),
        I("security_events", "ix_security_events_request_id", false, "request_id"),
        I("security_events", "ix_security_events_retention_until", false, "retention_until"),
        I("security_events", "ix_security_events_type_id", false, "event_type,id"),
        I("site_settings", "PRIMARY", true, "id"),
        I("site_settings", "fk_site_settings_default_role_id_roles", false, "default_role_id"),
        I("user_identities", "PRIMARY", true, "id"),
        I("user_identities", "ix_user_identities_user_id", false, "user_id"),
        I("user_identities", "uq_user_identities_provider_namespace_subject", true, "provider,provider_namespace,provider_subject"),
        I("user_permissions", "PRIMARY", true, "user_id,permission_id"),
        I("user_permissions", "fk_user_permissions_permission_id", false, "permission_id"),
        I("user_sessions", "PRIMARY", true, "id"),
        I("user_sessions", "ix_user_sessions_expires_at", false, "expires_at"),
        I("user_sessions", "ix_user_sessions_revoked_at", false, "revoked_at"),
        I("user_sessions", "ix_user_sessions_user_revoked_created", false, "user_id,revoked_at,created_at"),
        I("user_sessions", "uq_user_sessions_jti", true, "jti"),
        I("users", "PRIMARY", true, "id"),
        I("users", "ix_users_created_at_id", false, "created_at,id"),
        I("users", "ix_users_deactivated_at_id", false, "deactivated_at,id"),
        I("users", "ix_users_email", false, "email"),
        I("users", "ix_users_erasure_due_at_id", false, "erasure_due_at,id"),
        I("users", "ix_users_last_location_at_id", false, "last_location_at,id"),
        I("users", "ix_users_last_seen_at", false, "last_seen_at"),
        I("users", "ix_users_location_retention_until_id", false, "location_retention_until,id"),
        I("users", "ix_users_role_id", false, "role_id"),
    ];

    private static readonly string[] ExpectedForeignKeys =
    [
        F("legal_acceptances", "fk_legal_acceptances_user_id_users", "user_id", "users", "id", "RESTRICT"),
        F("role_permissions", "fk_role_permissions_permission_id_permissions", "permission_id", "permissions", "id", "CASCADE"),
        F("role_permissions", "fk_role_permissions_role_id_roles", "role_id", "roles", "id", "CASCADE"),
        F("security_event_deliveries", "fk_security_event_deliveries_event_id_security_events", "event_id", "security_events", "id", "CASCADE"),
        F("site_settings", "fk_site_settings_default_role_id_roles", "default_role_id", "roles", "id", "RESTRICT"),
        F("user_identities", "fk_user_identities_user_id_users", "user_id", "users", "id", "CASCADE"),
        F("user_permissions", "fk_user_permissions_permission_id", "permission_id", "permissions", "id", "CASCADE"),
        F("user_permissions", "fk_user_permissions_user_id", "user_id", "users", "id", "CASCADE"),
        F("user_sessions", "fk_user_sessions_user_id_users", "user_id", "users", "id", "CASCADE"),
        F("users", "fk_users_role_id_roles", "role_id", "roles", "id", "RESTRICT"),
    ];

    private static readonly string[] ExpectedChecks = BuildExpectedChecks();

    private static readonly string[] ExpectedPermissions =
    [
        "1|users.read|View the user directory|1",
        "2|users.manage|Manage user profiles, activation, and approval|1",
        "3|roles.manage|Create and edit roles and permissions|1",
        "4|presence.view|See who is currently online|1",
        "5|presence.kick|Force sign-out (kick) a user's active sessions|1",
        "6|settings.manage|Manage site-wide configuration|1",
        "9|logs.view|View the request activity log|1",
        "10|users.export|Export the user directory as CSV|1",
        "11|security_events.view|View and export durable security events|1",
        "12|privacy.manage|Manage privacy requests, retention, and legal holds|1",
    ];

    private static readonly string[] ExpectedRoles =
    [
        "1|admin|Full access to every feature.|1|1",
        "2|user|Standard access for new members.|1|1",
    ];

    private static readonly string[] ExpectedRolePermissions =
    [
        "1|1", "1|2", "1|3", "1|4", "1|5", "1|6", "1|9", "1|10", "1|11", "1|12", "2|4",
    ];

    private static ColumnMetadata C(
        string table,
        int ordinal,
        string name,
        string type,
        bool nullable,
        string? defaultValue,
        string extra,
        string? collation)
    {
        return new ColumnMetadata(
            table,
            ordinal,
            name,
            type,
            nullable,
            defaultValue,
            extra,
            collation);
    }

    private static string I(string table, string name, bool unique, string columns)
    {
        return $"{table}|{name}|{(unique ? "UNIQUE" : "NONUNIQUE")}|BTREE|{columns}";
    }

    private static string F(
        string table,
        string name,
        string columns,
        string referencedTable,
        string referencedColumns,
        string deleteRule)
    {
        return $"{table}|{name}|{columns}|{referencedTable}|{referencedColumns}|{deleteRule}|NO ACTION";
    }

    private static string[] BuildExpectedColumnNames()
    {
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alembic_version"] = "version_num",
            ["permissions"] = "id,key,description,is_system,created_at,updated_at",
            ["roles"] = "id,name,description,is_system,created_at,updated_at,version",
            ["role_permissions"] = "role_id,permission_id",
            ["users"] = "id,email,display_name,is_active,last_login_at,created_at,updated_at,role_id,last_seen_at,session_valid_after,title,department,phone,location,timezone,bio,is_approved,last_latitude,last_longitude,last_location_accuracy,last_location_at,accent_color,ui_prefs,access_version,deactivated_at,erasure_requested_at,erasure_due_at,erased_at,location_retention_until",
            ["user_identities"] = "id,user_id,provider,provider_subject,created_at,updated_at,provider_tenant_id,provider_hosted_domain,provider_namespace",
            ["user_permissions"] = "user_id,permission_id",
            ["user_sessions"] = "id,user_id,jti,user_agent,ip,created_at,last_seen_at,revoked_at,expires_at",
            ["site_settings"] = "id,signup_mode,allow_google,allow_microsoft,default_role_id,updated_at,calm_mode,retention_hold,retention_hold_reference",
            ["login_nonces"] = "nonce,created_at,expires_at",
            ["request_logs"] = "id,method,path,user_id,ip,status_code,created_at",
            ["security_events"] = "id,event_type,outcome,severity,request_id,actor_user_id,target_type,target_id,source_ip,metadata,occurred_at,retention_until",
            ["security_event_deliveries"] = "event_id,status,attempts,next_attempt_at,lease_owner,lease_expires_at,last_attempt_at,delivered_at,last_error,updated_at",
            ["legal_acceptances"] = "id,user_id,bundle_version,terms_version,eula_version,acceptable_use_version,privacy_version,terms_sha256,eula_sha256,acceptable_use_sha256,privacy_sha256,accepted_at,retention_until,source_ip,user_agent,request_id,personal_data_erased_at,bundle_effective_date,acceptance_label,terms_action,eula_action,acceptable_use_action,privacy_action",
        };

        return definitions
            .SelectMany(pair => pair.Value.Split(',').Select(
                (column, index) => $"{pair.Key}|{index + 1:D3}|{column}"))
            .ToArray();
    }

    private static string[] BuildExpectedChecks()
    {
        var checks = new (string Table, string Name, string Clause)[]
        {
            ("permissions", "ck_permissions_is_system_boolean", "is_system in (0,1)"),
            ("roles", "ck_roles_is_system_boolean", "is_system in (0,1)"),
            ("security_event_deliveries", "ck_security_event_deliveries_attempts_nonnegative", "attempts >= 0"),
            ("security_event_deliveries", "ck_security_event_deliveries_status_valid", "status in ('pending','leased','retry','delivered','dead_letter')"),
            ("security_events", "ck_security_events_outcome_valid", "outcome in ('success','failure','denied')"),
            ("security_events", "ck_security_events_severity_valid", "severity in ('info','warning','critical')"),
            ("site_settings", "ck_site_settings_allow_google_boolean", "allow_google in (0,1)"),
            ("site_settings", "ck_site_settings_allow_microsoft_boolean", "allow_microsoft in (0,1)"),
            ("site_settings", "ck_site_settings_calm_mode_boolean", "calm_mode in (0,1)"),
            ("site_settings", "ck_site_settings_provider_available", "(allow_google = 1) or (allow_microsoft = 1)"),
            ("site_settings", "ck_site_settings_retention_hold_boolean", "retention_hold in (0,1)"),
            ("site_settings", "ck_site_settings_retention_hold_state_valid", "((retention_hold = 0) and (retention_hold_reference is null)) or ((retention_hold = 1) and (retention_hold_reference is not null) and (char_length(trim(retention_hold_reference)) between 1 and 255))"),
            ("site_settings", "ck_site_settings_signup_mode_valid", "signup_mode in ('open','approval','closed')"),
            ("site_settings", "ck_site_settings_singleton_id", "id = 1"),
            ("users", "ck_users_deactivation_state_valid", "((is_active = 1) and (deactivated_at is null)) or ((is_active = 0) and (deactivated_at is not null))"),
            ("users", "ck_users_erasure_schedule_valid", "((erasure_requested_at is null) and (erasure_due_at is null)) or ((erasure_requested_at is not null) and (erasure_due_at is not null) and (erasure_due_at >= erasure_requested_at))"),
            ("users", "ck_users_erasure_state_valid", "(erased_at is null) or ((is_active = 0) and (is_approved = 0) and (deactivated_at is not null) and (erased_at >= deactivated_at) and ((erasure_requested_at is null) or (erased_at >= erasure_requested_at)))"),
            ("users", "ck_users_is_active_boolean", "is_active in (0,1)"),
            ("users", "ck_users_is_approved_boolean", "is_approved in (0,1)"),
            ("users", "ck_users_last_latitude_valid", "(last_latitude is null) or (last_latitude between -90 and 90)"),
            ("users", "ck_users_last_location_accuracy_valid", "(last_location_accuracy is null) or (last_location_accuracy between 0 and 100000)"),
            ("users", "ck_users_last_longitude_valid", "(last_longitude is null) or (last_longitude between -180 and 180)"),
            ("users", "ck_users_location_retention_valid", "((last_latitude is null) and (last_longitude is null) and (last_location_accuracy is null) and (last_location_at is null) and (location_retention_until is null)) or (((last_latitude is not null) or (last_longitude is not null) or (last_location_accuracy is not null)) and (last_location_at is not null) and (location_retention_until is not null) and (location_retention_until >= last_location_at))"),
            ("legal_acceptances", "ck_legal_acceptances_acceptable_use_action_valid", "acceptable_use_action in ('agreement','acknowledgement')"),
            ("legal_acceptances", "ck_legal_acceptances_acceptable_use_sha256_valid", "regexp_like(acceptable_use_sha256,'^[0-9a-f]{64}$')"),
            ("legal_acceptances", "ck_legal_acceptances_acceptance_label_valid", "char_length(trim(acceptance_label)) between 1 and 500"),
            ("legal_acceptances", "ck_legal_acceptances_effective_date_not_after_acceptance", "bundle_effective_date <= cast(accepted_at as date)"),
            ("legal_acceptances", "ck_legal_acceptances_eula_action_valid", "eula_action in ('agreement','acknowledgement')"),
            ("legal_acceptances", "ck_legal_acceptances_eula_sha256_valid", "regexp_like(eula_sha256,'^[0-9a-f]{64}$')"),
            ("legal_acceptances", "ck_legal_acceptances_personal_data_erasure_valid", "(personal_data_erased_at is null) or ((source_ip is null) and (user_agent is null) and (request_id is null) and (personal_data_erased_at >= accepted_at))"),
            ("legal_acceptances", "ck_legal_acceptances_privacy_action_valid", "privacy_action in ('agreement','acknowledgement')"),
            ("legal_acceptances", "ck_legal_acceptances_privacy_sha256_valid", "regexp_like(privacy_sha256,'^[0-9a-f]{64}$')"),
            ("legal_acceptances", "ck_legal_acceptances_retention_after_acceptance", "retention_until > accepted_at"),
            ("legal_acceptances", "ck_legal_acceptances_terms_action_valid", "terms_action in ('agreement','acknowledgement')"),
            ("legal_acceptances", "ck_legal_acceptances_terms_sha256_valid", "regexp_like(terms_sha256,'^[0-9a-f]{64}$')"),
        };

        return checks
            .Select(check => $"{check.Table}|{check.Name}|{NormalizeCheckClause(check.Clause)}")
            .ToArray();
    }
}
