using Sharpbill.Application.Abstractions;
using Sharpbill.Application.Common;
using Sharpbill.Contracts.Users;
using Sharpbill.Domain.Entities;

namespace Sharpbill.Infrastructure.Services.Identity;

internal sealed class AuthAccountService : IAuthAccountService
{
    private readonly IUserRepository _userRepository;

    public AuthAccountService(IUserRepository userRepository)
    {
        _userRepository = userRepository ?? throw new ArgumentNullException(nameof(userRepository));
    }

    public async Task<UserResponse> GetCurrentUserAsync(
        int userId,
        CancellationToken cancellationToken)
    {
        User user = await _userRepository.FindAsync(
            userId,
            false,
            cancellationToken).ConfigureAwait(false)
            ?? throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        if (!AuthenticationPolicy.IsAuthenticatable(user))
        {
            throw ApiException.Unauthorized("INVALID_SESSION", "Session invalid or expired");
        }

        return IdentityUserMapper.ToResponse(user, online: true);
    }
}
