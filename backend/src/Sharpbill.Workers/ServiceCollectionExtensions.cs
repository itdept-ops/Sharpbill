using Microsoft.Extensions.DependencyInjection;
using Sharpbill.Infrastructure.Services.Operations;

namespace Sharpbill.Workers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSharpbillWorkers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<RequestLogBufferWorker>();
        services.AddSingleton<IRequestLogBuffer>(static provider =>
            provider.GetRequiredService<RequestLogBufferWorker>());
        services.AddHostedService(static provider => provider.GetRequiredService<RequestLogBufferWorker>());
        services.AddHostedService<RetentionWorker>();
        return services;
    }
}
