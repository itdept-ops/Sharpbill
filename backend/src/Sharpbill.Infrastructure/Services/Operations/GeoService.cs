using System.Globalization;
using GeoTimeZone;
using Microsoft.Extensions.Logging;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;

namespace Sharpbill.Infrastructure.Services.Operations;

/// <summary>Offline, deterministic location enrichment; precise coordinates never leave the process.</summary>
public sealed partial class GeoService(ILogger<GeoService> logger) : IGeoService
{
    private const string PlaceResourceName = "Sharpbill.Geo.rg_cities1000.csv";
    private static readonly Lazy<PlaceIndex> Places = new(
        LoadPlaces,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public GeoPlace Resolve(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            throw ApiException.BadRequest("INVALID_LOCATION", "Coordinates are outside their allowed range");
        }

        string? timezone = ResolveTimezone(latitude, longitude);
        string? place = ResolvePlace(latitude, longitude);
        return new GeoPlace(place, timezone);
    }

    private string? ResolvePlace(double latitude, double longitude)
    {
        try
        {
            return Places.Value.Nearest(latitude, longitude);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
        {
            LogPlaceLookupFailure(logger, exception);
            return null;
        }
    }

    private string? ResolveTimezone(double latitude, double longitude)
    {
        try
        {
            return TimeZoneLookup.GetTimeZone(latitude, longitude).Result;
        }
        catch (Exception exception)
        {
            LogTimezoneLookupFailure(logger, exception);
            return null;
        }
    }

    private static PlaceIndex LoadPlaces()
    {
        Stream resource = typeof(GeoService).Assembly.GetManifestResourceStream(PlaceResourceName)
            ?? throw new InvalidDataException($"Embedded place index '{PlaceResourceName}' is missing.");
        using (resource)
        using (var reader = new StreamReader(resource, detectEncodingFromByteOrderMarks: true))
        {
            string? header = reader.ReadLine();
            if (!string.Equals(header, "lat,lon,name,admin1,admin2,cc", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The embedded place index has an unexpected schema.");
            }

            var places = new List<Place>(145_000);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                string[] fields = ParseCsvLine(line);
                if (fields.Length != 6 ||
                    !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double latitude) ||
                    !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double longitude))
                {
                    throw new InvalidDataException("The embedded place index contains a malformed record.");
                }

                string label = string.Join(
                    ", ",
                    new[] { fields[2], fields[3], fields[5] }
                        .Where(static field => !string.IsNullOrEmpty(field)));
                if (label.Length > 0)
                {
                    places.Add(new Place(latitude, longitude, label));
                }
            }

            if (places.Count < 100_000)
            {
                throw new InvalidDataException("The embedded worldwide place index is incomplete.");
            }

            return new PlaceIndex(places.ToArray());
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>(6);
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new FormatException("The embedded place index contains an unterminated quoted field.");
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }

    private sealed class PlaceIndex(Place[] places)
    {
        public string Nearest(double latitude, double longitude)
        {
            Place nearest = places[0];
            double nearestDistance = SquaredDistance(latitude, longitude, nearest);
            for (int index = 1; index < places.Length; index++)
            {
                Place candidate = places[index];
                double distance = SquaredDistance(latitude, longitude, candidate);
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }

            return nearest.Label;
        }

        private static double SquaredDistance(double latitude, double longitude, Place place)
        {
            double latitudeDifference = latitude - place.Latitude;
            double longitudeDifference = longitude - place.Longitude;
            return (latitudeDifference * latitudeDifference) +
                (longitudeDifference * longitudeDifference);
        }
    }

    private readonly record struct Place(double Latitude, double Longitude, string Label);

    [LoggerMessage(EventId = 2400, Level = LogLevel.Error, Message = "Offline reverse-geocode lookup failed")]
    private static partial void LogPlaceLookupFailure(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2401, Level = LogLevel.Error, Message = "Offline timezone lookup failed")]
    private static partial void LogTimezoneLookupFailure(ILogger logger, Exception exception);
}
