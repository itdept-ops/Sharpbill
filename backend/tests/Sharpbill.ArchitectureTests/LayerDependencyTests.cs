using System.Reflection;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Application.Exports;
using Sharpbill.Application.Identity;
using Sharpbill.Application.Policies;
using Sharpbill.Application.Users;
using Sharpbill.Api.Controllers;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;
using Sharpbill.Infrastructure.Database;
using Sharpbill.Infrastructure.Services.Identity;

namespace Sharpbill.ArchitectureTests;

public sealed class LayerDependencyTests
{
    [Fact]
    public void DomainHasNoOutwardSharpbillDependencies()
    {
        string[] references = SharpbillReferences(typeof(User).Assembly);
        Assert.Empty(references);
    }

    [Fact]
    public void ApplicationDoesNotReferenceRuntimeLayers()
    {
        Assembly applicationAssembly = typeof(IUserService).Assembly;
        string[] references = SharpbillReferences(applicationAssembly);
        Assert.DoesNotContain("Sharpbill.Infrastructure", references);
        Assert.DoesNotContain("Sharpbill.Workers", references);
        Assert.DoesNotContain("Sharpbill.Api", references);

        string[] providerReferences = AssemblyReferences(applicationAssembly);
        Assert.DoesNotContain("MySqlConnector", providerReferences);
        Assert.DoesNotContain("Microsoft.Extensions.Options", providerReferences);
    }

    [Fact]
    public void InfrastructureDoesNotReferenceDeliveryLayers()
    {
        string[] references = SharpbillReferences(typeof(SharpbillOptions).Assembly);
        Assert.DoesNotContain("Sharpbill.Workers", references);
        Assert.DoesNotContain("Sharpbill.Api", references);
    }

    [Fact]
    public void ApplicationServiceBoundariesFollowInterfaceConvention()
    {
        Type[] services = typeof(IUserService).Assembly.GetTypes()
            .Where(static type => type.IsInterface && type.Namespace == "Sharpbill.Application.Abstractions" &&
                                  type.Name.EndsWith("Service", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(services);
        Assert.All(services, static service => Assert.StartsWith("I", service.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void ControllersDoNotDependOnPersistenceBoundaries()
    {
        string[] violations = typeof(AuthController).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(static type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()
                    .Where(parameter => IsPersistenceBoundary(parameter.ParameterType))
                    .Select(parameter => $"{type.FullName} -> {parameter.ParameterType.FullName}")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void UserServiceFacadeDependsOnlyOnFocusedServices()
    {
        ConstructorInfo constructor = Assert.Single(typeof(UserService).GetConstructors());
        Type[] dependencies = constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [
                typeof(IUserQueryService),
                typeof(IUserProfileService),
                typeof(IUserAccessService),
                typeof(IUserLifecycleService),
            ],
            dependencies);
    }

    [Fact]
    public void UserUseCaseCodeIsOwnedByApplication()
    {
        Assembly applicationAssembly = typeof(IUserService).Assembly;
        Type[] boundaryTypes =
        [
            typeof(UserService),
            typeof(UserQueryService),
            typeof(UserProfileService),
            typeof(UserAccessService),
            typeof(UserLifecycleService),
            typeof(UserOperationContext),
            typeof(UserAuditWriter),
            typeof(UserResponseMapper),
            typeof(UserUseCaseOptions),
            typeof(CsvExportWriter),
            typeof(UserAccountPolicy),
            typeof(SecurityEventWriteFactory),
            typeof(SecurityEventMetadata),
            typeof(InvariantTimestamp),
            typeof(BusinessErrors),
        ];

        Assert.All(boundaryTypes, type => Assert.Equal(applicationAssembly, type.Assembly));
    }

    [Fact]
    public void InfrastructureDoesNotImplementUserUseCaseBoundaries()
    {
        Type[] boundaries =
        [
            typeof(IUserService),
            typeof(IUserQueryService),
            typeof(IUserProfileService),
            typeof(IUserAccessService),
            typeof(IUserLifecycleService),
        ];
        string[] violations = typeof(SharpbillOptions).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && boundaries.Any(boundary =>
                boundary.IsAssignableFrom(type)))
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void UserUseCaseConstructorsDoNotDependOnRuntimeTypes()
    {
        Assembly infrastructureAssembly = typeof(SharpbillOptions).Assembly;
        Type[] useCases =
        [
            typeof(UserQueryService),
            typeof(UserProfileService),
            typeof(UserAccessService),
            typeof(UserLifecycleService),
            typeof(UserOperationContext),
            typeof(UserAuditWriter),
        ];
        string[] violations = useCases
            .SelectMany(static type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters()
                    .Select(parameter => (Service: type, Dependency: parameter.ParameterType))))
            .Where(candidate =>
                candidate.Dependency.Assembly == infrastructureAssembly ||
                candidate.Dependency.FullName?.StartsWith(
                    "Microsoft.Extensions.Options.",
                    StringComparison.Ordinal) == true)
            .Select(candidate =>
                $"{candidate.Service.FullName} -> {candidate.Dependency.FullName}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void MySqlExecutorIsTheOnlyRuntimeTransactionImplementation()
    {
        Assert.Equal(typeof(IUserService).Assembly, typeof(ITransactionExecutor).Assembly);
        Type[] implementations = typeof(MySqlTransientRetryExecutor).Assembly.GetTypes()
            .Where(static type =>
                !type.IsAbstract &&
                typeof(ITransactionExecutor).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal([typeof(MySqlTransientRetryExecutor)], implementations);
    }

    [Fact]
    public void AuthServiceFacadeDependsOnlyOnFocusedServices()
    {
        ConstructorInfo constructor = Assert.Single(typeof(AuthService).GetConstructors());
        Type[] dependencies = constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [
                typeof(IAuthConfigurationService),
                typeof(IExternalLoginService),
                typeof(IDevelopmentLoginService),
                typeof(IAuthAccountService),
                typeof(IAuthSessionOperationsService),
            ],
            dependencies);
    }

    [Fact]
    public void AuthenticationBoundaryCodeIsOwnedByApplication()
    {
        Assembly applicationAssembly = typeof(IAuthService).Assembly;
        Type[] boundaryTypes =
        [
            typeof(AuthenticationPolicy),
            typeof(AuthenticationAdmissionService),
            typeof(IdentityUserMapper),
            typeof(IdentitySecurityEventFactory),
        ];

        Assert.All(
            boundaryTypes,
            type =>
            {
                Assert.Equal(applicationAssembly, type.Assembly);
                Assert.Equal("Sharpbill.Application.Identity", type.Namespace);
            });
    }

    [Fact]
    public void AuthenticationPolicyReceivesOnlySecretFreeProjection()
    {
        ConstructorInfo constructor = Assert.Single(typeof(AuthenticationPolicy).GetConstructors());
        Type[] dependencies = constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();
        string[] optionProperties = typeof(AuthenticationPolicyOptions).GetProperties()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([typeof(AuthenticationPolicyOptions)], dependencies);
        Assert.Equal(
            [
                nameof(AuthenticationPolicyOptions.DevelopmentAdminEmails),
                nameof(AuthenticationPolicyOptions.DevelopmentAuthenticationEnabled),
                nameof(AuthenticationPolicyOptions.GoogleAdminSubjects),
                nameof(AuthenticationPolicyOptions.GoogleClientId),
                nameof(AuthenticationPolicyOptions.IsLocal),
                nameof(AuthenticationPolicyOptions.MicrosoftAdminObjectIds),
                nameof(AuthenticationPolicyOptions.MicrosoftAdminTenantId),
                nameof(AuthenticationPolicyOptions.MicrosoftClientId),
            ],
            optionProperties);
    }

    [Fact]
    public void AuthenticationAdmissionDependsOnlyOnApplicationBoundaries()
    {
        ConstructorInfo constructor =
            Assert.Single(typeof(AuthenticationAdmissionService).GetConstructors());
        Type[] dependencies = constructor.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Equal(
            [
                typeof(IIdentityRepository),
                typeof(IUserRepository),
                typeof(IRoleRepository),
                typeof(IClock),
                typeof(AuthenticationPolicy),
            ],
            dependencies);
    }

    private static bool IsPersistenceBoundary(Type type) =>
        type == typeof(IUnitOfWork) ||
        type.Name.EndsWith("Repository", StringComparison.Ordinal) ||
        type == typeof(DatabaseSession) ||
        type == typeof(IDatabaseConnectionFactory) ||
        typeof(DbConnection).IsAssignableFrom(type) ||
        typeof(DbTransaction).IsAssignableFrom(type);

    private static string[] SharpbillReferences(Assembly assembly) => assembly.GetReferencedAssemblies()
        .Select(static reference => reference.Name)
        .Where(static name => name?.StartsWith("Sharpbill.", StringComparison.Ordinal) == true)
        .Cast<string>()
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] AssemblyReferences(Assembly assembly) => assembly.GetReferencedAssemblies()
        .Select(static reference => reference.Name)
        .OfType<string>()
        .Order(StringComparer.Ordinal)
        .ToArray();
}
