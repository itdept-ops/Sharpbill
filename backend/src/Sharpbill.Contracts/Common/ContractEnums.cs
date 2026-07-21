using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Common;

[JsonConverter(typeof(StrictJsonStringEnumConverter<ProviderContract>))]
public enum ProviderContract
{
    [JsonStringEnumMemberName("google")]
    Google,
    [JsonStringEnumMemberName("microsoft")]
    Microsoft,
    [JsonStringEnumMemberName("dev")]
    Dev,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<UserStatusContract>))]
public enum UserStatusContract
{
    [JsonStringEnumMemberName("active")]
    Active,
    [JsonStringEnumMemberName("pending")]
    Pending,
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<SignupModeContract>))]
public enum SignupModeContract
{
    [JsonStringEnumMemberName("open")]
    Open,
    [JsonStringEnumMemberName("approval")]
    Approval,
    [JsonStringEnumMemberName("closed")]
    Closed,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<BulkUserActionContract>))]
public enum BulkUserActionContract
{
    [JsonStringEnumMemberName("activate")]
    Activate,
    [JsonStringEnumMemberName("deactivate")]
    Deactivate,
    [JsonStringEnumMemberName("approve")]
    Approve,
    [JsonStringEnumMemberName("assign_role")]
    AssignRole,
}

[JsonConverter(typeof(StrictJsonStringEnumConverter<LegalAcceptanceContract>))]
public enum LegalAcceptanceContract
{
    [JsonStringEnumMemberName("agreement")]
    Agreement,
    [JsonStringEnumMemberName("acknowledgement")]
    Acknowledgement,
}

public sealed class StrictJsonStringEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public StrictJsonStringEnumConverter()
        : base(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
    {
    }
}
