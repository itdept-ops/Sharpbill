using Sharpbill.Application.Common;
using Sharpbill.Application.Policies;
using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Constants;
using Sharpbill.Domain.Entities;
using Sharpbill.Domain.Legal;
using Sharpbill.Domain.ValueObjects;

namespace Sharpbill.Application.Tests;

public sealed class PolicyTests
{
    [Fact]
    public void LegalPolicyRejectsMissingAcceptanceBeforeLogin()
    {
        var exception = Assert.Throws<ApiException>(() =>
            LegalAcceptancePolicy.RequireCurrent(false, LegalBundleV3.BundleVersion));

        Assert.Equal(428, exception.StatusCode);
        Assert.Equal("LEGAL_ACCEPTANCE_REQUIRED", exception.Code);
    }

    [Fact]
    public void LegalPolicyRejectsStaleBundle()
    {
        var exception = Assert.Throws<ApiException>(() =>
            LegalAcceptancePolicy.RequireCurrent(true, "2026-07-20-v2"));

        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("LEGAL_BUNDLE_STALE", exception.Code);
    }

    [Fact]
    public void LegalEvidenceUsesImmutableHashesAndBoundsDeviceMetadata()
    {
        var evidence = LegalAcceptancePolicy.CreateEvidence(
            1,
            7,
            new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
            2_555,
            new string('1', 60),
            new string('a', 500),
            new string('r', 80));

        Assert.Equal(LegalBundleV3.TermsSha256, evidence.TermsSha256);
        Assert.Equal(45, evidence.SourceIp?.Length);
        Assert.Equal(400, evidence.UserAgent?.Length);
        Assert.Equal(64, evidence.RequestId?.Length);
        Assert.Equal(evidence.AcceptedAt.AddDays(2_555), evidence.RetentionUntil);
    }

    [Fact]
    public void DelegatedManagerCannotManagePrincipalWithUnheldPermission()
    {
        var actor = User(
            "manager",
            new HashSet<string> { PermissionKeys.UsersManage, PermissionKeys.RolesManage });
        var target = User(
            "analyst",
            new HashSet<string> { PermissionKeys.UsersManage, "reports.export" });

        var exception = Assert.Throws<ApiException>(() =>
            RbacHierarchyPolicy.EnsureCanManageTarget(actor, target));

        Assert.Equal("INSUFFICIENT_PRIVILEGE", exception.Code);
    }

    [Fact]
    public void AdministratorCanManageHigherPermissionPrincipal()
    {
        var actor = User(SystemRoleNames.Administrator, new HashSet<string>());
        var target = User("analyst", new HashSet<string> { "reports.export" });

        RbacHierarchyPolicy.EnsureCanManageTarget(actor, target);
    }

    [Fact]
    public void OptimisticConcurrencyRequiresVersionAndRejectsStaleWrite()
    {
        Assert.Equal(
            "PRECONDITION_REQUIRED",
            Assert.Throws<ApiException>(() =>
                RbacHierarchyPolicy.RequireVersion(null, 2, "Role")).Code);
        Assert.Equal(
            "STALE_WRITE",
            Assert.Throws<ApiException>(() =>
                RbacHierarchyPolicy.RequireVersion(1, 2, "Role")).Code);
        RbacHierarchyPolicy.RequireVersion(2, 2, "Role");
    }

    [Fact]
    public void UiPreferencesPatchMergesAndExplicitNullClears()
    {
        var current = new UiPreferences { BaseTone = "abyss", Motion = "full" };
        var patch = new UiPreferencesContract
        {
            BaseTone = new PatchField<string?>(null),
            Motion = new PatchField<string?>("reduced"),
        };

        var result = UiPreferencesPolicy.ApplyPatch(current, patch);

        Assert.Null(result.BaseTone);
        Assert.Equal("reduced", result.Motion);
    }

    [Fact]
    public void SecurityEventPolicyRejectsSecretBearingMetadataKeys()
    {
        var securityEvent = new SecurityEventWrite
        {
            EventType = "auth.login",
            Outcome = "denied",
            Metadata = new Dictionary<string, object?> { ["session_token"] = "secret" },
        };

        var result = SecurityEventPolicy.Validate(securityEvent);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "INVALID_METADATA");
    }

    [Fact]
    public void RetentionOptionsEnforceGovernedRanges()
    {
        var validator = new RetentionPolicyValidator();

        Assert.True(validator.Validate(new RetentionPolicyOptions()).IsValid);
        Assert.False(validator.Validate(new RetentionPolicyOptions
        {
            PreciseLocationHours = 721,
        }).IsValid);
    }

    [Fact]
    public void BulkValidatorRejectsDuplicatesAndMissingRole()
    {
        var result = new BulkActionRequestValidator().Validate(new BulkActionRequest
        {
            Ids = [1, 1],
            Action = BulkUserActionContract.AssignRole,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Code == "DUPLICATE_ID");
        Assert.Contains(result.Errors, static error => error.Field == "role_id");
    }

    [Fact]
    public void LocationValidatorRejectsNonFiniteCoordinates()
    {
        var result = new LocationUpdateRequestValidator().Validate(new LocationUpdateRequest
        {
            Latitude = double.NaN,
            Longitude = 0,
        });

        Assert.False(result.IsValid);
    }

    private static User User(string roleName, IReadOnlySet<string> permissions) => new()
    {
        Id = 1,
        Email = "user@example.test",
        RoleId = 1,
        RoleName = roleName,
        RolePermissionKeys = permissions,
    };
}
