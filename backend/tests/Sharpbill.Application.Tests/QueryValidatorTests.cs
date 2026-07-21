using Sharpbill.Application.Validation;
using Sharpbill.Contracts.Operations;

namespace Sharpbill.Application.Tests;

public sealed class QueryValidatorTests
{
    private readonly SecurityEventQueryValidator _validator = new();

    [Fact]
    public void SecurityEventQueryAcceptsLegacyFilterContract()
    {
        var result = _validator.Validate(new SecurityEventQuery
        {
            Limit = 500,
            BeforeId = 1,
            EventType = "auth.login",
            Outcome = "denied",
            Severity = "warning",
            ActorUserId = 1,
            RequestId = "r",
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void SecurityEventQueryRejectsInvalidLegacyFilters()
    {
        var result = _validator.Validate(new SecurityEventQuery
        {
            EventType = "ab",
            Outcome = "unknown",
            ActorUserId = 0,
            RequestId = string.Empty,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error => error.Field == "event_type");
        Assert.Contains(result.Errors, static error => error.Field == "outcome");
        Assert.Contains(result.Errors, static error => error.Field == "actor_user_id");
        Assert.Contains(result.Errors, static error => error.Field == "request_id");
    }
}
