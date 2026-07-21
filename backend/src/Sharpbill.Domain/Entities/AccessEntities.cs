#pragma warning disable CA1711 // Schema-accurate RBAC entity names are intentional.

namespace Sharpbill.Domain.Entities;

public sealed record Permission
{
    public required int Id { get; init; }
    public required string Key { get; init; }
    public string? Description { get; init; }
    public bool IsSystem { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record Role
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsSystem { get; init; }
    public int Version { get; init; } = 1;
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public IReadOnlyList<Permission> Permissions { get; init; } = [];

    public IReadOnlySet<string> PermissionKeys =>
        Permissions.Select(static permission => permission.Key).ToHashSet(StringComparer.Ordinal);
}

public sealed record RolePermission(int RoleId, int PermissionId);

public sealed record UserPermission(int UserId, int PermissionId);

#pragma warning restore CA1711
