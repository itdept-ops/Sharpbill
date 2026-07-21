namespace Sharpbill.Domain.Enums;

public enum IdentityProvider
{
    Google,
    Microsoft,
    Dev,
}

public enum UserStatus
{
    Active,
    Pending,
    Disabled,
}

public enum SignupMode
{
    Open,
    Approval,
    Closed,
}

public enum SecurityEventOutcome
{
    Success,
    Failure,
    Denied,
}

public enum SecurityEventSeverity
{
    Info,
    Warning,
    Critical,
}

public enum EventDeliveryStatus
{
    Pending,
    Retry,
    Leased,
    Delivered,
    DeadLetter,
}

public enum LegalAcceptanceAction
{
    Agreement,
    Acknowledgement,
}

public enum BulkUserAction
{
    Activate,
    Deactivate,
    Approve,
    AssignRole,
}
