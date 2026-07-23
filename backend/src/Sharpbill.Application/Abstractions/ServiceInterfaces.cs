using Sharpbill.Application.Common;
using Sharpbill.Contracts.Access;
using Sharpbill.Contracts.Auth;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Dashboard;
using Sharpbill.Contracts.Health;
using Sharpbill.Contracts.Legal;
using Sharpbill.Contracts.Operations;
using Sharpbill.Contracts.Privacy;
using Sharpbill.Contracts.Settings;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Application.Abstractions;

public sealed record AuthenticatedSession(UserResponse User, SessionToken Session);

public sealed record SessionValidationResult
{
    private SessionValidationResult(
        UserResponse? user,
        string? failureCode,
        string? failureMessage)
    {
        User = user;
        FailureCode = failureCode;
        FailureMessage = failureMessage;
    }

    public UserResponse? User { get; }
    public string? FailureCode { get; }
    public string? FailureMessage { get; }
    public bool IsValid => User is not null;

    public static SessionValidationResult Valid(UserResponse user) =>
        new(user ?? throw new ArgumentNullException(nameof(user)), null, null);

    public static SessionValidationResult Invalid(string failureCode, string failureMessage) =>
        new(
            null,
            string.IsNullOrWhiteSpace(failureCode)
                ? throw new ArgumentException("A failure code is required.", nameof(failureCode))
                : failureCode,
            string.IsNullOrWhiteSpace(failureMessage)
                ? throw new ArgumentException("A failure message is required.", nameof(failureMessage))
                : failureMessage);
}

public interface IAuthService
{
    Task<AuthConfigResponse> GetConfigurationAsync(CancellationToken cancellationToken);
    Task<AuthenticatedSession> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
    Task<AuthenticatedSession> DevLoginAsync(
        DevLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
    Task LogoutAsync(RequestContext context, CancellationToken cancellationToken);
    Task<UserResponse> GetCurrentUserAsync(int userId, CancellationToken cancellationToken);
}

public interface IAuthConfigurationService
{
    Task<AuthConfigResponse> GetConfigurationAsync(CancellationToken cancellationToken);
}

public interface IExternalLoginService
{
    Task<AuthenticatedSession> LoginAsync(
        ProviderContract provider,
        TokenLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IDevelopmentLoginService
{
    Task<AuthenticatedSession> LoginAsync(
        DevLoginRequest request,
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IAuthAccountService
{
    Task<UserResponse> GetCurrentUserAsync(
        int userId,
        CancellationToken cancellationToken);
}

public interface IAuthSessionOperationsService
{
    Task LogoutAsync(
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IDevelopmentAuthService
{
    Task<AuthenticatedSession> LoginAsync(
        DevLoginRequest request,
        string? suppliedSecret,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListRolesAsync(
        string? suppliedSecret,
        CancellationToken cancellationToken);
}

public interface IIdentityTokenVerifier
{
    ProviderContract Provider { get; }
    Task<VerifiedIdentity> VerifyAsync(
        string idToken,
        string expectedNonce,
        CancellationToken cancellationToken);
}

public interface INonceService
{
    Task<NonceResponse> IssueAsync(CancellationToken cancellationToken);
    Task<bool> ConsumeAsync(string nonce, CancellationToken cancellationToken);
}

public interface ISessionService
{
    Task<SessionToken> StartAsync(
        int userId,
        bool legalAccepted,
        string legalBundleVersion,
        RequestContext context,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SessionResponse>> ListAsync(
        int actorUserId,
        int targetUserId,
        bool includeDeviceDetails,
        Guid? currentJti,
        CancellationToken cancellationToken);
    Task RevokeOwnAsync(
        int userId,
        int sessionId,
        CancellationToken cancellationToken);
    Task RevokeAdministrativelyAsync(
        int actorUserId,
        int targetUserId,
        int sessionId,
        CancellationToken cancellationToken);
    Task RevokeAllAsync(int targetUserId, DateTime validAfter, CancellationToken cancellationToken);
    Task<SessionValidationResult> ValidateAsync(
        int userId,
        Guid jti,
        DateTime issuedAt,
        CancellationToken cancellationToken);
}

public interface ILegalService
{
    LegalManifestResponse GetManifest();
    void RequireCurrentAcceptance(bool accepted, string bundleVersion);
    Task RecordAcceptanceAsync(
        int userId,
        RequestContext context,
        CancellationToken cancellationToken);
}

public interface IUserService
{
    Task<UserListResponse> ListAsync(UserQuery query, int actorUserId, CancellationToken cancellationToken);
    Task<UserResponse> GetAsync(int userId, int actorUserId, CancellationToken cancellationToken);
    Task<UserResponse> UpdateProfileAsync(
        int userId,
        int actorUserId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> AssignRoleAsync(
        int userId,
        int actorUserId,
        RoleAssignRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> SetPermissionsAsync(
        int userId,
        int actorUserId,
        PermissionGrantRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> SetStatusAsync(
        int userId,
        int actorUserId,
        StatusUpdateRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> ApproveAsync(int userId, int actorUserId, CancellationToken cancellationToken);
    Task<UserResponse> KickAsync(int userId, int actorUserId, CancellationToken cancellationToken);
    Task<BulkActionResponse> BulkAsync(
        int actorUserId,
        BulkActionRequest request,
        CancellationToken cancellationToken);
    Task UpdateLocationAsync(
        int userId,
        LocationUpdateRequest request,
        CancellationToken cancellationToken);
    Task<ExportDocument> ExportAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
}

public interface IUserQueryService
{
    Task<UserListResponse> ListAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
    Task<UserResponse> GetAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken);
    Task<ExportDocument> ExportAsync(
        UserQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
}

public interface IUserProfileService
{
    Task<UserResponse> UpdateProfileAsync(
        int userId,
        int actorUserId,
        ProfileUpdateRequest request,
        CancellationToken cancellationToken);
    Task UpdateLocationAsync(
        int userId,
        LocationUpdateRequest request,
        CancellationToken cancellationToken);
}

public interface IUserAccessService
{
    Task<UserResponse> AssignRoleAsync(
        int userId,
        int actorUserId,
        RoleAssignRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> SetPermissionsAsync(
        int userId,
        int actorUserId,
        PermissionGrantRequest request,
        CancellationToken cancellationToken);
}

public interface IUserLifecycleService
{
    Task<UserResponse> SetStatusAsync(
        int userId,
        int actorUserId,
        StatusUpdateRequest request,
        CancellationToken cancellationToken);
    Task<UserResponse> ApproveAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken);
    Task<UserResponse> KickAsync(
        int userId,
        int actorUserId,
        CancellationToken cancellationToken);
    Task<BulkActionResponse> BulkAsync(
        int actorUserId,
        BulkActionRequest request,
        CancellationToken cancellationToken);
}

public interface IRoleService
{
    Task<IReadOnlyList<RoleResponse>> ListAsync(int actorUserId, CancellationToken cancellationToken);
    Task<RoleResponse> CreateAsync(
        int actorUserId,
        RoleCreateRequest request,
        CancellationToken cancellationToken);
    Task<RoleResponse> UpdateAsync(
        int roleId,
        int actorUserId,
        RoleUpdateRequest request,
        CancellationToken cancellationToken);
    Task DeleteAsync(
        int roleId,
        int actorUserId,
        int? expectedVersion,
        CancellationToken cancellationToken);
}

public interface IPermissionService
{
    Task<IReadOnlyList<PermissionResponse>> ListAsync(
        int actorUserId,
        CancellationToken cancellationToken);
    Task<PermissionResponse> CreateAsync(
        int actorUserId,
        PermissionCreateRequest request,
        CancellationToken cancellationToken);
}

public interface ISettingsService
{
    Task<SiteSettingsResponse> GetAsync(int actorUserId, CancellationToken cancellationToken);
    Task<SiteSettingsResponse> UpdateAsync(
        int actorUserId,
        SiteSettingsUpdateRequest request,
        CancellationToken cancellationToken);
}

public interface IPrivacyService
{
    Task<PrivacyStatusResponse> GetAsync(int userId, CancellationToken cancellationToken);
    Task<PrivacyAdminStatusResponse> GetAdministrationAsync(
        int actorUserId,
        CancellationToken cancellationToken);
    Task DeleteLocationAsync(int userId, CancellationToken cancellationToken);
    Task<PrivacyStatusResponse> RequestOwnErasureAsync(
        int userId,
        CancellationToken cancellationToken);
    Task<PrivacyStatusResponse> RequestUserErasureAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken);
    Task<PrivacyStatusResponse> CancelOwnErasureAsync(
        int userId,
        CancellationToken cancellationToken);
    Task<PrivacyStatusResponse> CancelUserErasureAsync(
        int actorUserId,
        int targetUserId,
        CancellationToken cancellationToken);
    Task<PrivacyAdminStatusResponse> UpdateHoldAsync(
        int actorUserId,
        RetentionHoldUpdateRequest request,
        CancellationToken cancellationToken);
}

public interface IRetentionService
{
    Task<RetentionCycleResponse> RunCycleAsync(CancellationToken cancellationToken);
    RetentionMetricsResponse GetMetrics();
}

public interface ISecurityEventService
{
    Task<long> RecordAsync(
        SecurityEventWrite securityEvent,
        CancellationToken cancellationToken);
    Task<SecurityEventListResponse> ListAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
    Task<ExportDocument> ExportAsync(
        SecurityEventQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
}

public interface IEventOutboxService
{
    Task<IReadOnlyList<EventDeliveryEnvelope>> ClaimAsync(
        string workerId,
        int limit,
        TimeSpan lease,
        CancellationToken cancellationToken);
    Task<bool> MarkDeliveredAsync(
        long eventId,
        string workerId,
        CancellationToken cancellationToken);
    Task<bool> MarkFailedAsync(
        long eventId,
        string workerId,
        string failureMessage,
        CancellationToken cancellationToken);
}

public interface IRequestLogService
{
    Task<RequestLogListResponse> ListAsync(
        RequestLogQuery query,
        int actorUserId,
        CancellationToken cancellationToken);
    RequestLogMetricsResponse GetMetrics();
}

public interface IPresenceService
{
    Task<PresenceResponse> GetOnlineAsync(int actorUserId, CancellationToken cancellationToken);
    Task<HeartbeatResponse> HeartbeatAsync(int userId, CancellationToken cancellationToken);
}

public interface IGeoService
{
    GeoPlace Resolve(double latitude, double longitude);
}

public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(int userId, CancellationToken cancellationToken);
    Task<AnalyticsResponse> GetAnalyticsAsync(int userId, CancellationToken cancellationToken);
}

public interface IHealthService
{
    LivenessResponse GetLiveness();
    Task<(ReadinessResponse Response, bool IsReady)> GetReadinessAsync(
        CancellationToken cancellationToken);
}
