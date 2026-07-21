using Microsoft.Extensions.Logging.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.IntegrationTests.Operations;

public sealed class GeoServiceTests
{
    [Theory]
    [InlineData(37.7749, -122.4194, "San Francisco, California, US", "America/Los_Angeles")]
    [InlineData(35.6762, 139.6503, "Tokyo, Tokyo, JP", "Asia/Tokyo")]
    [InlineData(-1.2921, 36.8219, "Nairobi, Nairobi Area, KE", "Africa/Nairobi")]
    [InlineData(-33.8688, 151.2093, "Sydney, New South Wales, AU", "Australia/Sydney")]
    [InlineData(0, -60, "Rio Preto da Eva, Amazonas, BR", "America/Manaus")]
    public void WorldwideOfflineLookupPreservesPlaceAndTimezoneContract(
        double latitude,
        double longitude,
        string expectedPlace,
        string expectedTimezone)
    {
        var service = new GeoService(NullLogger<GeoService>.Instance);

        GeoPlace result = service.Resolve(latitude, longitude);

        Assert.Equal(expectedPlace, result.Place);
        Assert.Equal(expectedTimezone, result.Timezone);
    }

    [Fact]
    public void LookupRejectsInvalidCoordinates()
    {
        var service = new GeoService(NullLogger<GeoService>.Instance);

        ApiException exception = Assert.Throws<ApiException>(() => service.Resolve(91, 0));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("INVALID_LOCATION", exception.Code);
    }
}
