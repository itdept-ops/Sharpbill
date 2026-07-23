using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Users;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;
using Sharpbill.Workers;

namespace Sharpbill.IntegrationTests.Configuration;

public sealed class UserServiceCompositionTests
{
    [Fact]
    public async Task RuntimeCompositionResolvesApplicationUserUseCasesAsync()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_ENV"] = "local",
                ["DB_HOST"] = "localhost",
                ["DB_NAME"] = "sharpbill",
                ["DB_USER"] = "sharpbill",
                ["DB_PASSWORD"] = "local-test-password",
                ["SESSION_JWT_SECRET"] = "local-test-session-secret-0123456789-ABCDEF",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSharpbillConfiguration(configuration);
        services.AddSharpbillRuntime();
        services.AddSharpbillWorkers();

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IServiceProvider scopedServices = scope.ServiceProvider;

        Assert.IsType<UserService>(scopedServices.GetRequiredService<IUserService>());
        Assert.IsType<UserQueryService>(
            scopedServices.GetRequiredService<IUserQueryService>());
        Assert.IsType<UserProfileService>(
            scopedServices.GetRequiredService<IUserProfileService>());
        Assert.IsType<UserAccessService>(
            scopedServices.GetRequiredService<IUserAccessService>());
        Assert.IsType<UserLifecycleService>(
            scopedServices.GetRequiredService<IUserLifecycleService>());
        Assert.Same(
            scopedServices.GetRequiredService<MySqlTransientRetryExecutor>(),
            scopedServices.GetRequiredService<ITransactionExecutor>());

        UserUseCaseOptions userOptions =
            scopedServices.GetRequiredService<UserUseCaseOptions>();
        Assert.Equal(25 * 1024 * 1024, userOptions.ExportMaxBytes);
        Assert.Equal(24, userOptions.PreciseLocationHours);
    }
}
