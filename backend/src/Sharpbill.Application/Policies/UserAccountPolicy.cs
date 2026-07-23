using Sharpbill.Domain.Entities;

namespace Sharpbill.Application.Policies;

public static class UserAccountPolicy
{
    public static bool IsAuthenticatable(User? user) =>
        user is { IsActive: true, IsApproved: true, ErasedAt: null };
}
