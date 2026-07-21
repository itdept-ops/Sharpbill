using System.Text.Json;
using Sharpbill.Contracts.Common;

namespace Sharpbill.IntegrationTests.Contracts;

public sealed class StrictEnumContractTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public void ContractEnumsRejectNumericJson(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BulkUserActionContract>(json));
    }

    [Fact]
    public void ContractEnumsRetainLowerSnakeCaseStrings()
    {
        BulkUserActionContract value = JsonSerializer.Deserialize<BulkUserActionContract>("\"assign_role\"");

        Assert.Equal(BulkUserActionContract.AssignRole, value);
        Assert.Equal("\"assign_role\"", JsonSerializer.Serialize(value));
    }
}
