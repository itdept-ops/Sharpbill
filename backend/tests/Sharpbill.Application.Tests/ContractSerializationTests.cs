using System.Text.Json;
using Sharpbill.Contracts.Common;
using Sharpbill.Contracts.Users;

namespace Sharpbill.Application.Tests;

public sealed class ContractSerializationTests
{
    [Fact]
    public void ProfilePatchDistinguishesOmittedFromExplicitNull()
    {
        const string json = "{\"display_name\":null}";

        var patch = JsonSerializer.Deserialize<ProfileUpdateRequest>(json);

        Assert.NotNull(patch);
        Assert.True(patch.DisplayName.HasValue);
        Assert.Null(patch.DisplayName.Value);
        Assert.False(patch.Title.HasValue);
    }

    [Fact]
    public void ProfilePatchRejectsUnknownJsonFields()
    {
        const string json = "{\"display_name\":\"A\",\"unexpected\":true}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProfileUpdateRequest>(json));
    }

    [Fact]
    public void PatchFieldsSerializeUsingExactSnakeCaseContract()
    {
        var patch = new ProfileUpdateRequest
        {
            DisplayName = new PatchField<string?>("Operator"),
            UiPreferences = new PatchField<UiPreferencesContract?>(new UiPreferencesContract
            {
                BaseTone = new PatchField<string?>("ink"),
            }),
        };

        var json = JsonSerializer.Serialize(patch);

        Assert.Equal("{\"display_name\":\"Operator\",\"ui_prefs\":{\"base_tone\":\"ink\"}}", json);
    }

    [Fact]
    public void BulkActionUsesSnakeCaseEnumValue()
    {
        var request = new BulkActionRequest
        {
            Ids = [1],
            Action = BulkUserActionContract.AssignRole,
            RoleId = 2,
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"action\":\"assign_role\"", json, StringComparison.Ordinal);
    }
}
