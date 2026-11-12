using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using aclearningutil.Controllers;
using aclearningutil.RateLimiting;

namespace aclearningutil.test.RateLimiting;

/// <summary>
/// Per-user partitioning of the statistics write endpoints (review finding H2).
/// The middleware's own machinery (window bookkeeping, turning a rejection into
/// 429 via RejectionStatusCode) is framework-tested; this suite covers OUR code:
/// the partition key derivation, per-user bucket separation, the ceiling versus
/// the honest heartbeat cadence, and the attribute wiring on the three actions.
/// </summary>
public class UserWriteRateLimitingTests
{
    /// <summary>A context carrying the caller's claims; null name-identifier with a
    /// null sub simulates a token without a user id (or pre-auth pipeline position).</summary>
    private static HttpContext UserContext(string? nameIdentifier, string? sub = null)
    {
        var claims = new List<Claim>();
        if (nameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }
        if (sub is not null)
        {
            claims.Add(new Claim("sub", sub));
        }
        var identity = new ClaimsIdentity(claims, "Test");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    [Fact]
    public void PartitionKey_Follows_The_Shared_Auth_Contract()
    {
        UserWriteRateLimiting.PartitionKey(UserContext("user-a"))
            .Should().Be("user-a");
        UserWriteRateLimiting.PartitionKey(UserContext(null, sub: "sub-b"))
            .Should().Be("sub-b", "ClaimTypes.NameIdentifier with the \"sub\" fallback, like the controllers");
        UserWriteRateLimiting.PartitionKey(UserContext(null))
            .Should().Be("anonymous",
                "no claim and no remote IP still yields a usable bucket, never the empty key");
    }

    [Fact]
    public void ForPartition_Keys_By_User_So_Users_Get_Independent_Buckets()
    {
        var a = UserWriteRateLimiting.ForPartition(UserContext("user-a"));
        var b = UserWriteRateLimiting.ForPartition(UserContext("user-b"));

        a.PartitionKey.Should().Be("user-a");
        b.PartitionKey.Should().NotBe(a.PartitionKey,
            "distinct keys make the middleware's partitioner cache one window limiter per user — " +
            "one user's rejected burst can never spend another user's budget");
    }

    [Fact]
    public void Partition_Limiter_Allows_The_Honest_Heartbeat_Rate_And_Blocks_A_Burst()
    {
        var partition = UserWriteRateLimiting.ForPartition(UserContext("user-a"));
        using var limiter = partition.Factory(partition.PartitionKey);

        // Back-to-back acquisitions run well inside one minute, so the fixed
        // window cannot replenish mid-test.
        for (var i = 0; i < UserWriteRateLimiting.PermitLimitPerMinute; i++)
        {
            using var lease = limiter.AttemptAcquire();
            lease.IsAcquired.Should().BeTrue($"write {i + 1} of the window must pass");
        }

        using var overflow = limiter.AttemptAcquire();
        overflow.IsAcquired.Should().BeFalse(
            "writes over the per-user ceiling are rejected — the middleware turns this into 429");
    }

    [Fact]
    public void Ceiling_Leaves_Margin_Above_The_Honest_One_Beat_Per_Minute_Client()
    {
        UserWriteRateLimiting.PermitLimitPerMinute.Should().BeGreaterThanOrEqualTo(5,
            "several open tabs plus login/preferences calls must never trip the limiter");
        UserWriteRateLimiting.PermitLimitPerMinute.Should().BeLessThan(60,
            "and yet a runaway loop is cut down to a fraction of the unbounded write-lock pressure");
    }

    // ── endpoint wiring (reflection over the controller actions) ─────────────

    private static T? AttributeOf<T>(MethodInfo action) where T : Attribute =>
        action.GetCustomAttribute<T>();

    private static long? BodyCapOf(MethodInfo action) =>
        AttributeOf<RequestSizeLimitAttribute>(action) is { } attr
            ? ((IRequestSizeLimitMetadata)attr).MaxRequestBodySize
            : null;

    [Fact]
    public void Beat_And_Preferences_Endpoints_Carry_The_Policy_And_The_Body_Cap()
    {
        var beat = typeof(UserLearningTimesController).GetMethod(
            nameof(UserLearningTimesController.RecordBeat))!;
        AttributeOf<EnableRateLimitingAttribute>(beat)!.PolicyName
            .Should().Be(UserWriteRateLimiting.PolicyName);
        BodyCapOf(beat).Should().Be(UserWriteRateLimiting.MaxRequestBodyBytes);

        var preferences = typeof(UserLearningTimesController).GetMethod(
            nameof(UserLearningTimesController.UpdatePreferences))!;
        AttributeOf<EnableRateLimitingAttribute>(preferences)!.PolicyName
            .Should().Be(UserWriteRateLimiting.PolicyName);
        BodyCapOf(preferences).Should().Be(UserWriteRateLimiting.MaxRequestBodyBytes);
    }

    [Fact]
    public void Login_Post_Carries_The_Policy_And_No_Body_Cap_It_Has_No_Body()
    {
        var login = typeof(UserLoginHistoriesController).GetMethod(
            nameof(UserLoginHistoriesController.RecordLogin))!;

        AttributeOf<EnableRateLimitingAttribute>(login)!.PolicyName
            .Should().Be(UserWriteRateLimiting.PolicyName);
        BodyCapOf(login).Should().BeNull(
            "the endpoint never reads a request body; a size limit would be dead weight");
    }
}
