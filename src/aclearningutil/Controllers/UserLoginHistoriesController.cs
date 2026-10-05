using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;

namespace aclearningutil.Controllers;

/// <summary>
/// The caller's own login history, one row per server-local calendar day.
/// POST records a successful sign-in (the SPA calls it when the OIDC redirect completes);
/// it upserts today's row — first/last=now &amp; count=1 on the day's first login, else
/// LastLoginAt=now &amp; count++ — so repeat calls are idempotent for the row and
/// cumulative for the count. There is deliberately no PUT/DELETE: login truth must not
/// be client-mutable.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserLoginHistoriesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserLoginHistoriesController> _logger;

    public UserLoginHistoriesController(AppDbContext dbContext, ILogger<UserLoginHistoriesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // Same extraction contract as UserLearningHistoriesController.
    private string GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }

    // Server-local today — the established "today" convention (see HabitPunchesController.Today / NFR-5).
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static UserLoginHistoryDto Map(UserLoginHistory row) => new()
    {
        LoginDate = row.LoginDate,
        FirstLoginAt = row.FirstLoginAt,
        LastLoginAt = row.LastLoginAt,
        LoginCount = row.LoginCount,
    };

    // POST: api/UserLoginHistories — upsert today's row. Always 200: the row's identity
    // is (user, day), not a new id, so there are no resource-creation semantics to signal.
    [HttpPost]
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
            row.LastLoginAt = now;
            row.LoginCount += 1;
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
