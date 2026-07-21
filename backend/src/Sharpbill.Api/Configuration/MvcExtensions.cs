using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Api.Filters;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Api.Configuration;

public static class MvcExtensions
{
    public static IMvcBuilder AddSharpbillControllers(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                object[] errors = context.ModelState
                    .Where(static pair => pair.Value?.Errors.Count > 0)
                    .SelectMany(static pair => pair.Value!.Errors.Select(error => new
                    {
                        loc = pair.Key,
                        msg = string.IsNullOrWhiteSpace(error.ErrorMessage) ? "Invalid value." : error.ErrorMessage,
                        type = "value_error",
                    }))
                    .ToArray<object>();

                return new ObjectResult(new
                {
                    detail = new
                    {
                        code = "VALIDATION_ERROR",
                        message = "Invalid request",
                        errors,
                    },
                })
                {
                    StatusCode = StatusCodes.Status422UnprocessableEntity,
                };
            };
        });

        bool developmentAuthenticationEnabled =
            string.Equals(configuration["APP_ENV"], "local", StringComparison.OrdinalIgnoreCase) &&
            bool.TryParse(configuration["DEV_AUTH_ENABLED"], out bool enabled) && enabled &&
            DevelopmentAuthenticationGuard.IsStrongIndependentSecret(
                configuration["DEV_AUTH_SECRET"],
                configuration["SESSION_JWT_SECRET"]);

        services.AddScoped<DatabaseConnectionReleaseFilter>();
        return services.AddControllers(options =>
            options.Filters.AddService<DatabaseConnectionReleaseFilter>())
            .ConfigureApplicationPartManager(manager =>
                manager.FeatureProviders.Add(new DevelopmentControllerFeatureProvider(
                    developmentAuthenticationEnabled)))
            .AddJsonOptions(options =>
        {
            JsonSerializerOptions json = options.JsonSerializerOptions;
            json.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            json.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
            json.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            json.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            json.Converters.Add(new JsonStringEnumConverter(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false));
        });
    }
}
