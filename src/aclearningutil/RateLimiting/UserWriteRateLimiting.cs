using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace aclearningutil.RateLimiting;

/// <summary>
/// Per-user fixed-window limiter for the client-triggerable statistics writes
/// (<c>POST /api/UserLoginHistories</c>, <c>POST /api/UserLearningTimes</c>,
/// <c>PUT /api/UserLearningTimes/Preferences</c>). Every one of those handlers
/// takes SQLite's single global write lock (Serializable / BEGIN IMMEDIATE
/// transaction), so an authenticated user looping the beat endpoint would
/// otherwise queue every other writer in the app behind it (review finding
/// H2 in <c>docs/review-findings-user-stats-2026-11-08.md</c>). The built-in
/// <c>AddFixedWindowLimiter</c> policy is process-global — useless here, since
/// normal traffic alone is ~one beat per minute PER USER — so this policy
/// partitions by the caller's user id: each user gets their own window, and one
/// user hitting the ceiling never 429s another.
/// The legitimate client cadence (one heartbeat/minute per open tab, login POST
/// once per interactive sign-in, rare preference toggles) stays far below the
/// limit; it only caps abuse at the same order of magnitude as honest use.
/// </summary>
public static class UserWriteRateLimiting
{
    /// <summary>Policy name for <c>[EnableRateLimiting]</c> on the write actions.</summary>
    public const string PolicyName = "UserWrites";

    /// <summary>Accepted writes per user per window. Generous versus the honest
    /// ~1 beat/minute cadence (multi-tab + a burst after a throttled background
    /// tab still fit), yet 1/6th of the unbounded loop's write-lock pressure.</summary>
    public const int PermitLimitPerMinute = 10;

    /// <summary>Window after which a partition's counter resets.</summary>
    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Hard body cap for the two write endpoints that read a JSON body
    /// (heartbeat POST, Preferences PUT). Legitimate payloads are a few hundred
    /// bytes; without this, MVC would fully deserialize a Kestrel-sized body
    /// (~30 MB default) before the controller ever trims the name field —
    /// oversized requests now fail with <c>413</c>.
    /// </summary>
    public const long MaxRequestBodyBytes = 1024;

    /// <summary>
    /// Produces this request's partition. ASP.NET Core's default partitioner
    /// calls this per request but CREATES AND CACHES one fixed-window limiter
    /// per distinct key, so per-user counters survive across requests.
    /// </summary>
    public static RateLimitPartition<string> ForPartition(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitLimitPerMinute,
                Window = Window,
                QueueLimit = 0,
            });

    /// <summary>
    /// The partition key: <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/>
    /// with a <c>"sub"</c> fallback — the same user-extraction contract as the
    /// controllers — then the remote IP so (impossible through these
    /// <c>[Authorize]</c>d endpoints, but sound in principle) anonymous callers
    /// still land in per-connection buckets rather than one shared one.
    /// </summary>
    internal static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.FindFirstValue("sub")
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}
