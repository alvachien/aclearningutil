using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// READ-ONLY "shared with me" surface (FR-6 exception limited to named invitees): a
/// user sees the definition, items/criteria and full punch history of habits their
/// owners explicitly invited them to — nothing else. Gating: <c>HabitShareGrant</c> rows
/// matched against the caller's id (the owner may use these endpoints on their own
/// habits too). Owners never appear by identity claim, only by the name snapshot taken
/// server-side at invite time; non-invited ids and unknown ids both 404. Write paths
/// stay exclusively on the owner-gated controllers. See docs/design-habit-api.md.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class SharedHabitsController : HabitControllerBase
{
    private readonly HabitEvaluationService _evaluation;

    public SharedHabitsController(AppDbContext dbContext, HabitEvaluationService evaluation)
        : base(dbContext)
    {
        _evaluation = evaluation;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // GET: api/SharedHabits — every habit the CALLER was invited to, with owner names.
    [HttpGet]
    public async Task<ActionResult<List<SharedHabitOutDto>>> GetAll(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var grants = await DbContext.HabitShareGrants
            .Where(g => g.GranteeUserId == userId)
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);
        if (grants.Count == 0)
        {
            return new List<SharedHabitOutDto>();
        }

        var habitIds = grants.Select(g => g.HabitId).ToList();
        var habits = await DbContext.Habits
            .Where(h => habitIds.Contains(h.Id))
            .OrderBy(h => h.Id)
            .ToListAsync(cancellationToken);

        // Batched hasPunches flag mirrors the owner list (avoids per-habit AnyAsync).
        var punchHabitIds = (await DbContext.Punches
            .Where(p => habitIds.Contains(p.HabitId))
            .Select(p => p.HabitId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var ownerNameByHabit = grants
            .GroupBy(g => g.HabitId)
            .ToDictionary(group => group.Key, group => group.First().OwnerName);

        var result = new List<SharedHabitOutDto>(habits.Count);
        foreach (var habit in habits)
        {
            result.Add(new SharedHabitOutDto
            {
                OwnerName = ownerNameByHabit.TryGetValue(habit.Id, out var name) ? name : "unknown",
                Habit = await ToHabitOutAsync(habit, punchHabitIds.Contains(habit.Id), cancellationToken),
            });
        }

        return result;
    }

    // GET: api/SharedHabits/{habitId} — full read-only definition (habit + items + criteria).
    [HttpGet("{habitId:int}")]
    public async Task<ActionResult<SharedHabitDetailOutDto>> GetById(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadSharedOrOwnedHabitAsync(habitId, userId, cancellationToken);

        var items = await DbContext.HabitItems
            .Where(i => i.HabitId == habitId)
            .Include(i => i.Properties)
            .OrderBy(i => i.Order).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, Today, cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();
        var punchedItemIds = (await DbContext.Punches
            .Where(p => itemIds.Contains(p.ItemId))
            .Select(p => p.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var criteria = await DbContext.Criteria
            .Where(c => c.HabitId == habitId)
            .Include(c => c.Condition).ThenInclude(l => l!.ScopeItems)
            .Include(c => c.Composite).ThenInclude(co => co!.Operands)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);

        // Owner credit: the caller's own grant snapshot; an owner self-viewing their habit
        // gets their own token name (same claim used when invites were created).
        var callerGrant = await DbContext.HabitShareGrants
            .FirstOrDefaultAsync(g => g.HabitId == habitId && g.GranteeUserId == userId, cancellationToken);
        var ownerName = callerGrant?.OwnerName ?? GetUserName();

        return new SharedHabitDetailOutDto
        {
            OwnerName = string.IsNullOrWhiteSpace(ownerName) ? "unknown" : ownerName,
            Habit = await ToHabitOutAsync(habit, await HasPunchesAsync(habitId, cancellationToken), cancellationToken),
            Items = items
                .Select(item => ToItemOut(item, aggregates, punchedItemIds.Contains(item.Id)))
                .ToList(),
            Criteria = criteria.Select(ToCriterionOut).ToList(),
        };
    }

    // GET: api/SharedHabits/{habitId}/History?from=&to= — identical granularity to the
    // owner endpoint (full DayHistoryOut incl. every punch's values), invitee-gated.
    [HttpGet("{habitId:int}/History")]
    public async Task<ActionResult<List<DayHistoryOutDto>>> GetHistory(
        int habitId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidDateRange, "from must be on or before to.");
        }

        var habit = await LoadSharedOrOwnedHabitAsync(habitId, userId, cancellationToken);
        return await _evaluation.BuildHistoryAsync(habit, from, to, Today, cancellationToken);
    }

    // ── Mappers (private copies — mirror the owner-gated controllers without refactoring
    // their write paths; Keep in sync when ItemOutDto/CriterionOutDto shapes change) ──

    private async Task<HabitOutDto> ToHabitOutAsync(Habit habit, bool hasPunches, CancellationToken ct)
    {
        var progress = await _evaluation.BuildProgressAsync(habit, Today, ct);
        return new HabitOutDto
        {
            Id = habit.Id,
            Name = habit.Name,
            Description = habit.Description,
            Cycle = habit.Cycle,
            StartDate = habit.StartDate,
            EndDate = habit.EndDate,
            State = habit.State,
            DeactivatedDate = habit.DeactivatedDate,
            HasPunches = hasPunches,
            CreatedAt = habit.CreatedAt,
            Progress = progress,
        };
    }

    private async Task<bool> HasPunchesAsync(int habitId, CancellationToken ct) =>
        await DbContext.Punches.AnyAsync(p => p.HabitId == habitId, ct);

    private static ItemOutDto ToItemOut(
        HabitItem item, Dictionary<int, (double CurrentCycleValue, double? TodayValue)> aggregates, bool hasPunches) => new()
    {
        Id = item.Id,
        HabitId = item.HabitId,
        Name = item.Name,
        Order = item.Order,
        CreatedAt = item.CreatedAt,
        HasPunches = hasPunches,
        Properties = item.Properties
            .OrderBy(p => p.Order).ThenBy(p => p.Id)
            .Select(p => new PropertyOutDto
            {
                Id = p.Id,
                ItemId = p.ItemId,
                Name = p.Name,
                PropertyType = p.PropertyType,
                BaseRate = p.BaseRate,
                ItemUniqueness = p.ItemUniqueness,
                Order = p.Order,
                CreatedAt = p.CreatedAt,
                CurrentCycleValue = aggregates.TryGetValue(p.Id, out var agg) ? agg.CurrentCycleValue : 0,
                TodayValue = aggregates.TryGetValue(p.Id, out var agg2) ? agg2.TodayValue : null,
            }).ToList(),
    };

    private static CriterionOutDto ToCriterionOut(Criterion c) => new()
    {
        Id = c.Id,
        HabitId = c.HabitId,
        Name = c.Name,
        IsRoot = c.IsRoot,
        CriterionType = c.CriterionType,
        PropertyName = c.Condition?.PropertyName,
        AggregationMode = c.Condition?.AggregationMode,
        ItemScope = c.Condition?.ItemScope,
        ScopeItemIds = c.Condition is { ItemScope: ItemScope.Subset }
            ? c.Condition.ScopeItems.Select(s => s.ItemId).ToList()
            : null,
        Threshold = c.Condition?.Threshold,
        Operator = c.Composite?.Operator,
        OperandCriterionIds = c.Composite?.Operands.OrderBy(o => o.Order).Select(o => o.OperandCriterionId).ToList(),
        SuccessType = c.SuccessType,
        CycleTarget = c.CycleTarget,
        CreatedAt = c.CreatedAt,
    };
}
