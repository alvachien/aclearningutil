using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Shared plumbing for habit-tracking controllers: user id extraction
/// (ClaimTypes.NameIdentifier with a "sub" fallback, per the project's auth contract)
/// and tenant-scoped habit loading. A missing habit OR a habit owned by another user
/// both yield notFound — cross-user existence is never revealed (FR-6).
/// </summary>
[ApiController]
public abstract class HabitControllerBase : ControllerBase
{
    protected AppDbContext DbContext { get; }

    protected HabitControllerBase(AppDbContext dbContext)
    {
        DbContext = dbContext;
    }

    protected string GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }

    /// <summary>
    /// The caller's display name as issued by the ID provider (acidserver's ProfileService
    /// maps the "name" claim to AspNetUsers.UserName). Server-side source for owner-name
    /// snapshots — never taken from a request body.
    /// </summary>
    protected string GetUserName() => User.FindFirstValue("name") ?? string.Empty;

    /// <summary>Loads the habit scoped to the caller, or throws notFound.</summary>
    protected async Task<Habit> LoadOwnedHabitAsync(int habitId, string ownerId, CancellationToken ct)
    {
        var habit = await DbContext.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.OwnerId == ownerId, ct);
        return habit ?? throw HabitException.NotFound("Habit");
    }

    /// <summary>
    /// Loads a habit the caller may READ: either their own or one they were explicitly
    /// invited to (FR-6 exception limited to named invitees). Callers without a grant and
    /// missing habits both throw notFound — private habits are never confirmed to them.
    /// </summary>
    protected async Task<Habit> LoadSharedOrOwnedHabitAsync(int habitId, string userId, CancellationToken ct)
    {
        var habit = await DbContext.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId, ct)
            ?? throw HabitException.NotFound("Habit");

        if (habit.OwnerId == userId)
        {
            return habit;
        }

        if (await DbContext.HabitShareGrants.AnyAsync(
                g => g.HabitId == habitId && g.GranteeUserId == userId, ct))
        {
            return habit;
        }

        throw HabitException.NotFound("Habit");
    }
}
