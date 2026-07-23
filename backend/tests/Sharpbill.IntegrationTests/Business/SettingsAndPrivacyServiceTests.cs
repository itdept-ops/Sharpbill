using Sharpbill.Application.Common;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Settings;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;

namespace Sharpbill.IntegrationTests.Business;

public sealed class SettingsAndPrivacyServiceTests
{
    [Fact]
    public async Task NoOpSettingsUpdatePreservesUpdatedAtAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        DateTime originalUpdatedAt = fixture.Settings.Value!.UpdatedAt;

        SiteSettingsResponse response = await fixture.CreateSettingsService().UpdateAsync(
            1,
            new SiteSettingsUpdateRequest(),
            CancellationToken.None);

        Assert.Equal(originalUpdatedAt, response.UpdatedAt);
        Assert.Equal(originalUpdatedAt, fixture.Settings.Value.UpdatedAt);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(fixture.SecurityEvents.Writes).Metadata["changes"]));
    }

    [Fact]
    public async Task UpdateSettingsRejectsAdministratorAsSignupDefaultAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateSettingsService().UpdateAsync(
                1,
                new SiteSettingsUpdateRequest { DefaultRoleId = 1 },
                CancellationToken.None));

        Assert.Equal("PROTECTED_DEFAULT_ROLE", exception.Code);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
    }

    [Fact]
    public async Task UpdateSettingsPreventsRemovingTheLastConfiguredProviderAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Options.IdentityProviders.GoogleClientId = null;
        fixture.Options.IdentityProviders.MicrosoftClientId = null;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateSettingsService().UpdateAsync(
                1,
                new SiteSettingsUpdateRequest { AllowGoogle = false },
                CancellationToken.None));

        Assert.Equal("NO_PROVIDER_ENABLED", exception.Code);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
    }

    [Fact]
    public async Task UpdateSettingsRollsBackAProviderTransitionThatStrandsAdministrationAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        fixture.Health.ReachableAdministrator = false;

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreateSettingsService().UpdateAsync(
                1,
                new SiteSettingsUpdateRequest { AllowGoogle = false },
                CancellationToken.None));

        Assert.Equal("ADMIN_ACCESS_STRANDED", exception.Code);
        Assert.Equal(0, fixture.UnitOfWork.Commits);
        Assert.Equal(1, fixture.UnitOfWork.Rollbacks);
    }

    [Fact]
    public async Task DeleteLocationIsBlockedByRetentionHoldAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[2] = Member(2) with
        {
            Location = "Hillsboro",
            LastLatitude = 45.52,
            LastLongitude = -122.99,
        };
        fixture.Settings.Value = fixture.Settings.Value! with
        {
            RetentionHold = true,
            RetentionHoldReference = "CASE-123",
        };

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreatePrivacyService().DeleteLocationAsync(2, CancellationToken.None));

        Assert.Equal(423, exception.StatusCode);
        Assert.Equal("RETENTION_HOLD", exception.Code);
        Assert.Equal("Hillsboro", fixture.Users.Items[2].Location);
    }

    [Fact]
    public async Task RequestErasureSchedulesTheConfiguredGracePeriodAtomicallyAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[2] = Member(2);
        fixture.Options.Retention.AccountErasureGraceDays = 17;

        PrivacyStatusResponse response = await fixture.CreatePrivacyService().RequestOwnErasureAsync(
            2,
            CancellationToken.None);

        Assert.Equal(fixture.Clock.UtcNow, response.ErasureRequestedAt);
        Assert.Equal(fixture.Clock.UtcNow.AddDays(17), response.ErasureDueAt);
        Assert.Equal("privacy.erasure.requested", Assert.Single(fixture.SecurityEvents.Writes).EventType);
        Assert.Equal(1, fixture.UnitOfWork.Commits);
    }

    [Fact]
    public async Task AdministrativeErasureOperationsRejectSelfBeforeStartingATransactionAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);
        var service = fixture.CreatePrivacyService();

        ApiException requestException = await Assert.ThrowsAsync<ApiException>(() =>
            service.RequestUserErasureAsync(1, 1, CancellationToken.None));
        ApiException cancellationException = await Assert.ThrowsAsync<ApiException>(() =>
            service.CancelUserErasureAsync(1, 1, CancellationToken.None));

        Assert.Equal("CANNOT_MODIFY_SELF", requestException.Code);
        Assert.Equal("CANNOT_MODIFY_SELF", cancellationException.Code);
        Assert.Equal(0, fixture.UnitOfWork.Begins);
        Assert.Empty(fixture.Users.Updates);
        Assert.Empty(fixture.SecurityEvents.Writes);
    }

    [Fact]
    public async Task CancelErasureRemainsAvailableDuringRetentionHoldAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[2] = Member(2) with
        {
            ErasureRequestedAt = fixture.Clock.UtcNow.AddDays(-1),
            ErasureDueAt = fixture.Clock.UtcNow.AddDays(29),
        };
        fixture.Settings.Value = fixture.Settings.Value! with { RetentionHold = true };

        PrivacyStatusResponse response = await fixture.CreatePrivacyService().CancelOwnErasureAsync(
            2,
            CancellationToken.None);

        Assert.Null(response.ErasureRequestedAt);
        Assert.Null(response.ErasureDueAt);
        Assert.True(response.RetentionHold);
    }

    [Fact]
    public async Task UpdateHoldRequiresAValidatedExternalReferenceAsync()
    {
        var fixture = new BusinessServiceFixture();
        fixture.Users.Items[1] = Administrator(1);

        ApiException exception = await Assert.ThrowsAsync<ApiException>(() =>
            fixture.CreatePrivacyService().UpdateHoldAsync(
                1,
                new RetentionHoldUpdateRequest { Enabled = true },
                CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        Assert.Equal("VALIDATION_ERROR", exception.Code);
        Assert.Equal(0, fixture.UnitOfWork.Begins);
    }

    private static User Administrator(int id) => BusinessTestData.User(
        id,
        SystemRoleNames.Administrator,
        PermissionKeys.BuiltIn);

    private static User Member(int id) => BusinessTestData.User(
        id,
        SystemRoleNames.DefaultUser,
        [PermissionKeys.UsersRead, PermissionKeys.PresenceView]);
}
