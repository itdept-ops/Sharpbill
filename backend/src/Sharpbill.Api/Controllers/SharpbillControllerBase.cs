using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Common;

namespace Sharpbill.Api.Controllers;

[ApiController]
public abstract class SharpbillControllerBase : ControllerBase
{
    protected int ActorUserId
    {
        get
        {
            string? value = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int userId)
                ? userId
                : throw ApiException.Unauthorized("NOT_AUTHENTICATED", "Not signed in");
        }
    }

    protected Guid? SessionJti =>
        Guid.TryParse(User.FindFirstValue(SharpbillClaimTypes.SessionJti), out Guid jti) ? jti : null;

    protected RequestContext RequestContext => new()
    {
        RequestId = HttpContext.TraceIdentifier,
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } value ? value : null,
        SessionJti = SessionJti,
    };
}

public static class SharpbillClaimTypes
{
    public const string Permission = "sharpbill:permission";
    public const string SessionJti = "sharpbill:session_jti";
    public const string SessionIssuedAt = "sharpbill:session_issued_at";
}
