using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using aclearningutil.Authorization;

namespace aclearningutil.test.Authorization;

/// <summary>
/// Unit tests for <see cref="AdminUserAuthorizationHandler"/>. These exercise the
/// actual authorization decision (the controller-level tests instantiate the
/// controller directly and bypass the auth pipeline, so they cannot cover this).
/// </summary>
public class AdminUserAuthorizationHandlerTests
{
    private static ClaimsPrincipal UserWithClaim(string claimType, string value)
        => new(new ClaimsIdentity(new[] { new Claim(claimType, value) }, "Test"));

    private static async Task<bool> EvaluateAsync(AdminUserRequirement requirement, ClaimsPrincipal user)
    {
        var handler = new AdminUserAuthorizationHandler();
        var context = new AuthorizationHandlerContext(new IAuthorizationRequirement[] { requirement }, user, resource: null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Succeeds_When_NameIdentifier_Is_In_Allowlist()
    {
        var requirement = new AdminUserRequirement(new[] { "admin-1", "admin-2" });
        var user = UserWithClaim(ClaimTypes.NameIdentifier, "admin-1");

        (await EvaluateAsync(requirement, user)).Should().BeTrue();
    }

    [Fact]
    public async Task Succeeds_When_Sub_Claim_Is_In_Allowlist_And_NameIdentifier_Absent()
    {
        // JWT bearer auth may leave the user ID under "sub" (no inbound mapping);
        // the handler must honor the same fallback as GetUserId() in the controllers.
        var requirement = new AdminUserRequirement(new[] { "admin-1" });
        var user = UserWithClaim("sub", "admin-1");

        (await EvaluateAsync(requirement, user)).Should().BeTrue();
    }

    [Fact]
    public async Task Does_Not_Succeed_When_Id_Not_In_Allowlist()
    {
        var requirement = new AdminUserRequirement(new[] { "admin-1" });
        var user = UserWithClaim(ClaimTypes.NameIdentifier, "ordinary-user");

        (await EvaluateAsync(requirement, user)).Should().BeFalse();
    }

    [Fact]
    public async Task Does_Not_Succeed_When_No_Id_Claim_Present()
    {
        var requirement = new AdminUserRequirement(new[] { "admin-1" });
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("name", "someone") }, "Test"));

        (await EvaluateAsync(requirement, user)).Should().BeFalse();
    }

    [Fact]
    public async Task Does_Not_Succeed_When_Allowlist_Empty()
    {
        // Fail closed: an empty allowlist (default appsettings) means no one is admin.
        var requirement = new AdminUserRequirement(Array.Empty<string>());
        var user = UserWithClaim(ClaimTypes.NameIdentifier, "anyone");

        (await EvaluateAsync(requirement, user)).Should().BeFalse();
    }
}
