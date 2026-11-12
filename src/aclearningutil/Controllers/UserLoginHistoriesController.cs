using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.RateLimiting;

namespace aclearningutil.Controllers;

/// <summary>
/// The caller's own login history, one row per server-local calendar day.
/// POST records a successful sign-in (the SPA calls it when the OIDC redirect completes);
/// it upserts today's row — first/last=now &amp; count=1 on the day's first login, else
/// LastLoginAt=now &amp; count++ — but only when the report is separated from the day's
/// last ACCEPTED sign-in by at least <see cref="LoginDedupWindowSeconds"/>: any closer
/// POST is a duplicate report (script replay, stuck-reload re-signin) and leaves the row
/// completely untouched, so a fast loop can neither mint <c>LoginCount</c> nor push
/// <c>LastLoginAt</c> (review finding H3; the H2 <c>UserWrites</c> limiter bounds even
/// the paced abuse). <c>LoginCount</c> therefore means "sign-ins at least 5 minutes
/// apart on that day" — a self-scoped cosmetic stat shown only on the caller's own
/// user-detail page. There is deliberately no PUT/DELETE: login truth must not be
/// client-mutable.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserLoginHistoriesController : ControllerBase
{
    /// <summary>
    /// Minimum separation, in seconds, between two POSTs that BOTH count as sign-ins.
    /// Human re-logins are minutes apart (token lifetimes, separate sessions); anything
    /// closer is a retry of the same sign-in event or a scripted call.
    /// </summary>
    internal const int LoginDedupWindowSeconds = 300;

    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserLoginHistoriesController> _logger;

    public UserLoginHistoriesController(AppDbContext dbContext, ILogger<UserLoginHistoriesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // Same extraction contract as the other user-scoped controllers.
    private string GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }

    // Server-local today — the established "today" convention (see HabitPunchesController.Today / NFR-5).
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// Whether a POST counts as a distinct sign-in: separated from the day's last
    /// accepted login by at least the dedup window. Negative elapsed (clock skew /
    /// a stored instant in the future) is indistinguishable from a replay and never
    /// counts.
    /// </summary>
    internal static bool IsDistinctLogin(DateTime now, DateTime lastAcceptedLoginAt) =>
        (now - lastAcceptedLoginAt).TotalSeconds >= LoginDedupWindowSeconds;

    private static UserLoginHistoryDto Map(UserLoginHistory row) => new()
    {
        LoginDate = row.LoginDate,
        FirstLoginAt = row.FirstLoginAt,
        LastLoginAt = row.LastLoginAt,
        LoginCount = row.LoginCount,
    };

    // POST: api/UserLoginHistories — upsert today's row. Always 200: the row's identity
    // is (user, day), not a new id, so there are no resource-creation semantics to signal.
    // Rate-limited with the per-user UserWrites policy (the upsert takes SQLite's global
    // write lock; the honest cadence is one call per interactive sign-in). No request
    // body, so no size cap applies here.
    [HttpPost]
    [EnableRateLimiting(UserWriteRateLimiting.PolicyName)]
    public async Task<ActionResult<UserLoginHistoryDto>> RecordLogin(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized("User ID not found in token.");
        }

        var day = Today;
        var now = DateTime.UtcNow;

        // The read-then-write below runs inside one explicit transaction, the same
        // mechanism as HabitPunchesController.Create: Serializable isolation makes
        // Microsoft.Data.Sqlite issue BEGIN IMMEDIATE, so the write lock is taken BEFORE
        // the read — two concurrent first-login POSTs for the same day can neither
        // both-insert (unique-index violation) nor lose an increment to LoginCount.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        var existing = await _dbContext.UserLoginHistories
            .FirstOrDefaultAsync(h => h.UserId == userId && h.LoginDate == day, cancellationToken);

        UserLoginHistory row;
        var created = false;
        if (existing is null)
        {
            created = true;
            row = new UserLoginHistory
            {
                UserId = userId,
                LoginDate = day,
                FirstLoginAt = now,
                LastLoginAt = now,
                LoginCount = 1,
            };
            _dbContext.UserLoginHistories.Add(row);
        }
        else
        {
            row = existing;
            // H3: within the dedup window a POST is a duplicate report, not a new
            // sign-in — the row stays byte-identical (the 200 echo below re-reads it),
            // so LoginCount cannot be minted and LastLoginAt cannot be walked forward
            // by a loop. Only a genuinely separated sign-in mutates the row.
            if (IsDistinctLogin(now, row.LastLoginAt))
            {
                row.LastLoginAt = now;
                row.LoginCount += 1;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (created)
        {
            _logger.LogInformation("Login history row created for user {UserId} on {LoginDate}.", userId, day);
        }
        return Ok(Map(row));
    }

    // GET: api/UserLoginHistories?from=2026-07-01&to=2026-10-05 — own rows, newest first.
    [HttpGet]
    public async Task<ActionResult<List<UserLoginHistoryDto>>> GetHistory(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized("User ID not found in token.");
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return BadRequest("from must be on or before to.");
        }

        // Default window: the trailing 90 days ending today — a bounded response for the
        // user's own "recent days" page; explicit from/to override it.
        var toDay = to ?? Today;
        var fromDay = from ?? toDay.AddDays(-89);

        var rows = await _dbContext.UserLoginHistories
            .Where(h => h.UserId == userId && h.LoginDate >= fromDay && h.LoginDate <= toDay)
            .OrderByDescending(h => h.LoginDate)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }
}
