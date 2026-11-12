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
/// The caller's accumulated learning time, one row per server-local calendar day per
/// content category. POST /api/UserLearningTimes accepts the SPA's 60-second heartbeat
/// (fired only while an exercise page is visible and the user is active); it credits
/// the REAL seconds elapsed since the day's previous accepted beat — measured against
/// a SHARED per-user-per-day cursor (the MAX of the day's rows' LastBeatAt across all
/// categories), clamped to [0, 75] — so the first beat of the day bootstraps the
/// minute it watched and a replayed, early, multi-tab or per-category-looping beat
/// adds ~0. The client can never inflate the DAY'S TOTAL (the sum over its
/// categories), because every increment comes from one shared server-clock timeline;
/// per-category attribution is exactly what the sender chose to beat. There is
/// deliberately no PUT/DELETE: learning time must not be client-mutable.
/// Writes are rate-limited per user (<see cref="UserWriteRateLimiting"/>: 10/min,
/// honest cadence ~1/min) and body-capped, because every accepted beat takes
/// SQLite's global write lock.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserLearningTimesController : ControllerBase
{
    /// <summary>Seconds credited for the first beat of a user's day (bootstraps the
    /// minute the heartbeat loop had already been watching). Once per day — a new
    /// category's first row on an already-beaten day credits from the shared cursor
    /// instead, so the bootstrap cannot be collected up to six times a day.</summary>
    internal const int FirstBeatCreditSeconds = 60;

    /// <summary>
    /// Upper bound of a single beat's credit. Beats arrive every 60s; the slack covers
    /// a browser-throttled tick, while a longer gap (hidden tab, laptop closed) credits
    /// at most this — the idle minutes were never verified as learning.
    /// </summary>
    internal const int MaxCreditPerBeatSeconds = 75;

    /// <summary>Seeded LearningContentCategory ids are 1-6 (root CLAUDE.md category table).</summary>
    internal const int MinCategoryId = 1;
    internal const int MaxCategoryId = 6;

    /// <summary>Leaderboard guardrails: the window is clamped to a quarter year, the list to a top ten.</summary>
    internal const int LeaderboardDefaultWindowDays = 7;
    internal const int LeaderboardMaxWindowDays = 92;
    internal const int LeaderboardDefaultTop = 3;
    internal const int LeaderboardMaxTop = 10;
    /// <summary>Prefix length used to label a user who never sent a display name.</summary>
    internal const int AnonymousNamePrefixLength = 8;

    private readonly AppDbContext _dbContext;
    private readonly ILogger<UserLearningTimesController> _logger;

    public UserLearningTimesController(AppDbContext dbContext, ILogger<UserLearningTimesController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // Same extraction contract as UserLoginHistoriesController.
    private string GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }

    // Server-local today — the established "today" convention (see HabitPunchesController.Today / NFR-5).
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    /// How many seconds a beat credits. The day's FIRST beat (no row for the user/day
    /// yet) gets the minute its heartbeat loop has already been watching
    /// (FirstBeatCreditSeconds); every later beat — on any category — gets the
    /// server-measured time since the day's shared cursor (the latest accepted beat
    /// across all of the day's rows), floored at 0 (clock skew / out-of-order /
    /// multi-tab replay) and capped at MaxCreditPerBeatSeconds (resumed-after-idle gap).
    /// </summary>
    internal static int ComputeCredit(double elapsedSecondsSinceLastBeat, bool isNewRow)
    {
        if (isNewRow)
        {
            return FirstBeatCreditSeconds;
        }
        if (double.IsNaN(elapsedSecondsSinceLastBeat) || double.IsNegative(elapsedSecondsSinceLastBeat))
        {
            return 0;
        }
        return (int)Math.Min(Math.Round(elapsedSecondsSinceLastBeat), MaxCreditPerBeatSeconds);
    }

    private static LearningTimeDto Map(UserLearningTime row) => new()
    {
        LearnDate = row.LearnDate,
        CategoryId = row.CategoryId,
        LearningSeconds = row.LearningSeconds,
        LastBeatAt = row.LastBeatAt,
    };

    /// <summary>
    /// Trims the client-supplied display name, clamps it to the column width,
    /// and maps blank to null. Cosmetic only: it is stored against the
    /// caller's own rows, never anyone else's.
    /// </summary>
    internal static string? SanitizeUserName(string? raw)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }
        return name.Length <= 200 ? name : name[..200];
    }

    /// <summary>Stored name if any, else a short stable prefix of the user id.</summary>
    internal static string LeaderboardLabel(string userId, string? storedName) =>
        string.IsNullOrEmpty(storedName)
            ? userId[..Math.Min(AnonymousNamePrefixLength, userId.Length)]
            : storedName;

    // POST: api/UserLearningTimes — upsert today's row for the request's category.
    [HttpPost]
    [EnableRateLimiting(UserWriteRateLimiting.PolicyName)]
    [RequestSizeLimit(UserWriteRateLimiting.MaxRequestBodyBytes)]
    public async Task<ActionResult<LearningTimeDto>> RecordBeat(
        [FromBody] LearningTimeBeatRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized("User ID not found in token.");
        }

        if (request.CategoryId < MinCategoryId || request.CategoryId > MaxCategoryId)
        {
            return BadRequest($"CategoryId must be between {MinCategoryId} and {MaxCategoryId}.");
        }

        var day = Today;
        var now = DateTime.UtcNow;

        // Read-then-write inside one explicit transaction (Serializable → Microsoft.Data.
        // Sqlite issues BEGIN IMMEDIATE), same mechanism as UserLoginHistoriesController:
        // the write lock precedes the read, so concurrent beats for the same
        // (user, day) can neither both-insert (unique-index violation) nor interleave
        // into a lost increment.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        // The day's rows for this user — at most one per category (6), served by the
        // (UserId, LearnDate, CategoryId) unique index's prefix — do double duty in one
        // query: they yield the category row to upsert, and they define the SHARED
        // beat cursor (the latest accepted beat across ALL categories). Crediting off
        // the cursor instead of the category row's own LastBeatAt is what keeps the
        // DAY's TOTAL bounded by real elapsed time: a client beating several
        // categories in quick succession moves the cursor once and only earns the one
        // true gap, not one gap per category (review finding H1).
        var todaysRows = await _dbContext.UserLearningTimes
            .Where(t => t.UserId == userId && t.LearnDate == day)
            .ToListAsync(cancellationToken);

        var existing = todaysRows.FirstOrDefault(t => t.CategoryId == request.CategoryId);
        var beatUserName = SanitizeUserName(request.UserName);

        var credit = todaysRows.Count == 0
            ? ComputeCredit(0, isNewRow: true)
            : ComputeCredit((now - todaysRows.Max(t => t.LastBeatAt)).TotalSeconds, isNewRow: false);

        UserLearningTime row;
        var created = false;
        if (existing is null)
        {
            created = true;
            row = new UserLearningTime
            {
                UserId = userId,
                LearnDate = day,
                CategoryId = request.CategoryId,
                LearningSeconds = credit,
                LastBeatAt = now,
                UserName = beatUserName,
            };
            _dbContext.UserLearningTimes.Add(row);
        }
        else
        {
            row = existing;
            row.LearningSeconds += credit;
            row.LastBeatAt = now;
            // Last non-empty name wins; a beat without the field keeps what
            // earlier senders stored, so old clients never erase the label.
            if (beatUserName is not null)
            {
                row.UserName = beatUserName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (created)
        {
            _logger.LogInformation(
                "Learning-time row created for user {UserId} on {LearnDate}, category {CategoryId}.",
                userId, day, request.CategoryId);
        }
        return Ok(Map(row));
    }

    // GET: api/UserLearningTimes?from=2026-07-01&to=2026-10-05&categoryId=1 — own rows.
    [HttpGet]
    public async Task<ActionResult<List<LearningTimeDto>>> GetLearningTimes(
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int? categoryId, CancellationToken cancellationToken)
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

        if (categoryId.HasValue && (categoryId.Value < MinCategoryId || categoryId.Value > MaxCategoryId))
        {
            return BadRequest($"categoryId must be between {MinCategoryId} and {MaxCategoryId}.");
        }

        // Default window: the trailing 90 days ending today — a bounded response for the
        // user's own stats page; explicit from/to override it.
        var toDay = to ?? Today;
        var fromDay = from ?? toDay.AddDays(-89);

        var query = _dbContext.UserLearningTimes
            .Where(t => t.UserId == userId && t.LearnDate >= fromDay && t.LearnDate <= toDay);
        if (categoryId.HasValue)
        {
            query = query.Where(t => t.CategoryId == categoryId.Value);
        }

        var rows = await query
            .OrderByDescending(t => t.LearnDate)
            .ThenBy(t => t.CategoryId)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    // GET: api/UserLearningTimes/Preferences — the caller's leaderboard visibility
    // choice. No stored row simply means "visible"; nothing to backfill.
    [HttpGet("Preferences")]
    public async Task<ActionResult<LearningTimePreferencesDto>> GetPreferences(
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized("User ID not found in token.");
        }

        var row = await _dbContext.LeaderboardPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        return new LearningTimePreferencesDto
        {
            LeaderboardOptOut = row?.LeaderboardOptOut ?? false,
        };
    }

    // PUT: api/UserLearningTimes/Preferences — upsert the visibility choice.
    // One row per user, set from the token; the Serializable transaction mirrors
    // the beat handler so a first-time opt-out can't race itself into two rows
    // against the unique index. Shares the UserWrites per-user limiter and body
    // cap with the beat endpoint: it also takes the global write lock.
    [HttpPut("Preferences")]
    [EnableRateLimiting(UserWriteRateLimiting.PolicyName)]
    [RequestSizeLimit(UserWriteRateLimiting.MaxRequestBodyBytes)]
    public async Task<ActionResult<LearningTimePreferencesDto>> UpdatePreferences(
        [FromBody] LearningTimePreferencesDto preferences, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized("User ID not found in token.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        var row = await _dbContext.LeaderboardPreferences
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (row is null)
        {
            row = new LeaderboardPreference { UserId = userId };
            _dbContext.LeaderboardPreferences.Add(row);
        }
        row.LeaderboardOptOut = preferences.LeaderboardOptOut;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Leaderboard preference updated for user {UserId}: optOut={OptOut}.",
            userId, row.LeaderboardOptOut);
        return Ok(new LearningTimePreferencesDto { LeaderboardOptOut = row.LeaderboardOptOut });
    }

    // GET: api/UserLearningTimes/Leaderboard?window=7&top=3 — the top learners
    // over a rolling trailing window (server-local days, inclusive of today).
    // Deliberately NOT owner-scoped like the rest of this controller: it is the
    // workspace's first cross-user surface, visible to any signed-in user
    // ([Authorize] at the class) and exposing only { userId, label, seconds } —
    // no emails, no per-day breakdown, no categories. Users who opted out via
    // Preferences are excluded entirely (their own view of the board is
    // unaffected — the opt-out hides yourself, not the board).
    [HttpGet("Leaderboard")]
    public async Task<ActionResult<List<LearningTimeLeaderboardEntry>>> GetLeaderboard(
        [FromQuery] int? window, [FromQuery] int? top, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(window ?? LeaderboardDefaultWindowDays, 1, LeaderboardMaxWindowDays);
        var take = Math.Clamp(top ?? LeaderboardDefaultTop, 1, LeaderboardMaxTop);
        var cutoff = Today.AddDays(-(days - 1));

        var optedOut = _dbContext.LeaderboardPreferences
            .Where(p => p.LeaderboardOptOut)
            .Select(p => p.UserId);

        var ranked = await _dbContext.UserLearningTimes
            .Where(t => t.LearnDate >= cutoff && !optedOut.Contains(t.UserId))
            .GroupBy(t => t.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                TotalSeconds = g.Sum(t => t.LearningSeconds),
                // Any stored display name of the period; MAX keeps the choice
                // deterministic when a user's rows disagree.
                StoredName = g.Max(t => t.UserName),
            })
            .OrderByDescending(r => r.TotalSeconds)
            .ThenBy(r => r.UserId)
            .Take(take)
            .ToListAsync(cancellationToken);

        return ranked.Select(r => new LearningTimeLeaderboardEntry
        {
            UserId = r.UserId,
            UserName = LeaderboardLabel(r.UserId, r.StoredName),
            TotalSeconds = r.TotalSeconds,
        }).ToList();
    }
}
