using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Policies;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Enums;
using Sharpbill.Domain.ValueObjects;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;
using Sharpbill.Infrastructure.Repositories;

namespace Sharpbill.IntegrationTests.Repositories;

public sealed class RepositoryIntegrationTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("SHARPBILL_TEST_DATABASE");

    [Fact]
    public void EveryRepositoryContractHasAConcreteDapperImplementation()
    {
        Type[] contracts = typeof(IUserRepository).Assembly.GetTypes()
            .Where(static type => type.IsInterface &&
                type.Name.EndsWith("Repository", StringComparison.Ordinal))
            .ToArray();
        Type[] implementations = typeof(UserRepository).Assembly.GetTypes()
            .Where(static type => type is { IsAbstract: false, IsClass: true } &&
                type.Namespace == typeof(UserRepository).Namespace)
            .ToArray();

        Assert.NotEmpty(contracts);
        foreach (Type contract in contracts)
        {
            Assert.Contains(implementations, contract.IsAssignableFrom);
        }
    }

    [Fact]
    public async Task CanonicalSeedAndBoundedQueriesRoundTripWhenDatabaseIsConfiguredAsync()
    {
        string? connectionString = GetConfiguredDatabase();
        if (connectionString is null)
        {
            return;
        }

        await using var session = new DatabaseSession(new TestConnectionFactory(connectionString));
        var roleRepository = new RoleRepository(session);
        var permissionRepository = new PermissionRepository(session);
        var userRepository = new UserRepository(session, TestOptions());
        var healthRepository = new HealthRepository(session, TestOptions());

        IReadOnlyList<Role> roles = await roleRepository.ListAsync(CancellationToken.None);
        IReadOnlyList<Permission> permissions = await permissionRepository.ListAsync(
            CancellationToken.None);
        (IReadOnlyList<User> users, int total) = await userRepository.ListAsync(new UserQuery
        {
            Limit = 5,
            Offset = 0,
        }, CancellationToken.None);

        Assert.Contains(roles, static role => role.Name == "admin");
        Assert.Contains(roles, static role => role.Name == "user");
        Assert.Contains(permissions, static permission => permission.Key == "users.read");
        Assert.True(total >= users.Count);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "0021" },
            await healthRepository.GetSchemaHeadsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NonceAndOutboxTransitionsAreAtomicWhenDatabaseIsConfiguredAsync()
    {
        string? connectionString = GetConfiguredDatabase();
        if (connectionString is null)
        {
            return;
        }

        await using var session = new DatabaseSession(new TestConnectionFactory(connectionString));
        await session.BeginAsync(CancellationToken.None);
        try
        {
            DateTime now = DateTime.UtcNow;
            string nonceValue = $"test-{Guid.NewGuid():N}";
            var nonces = new NonceRepository(session);
            await nonces.AddAsync(new LoginNonce
            {
                Nonce = nonceValue,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
            }, CancellationToken.None);
            Assert.True(await nonces.ConsumeAsync(
                nonceValue,
                now,
                CancellationToken.None));
            Assert.False(await nonces.ConsumeAsync(
                nonceValue,
                now,
                CancellationToken.None));

            var roles = new RoleRepository(session);
            Role standardRole = await roles.FindByNameAsync(
                "user",
                false,
                CancellationToken.None) ?? throw new InvalidOperationException("Seed role is missing.");
            var users = new UserRepository(session, TestOptions());
            string email = $"literal%_{Guid.NewGuid():N}@example.invalid";
            int userId = await users.AddAsync(new User
            {
                Id = 0,
                Email = email,
                DisplayName = "LIKE escape probe",
                RoleId = standardRole.Id,
                RoleName = standardRole.Name,
                IsActive = true,
                IsApproved = true,
                AccessVersion = 1,
                UiPreferences = new UiPreferences
                {
                    BaseTone = "slate",
                    HighContrastText = true,
                    Version = 1,
                },
                CreatedAt = now,
                UpdatedAt = now,
                RolePermissionKeys = standardRole.PermissionKeys,
            }, CancellationToken.None);
            User authenticated = await users.FindForAuthenticationAsync(
                userId,
                CancellationToken.None) ?? throw new InvalidOperationException("Inserted user was not found.");
            Assert.Equal(email, authenticated.Email);
            Assert.Equal("slate", authenticated.UiPreferences?.BaseTone);
            Assert.True(authenticated.UiPreferences?.HighContrastText);
            (IReadOnlyList<User> escapedUsers, _) = await users.ListAsync(new UserQuery
            {
                Search = "%_",
                Limit = 500,
            }, CancellationToken.None);
            Assert.Contains(escapedUsers, user => user.Id == userId);

            var legalAcceptances = new LegalAcceptanceRepository(
                session,
                new TestClock(now));
            LegalAcceptance evidence = LegalAcceptancePolicy.CreateEvidence(
                0,
                userId,
                now,
                2_555,
                "203.0.113.10",
                "repository-integration-test",
                Guid.NewGuid().ToString("N"));
            long acceptanceId = await legalAcceptances.AddAsync(
                evidence,
                CancellationToken.None);
            LegalAcceptance roundTripped = Assert.Single(
                await legalAcceptances.ListForUserAsync(userId, CancellationToken.None));
            Assert.Equal(acceptanceId, roundTripped.Id);
            Assert.Equal(evidence.BundleEffectiveDate, roundTripped.BundleEffectiveDate);

            DateTime roleNow = now.AddMilliseconds(1);
            int customRoleId = await roles.AddAsync(new Role
            {
                Id = 0,
                Name = $"test-{Guid.NewGuid():N}",
                IsSystem = false,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            }, CancellationToken.None);
            Role customRole = await roles.FindAsync(customRoleId, true, CancellationToken.None)
                ?? throw new InvalidOperationException("Inserted role was not found.");
            Role updatedRole = customRole with { Version = 2, UpdatedAt = roleNow };
            await roles.UpdateAsync(updatedRole, CancellationToken.None);
            await Assert.ThrowsAsync<DBConcurrencyException>(() =>
                roles.UpdateAsync(updatedRole, CancellationToken.None));

            var events = new SecurityEventRepository(session);
            long eventId = await events.AddWithPendingDeliveryAsync(new SecurityEvent
            {
                Id = 0,
                EventType = "repository.integration_test",
                Outcome = SecurityEventOutcome.Success,
                Severity = SecurityEventSeverity.Info,
                Metadata = new Dictionary<string, object?> { ["suite"] = "repositories" },
                OccurredAt = now,
                RetentionUntil = now.AddDays(1),
            }, CancellationToken.None);
            var outbox = new EventOutboxRepository(session);
            IReadOnlyList<EventDeliveryEnvelope> claimed = await outbox.ClaimAsync(
                "repository-integration-test",
                500,
                now,
                now.AddMinutes(1),
                CancellationToken.None);

            Assert.Contains(claimed, envelope => envelope.EventId == eventId);
            DateTime firstFailure = now.AddSeconds(1);
            Assert.True(await outbox.MarkFailedAsync(
                eventId,
                "repository-integration-test",
                firstFailure,
                firstFailure.AddSeconds(2),
                "sink_delivery_failed:0123456789abcdef",
                10,
                CancellationToken.None));
            DeliveryStateRow firstState = await session.Connection.QuerySingleAsync<DeliveryStateRow>(
                "SELECT status, attempts, next_attempt_at FROM security_event_deliveries " +
                "WHERE event_id = @EventId",
                new { EventId = eventId },
                session.Transaction);
            Assert.Equal("retry", firstState.Status);
            Assert.Equal(1, firstState.Attempts);
            Assert.Equal(ToMySqlTimestampPrecision(firstFailure.AddSeconds(2)),
                DateTime.SpecifyKind(firstState.NextAttemptAt, DateTimeKind.Utc));

            DateTime secondFailure = now.AddSeconds(4);
            IReadOnlyList<EventDeliveryEnvelope> reclaimed = await outbox.ClaimAsync(
                "repository-integration-test",
                500,
                secondFailure,
                secondFailure.AddMinutes(1),
                CancellationToken.None);
            Assert.Contains(reclaimed, envelope => envelope.EventId == eventId);
            Assert.True(await outbox.MarkFailedAsync(
                eventId,
                "repository-integration-test",
                secondFailure,
                secondFailure.AddSeconds(2),
                "sink_delivery_failed:fedcba9876543210",
                10,
                CancellationToken.None));
            DeliveryStateRow secondState = await session.Connection.QuerySingleAsync<DeliveryStateRow>(
                "SELECT status, attempts, next_attempt_at FROM security_event_deliveries " +
                "WHERE event_id = @EventId",
                new { EventId = eventId },
                session.Transaction);
            Assert.Equal("retry", secondState.Status);
            Assert.Equal(2, secondState.Attempts);
            Assert.Equal(ToMySqlTimestampPrecision(secondFailure.AddSeconds(4)),
                DateTime.SpecifyKind(secondState.NextAttemptAt, DateTimeKind.Utc));
        }
        finally
        {
            await session.RollbackAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AuthenticationSnapshotsRemainConcurrentAndPresenceWritesAreThrottledAsync()
    {
        string? connectionString = GetConfiguredDatabase();
        if (connectionString is null)
        {
            return;
        }

        int userId = 0;
        DateTime now = DateTime.UtcNow;
        try
        {
            await using (var setup = new DatabaseSession(new TestConnectionFactory(connectionString)))
            {
                var roles = new RoleRepository(setup);
                Role standardRole = await roles.FindByNameAsync(
                    "user",
                    false,
                    CancellationToken.None) ?? throw new InvalidOperationException("Seed role is missing.");
                var users = new UserRepository(setup, TestOptions());
                userId = await users.AddAsync(new User
                {
                    Id = 0,
                    Email = $"concurrency-{Guid.NewGuid():N}@example.invalid",
                    DisplayName = "Authentication concurrency probe",
                    RoleId = standardRole.Id,
                    RoleName = standardRole.Name,
                    IsActive = true,
                    IsApproved = true,
                    AccessVersion = 1,
                    LastSeenAt = now.AddMinutes(-1),
                    CreatedAt = now,
                    UpdatedAt = now,
                    RolePermissionKeys = standardRole.PermissionKeys,
                }, CancellationToken.None);
            }

            await using var firstSession = new DatabaseSession(new TestConnectionFactory(connectionString));
            await using var secondSession = new DatabaseSession(new TestConnectionFactory(connectionString));
            await firstSession.BeginAsync(CancellationToken.None);
            await secondSession.BeginAsync(CancellationToken.None);
            try
            {
                var firstUsers = new UserRepository(firstSession, TestOptions());
                var secondUsers = new UserRepository(secondSession, TestOptions());
                Assert.NotNull(await firstUsers.FindForAuthenticationAsync(userId, CancellationToken.None));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Assert.NotNull(await secondUsers.FindForAuthenticationAsync(userId, timeout.Token));
            }
            finally
            {
                await firstSession.RollbackAsync(CancellationToken.None);
                await secondSession.RollbackAsync(CancellationToken.None);
            }

            DateTime firstSeenAt = now.AddSeconds(1);
            await using (var activity = new DatabaseSession(new TestConnectionFactory(connectionString)))
            {
                var presence = new PresenceRepository(activity);
                await presence.TouchAsync(
                    userId,
                    firstSeenAt,
                    firstSeenAt.AddSeconds(-15),
                    CancellationToken.None);
                await presence.TouchAsync(
                    userId,
                    firstSeenAt.AddSeconds(1),
                    firstSeenAt.AddSeconds(-14),
                    CancellationToken.None);
                DateTime persisted = await activity.Connection.ExecuteScalarAsync<DateTime>(
                    "SELECT last_seen_at FROM users WHERE id = @UserId",
                    new { UserId = userId });
                Assert.Equal(ToMySqlTimestampPrecision(firstSeenAt),
                    DateTime.SpecifyKind(persisted, DateTimeKind.Utc));
            }
        }
        finally
        {
            if (userId != 0)
            {
                await using var cleanup = new MySqlConnection(connectionString);
                await cleanup.OpenAsync(CancellationToken.None);
                _ = await cleanup.ExecuteAsync(
                    "DELETE FROM users WHERE id = @UserId",
                    new { UserId = userId });
            }
        }
    }

    private static IOptions<SharpbillOptions> TestOptions() => Options.Create(new SharpbillOptions
    {
        AppEnvironment = "local",
        IdentityProviders = new IdentityProviderOptions(),
        DevelopmentAuthentication = new DevelopmentAuthenticationOptions(),
        Retention = new RetentionOptions(),
    });

    private static string? GetConfiguredDatabase()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString))
        {
            return ConnectionString;
        }

        if (string.Equals(
            Environment.GetEnvironmentVariable("CI"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SHARPBILL_TEST_DATABASE is required for repository integration tests in CI.");
        }

        return null;
    }

    private static DateTime ToMySqlTimestampPrecision(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);

    private sealed class TestClock(DateTime utcNow) : Sharpbill.Application.Common.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class TestConnectionFactory(string connectionString) : IDatabaseConnectionFactory
    {
        static TestConnectionFactory()
        {
            DefaultTypeMap.MatchNamesWithUnderscores = true;
        }

        public async ValueTask<MySqlConnection> OpenConnectionAsync(
            CancellationToken cancellationToken = default)
        {
            var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            _ = await connection.ExecuteAsync(new CommandDefinition(
                "SET SESSION time_zone = '+00:00'",
                cancellationToken: cancellationToken));
            return connection;
        }
    }

    private sealed class DeliveryStateRow
    {
        public string Status { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; }
    }
}
