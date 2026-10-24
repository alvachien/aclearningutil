using Microsoft.AspNetCore.Authorization;

namespace aclearningutil.Authorization;

/// <summary>
/// Authorization requirement that allows only users whose user-ID claim
/// (<see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>, with a
/// "sub" fallback - matching the convention used by the user-scoped controllers)
/// appears in the configured admin allowlist. The allowlist is sourced from
/// "Admin:UserIds" in appsettings.json. An empty allowlist fails closed: no one
/// is treated as an admin.
/// </summary>
public sealed class AdminUserRequirement : IAuthorizationRequirement
{
    public IReadOnlySet<string> AdminUserIds { get; }

    public AdminUserRequirement(IEnumerable<string> adminUserIds)
    {
        AdminUserIds = new HashSet<string>(
            adminUserIds ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
    }
}
