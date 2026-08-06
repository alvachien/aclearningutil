using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace aclearningutil.Authorization;

/// <summary>
/// Validates an <see cref="AdminUserRequirement"/> against the current user's
/// user-ID claim. Extraction mirrors <c>GetUserId()</c> in the user-scoped
/// controllers: <see cref="ClaimTypes.NameIdentifier"/> first, "sub" as fallback.
/// On no match the requirement is left un-satisfied so the default 403 handler
/// applies.
/// </summary>
public sealed class AdminUserAuthorizationHandler : AuthorizationHandler<AdminUserRequirement>
{
    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AdminUserRequirement requirement)
    {
        if (context.User is null)
        {
            return Task.CompletedTask;
        }

        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("sub");

        if (!string.IsNullOrEmpty(userId) && requirement.AdminUserIds.Contains(userId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
