using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Auth;
using Sharpbill.Domain.Entities;
using Sharpbill.Infrastructure.Configuration;

namespace Sharpbill.Infrastructure.Services.Identity;

public sealed class DevelopmentAuthService(
    IAuthService authService,
    IRoleRepository roleRepository,
    IRequestContextAccessor requestContextAccessor,
    IOptions<SharpbillOptions> options) : IDevelopmentAuthService
{
    private readonly byte[]? _expectedSecretHash = HashSecret(
        options.Value.DevelopmentAuthentication.Secret);

    public Task<AuthenticatedSession> LoginAsync(
        DevLoginRequest request,
        string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        RequireSecret(suppliedSecret);
        return authService.DevLoginAsync(
            request,
            requestContextAccessor.Current,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListRolesAsync(
        string? suppliedSecret,
        CancellationToken cancellationToken)
    {
        RequireSecret(suppliedSecret);
        IReadOnlyList<Role> roles = await roleRepository.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        return roles
            .OrderBy(static role => role.Id)
            .Select(static role => role.Name)
            .ToArray();
    }

    private void RequireSecret(string? suppliedSecret)
    {
        byte[]? suppliedSecretHash = HashSecret(suppliedSecret);
        bool valid = _expectedSecretHash is not null &&
            suppliedSecretHash is not null &&
            CryptographicOperations.FixedTimeEquals(
                _expectedSecretHash,
                suppliedSecretHash);
        if (suppliedSecretHash is not null)
        {
            CryptographicOperations.ZeroMemory(suppliedSecretHash);
        }

        if (!valid)
        {
            throw ApiException.NotFound("Not found");
        }
    }

    private static byte[]? HashSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return SHA256.HashData(secretBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }
}
