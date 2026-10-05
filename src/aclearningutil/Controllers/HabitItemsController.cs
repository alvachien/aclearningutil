using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Items within a habit. Adding items is always permitted; removing an item is blocked
/// while punch records reference it (FR-2.3). See docs/design-habit-api.md § Items.
/// </summary>
[Route("api/Habits/{habitId:int}/Items")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class HabitItemsController : HabitControllerBase
{
    private readonly HabitEvaluationService _evaluation;

    public HabitItemsController(AppDbContext dbContext, HabitEvaluationService evaluation)
        : base(dbContext)
    {
        _evaluation = evaluation;
    }

    private static ItemOutDto ToItemOut(
        HabitItem item, Dictionary<int, (double CurrentCycleValue, double? TodayValue)> aggregates, bool hasPunches)
    {
        return new ItemOutDto
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
    }

    private async Task<(Habit Habit, List<HabitItem> Items)> LoadItemsAsync(
        int habitId, string userId, CancellationToken ct)
    {
        var habit = await LoadOwnedHabitAsync(habitId, userId, ct);
        var items = await DbContext.HabitItems
            .Where(i => i.HabitId == habitId && i.OwnerId == userId)
            .Include(i => i.Properties)
            .OrderBy(i => i.Order).ThenBy(i => i.Id)
            .ToListAsync(ct);
        return (habit, items);
    }

    // GET: api/Habits/{habitId}/Items
    [HttpGet]
    public async Task<ActionResult<List<ItemOutDto>>> GetAll(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var (habit, items) = await LoadItemsAsync(habitId, userId, cancellationToken);
        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);

        // One batched query for every item's punch existence (the per-item AnyAsync was an N+1).
        var itemIds = items.Select(i => i.Id).ToList();
        var punchedItemIds = (await DbContext.Punches
            .Where(p => itemIds.Contains(p.ItemId))
            .Select(p => p.ItemId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        return items
            .Select(item => ToItemOut(item, aggregates, punchedItemIds.Contains(item.Id)))
            .ToList();
    }

    // GET: api/Habits/{habitId}/Items/{itemId}
    [HttpGet("{itemId:int}")]
    public async Task<ActionResult<ItemOutDto>> GetById(int habitId, int itemId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var item = await DbContext.HabitItems
            .Where(i => i.Id == itemId && i.HabitId == habitId && i.OwnerId == userId)
            .Include(i => i.Properties)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw HabitException.NotFound("Item");

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return ToItemOut(item, aggregates, await DbContext.Punches.AnyAsync(p => p.ItemId == itemId, cancellationToken));
    }

    // POST: api/Habits/{habitId}/Items
    [HttpPost]
    public async Task<ActionResult<ItemOutDto>> Create(int habitId, [FromBody] ItemCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        HabitRuleValidator.ValidateItemShape(dto);

        if (await DbContext.HabitItems.AnyAsync(i => i.HabitId == habitId && i.Name == dto.Name, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Item name '{dto.Name}' already exists in this habit.");
        }

        // Habit-wide same-name ⇒ same-type rule: the new item's properties must agree with
        // every property row already in the habit (spec).
        var existingPairs = (await DbContext.ItemProperties
                .Where(p => p.HabitId == habitId)
                .Select(p => new { p.Name, p.PropertyType })
                .ToListAsync(cancellationToken))
            .Select(p => (p.Name, p.PropertyType))
            .ToList();
        foreach (var p in dto.Properties)
        {
            // ValidateItemShape already rejected an omitted propertyType (missingPropertyType);
            // resolving again keeps the nullable wire field out of the non-nullable checks.
            var propertyType = HabitRuleValidator.RequirePropertyType(p.PropertyType, p.Name);
            HabitRuleValidator.ValidatePropertyTypeConsistency(existingPairs, p.Name, propertyType);
            existingPairs.Add((p.Name, propertyType));
        }

        var item = new HabitItem
        {
            HabitId = habitId,
            OwnerId = userId,
            Name = dto.Name,
            Order = dto.Order,
            CreatedAt = DateTime.UtcNow,
        };
        foreach (var p in dto.Properties)
        {
            item.Properties.Add(new ItemProperty
            {
                Item = item,
                HabitId = habitId,
                OwnerId = userId,
                Name = p.Name,
                PropertyType = HabitRuleValidator.RequirePropertyType(p.PropertyType, p.Name),
                BaseRate = p.BaseRate,
                ItemUniqueness = p.ItemUniqueness,
                Order = p.Order,
                CreatedAt = DateTime.UtcNow,
            });
        }
        DbContext.HabitItems.Add(item);
        await DbContext.SaveChangesAsync(cancellationToken);

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return CreatedAtAction(nameof(GetById), new { habitId, itemId = item.Id },
            ToItemOut(item, aggregates, hasPunches: false));
    }

    // PUT: api/Habits/{habitId}/Items/{itemId}
    [HttpPut("{itemId:int}")]
    public async Task<ActionResult<ItemOutDto>> Update(
        int habitId, int itemId, [FromBody] ItemUpdateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var item = await DbContext.HabitItems
            .Where(i => i.Id == itemId && i.HabitId == habitId && i.OwnerId == userId)
            .Include(i => i.Properties)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw HabitException.NotFound("Item");

        HabitRuleValidator.RequireName(dto.Name, "Item");
        if (await DbContext.HabitItems.AnyAsync(
                i => i.HabitId == habitId && i.Name == dto.Name && i.Id != itemId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Item name '{dto.Name}' already exists in this habit.");
        }

        item.Name = dto.Name;
        item.Order = dto.Order;
        await DbContext.SaveChangesAsync(cancellationToken);

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return ToItemOut(item, aggregates, await DbContext.Punches.AnyAsync(p => p.ItemId == itemId, cancellationToken));
    }

    // DELETE: api/Habits/{habitId}/Items/{itemId}
    [HttpDelete("{itemId:int}")]
    public async Task<IActionResult> Delete(int habitId, int itemId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var item = await DbContext.HabitItems
            .Where(i => i.Id == itemId && i.HabitId == habitId && i.OwnerId == userId)
            .Include(i => i.Properties)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw HabitException.NotFound("Item");

        if (await DbContext.Punches.AnyAsync(p => p.ItemId == itemId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ItemHasPunches,
                "Item cannot be deleted while punch records reference it.");
        }

        // Criteria bind properties by NAME, so deleting this item's property rows never
        // blocks removal: the scope shrinks and a criterion whose last in-scope definition
        // disappears simply stops passing (spec FR-2.3).

        // Subset-scope membership rows for this item shrink away with it (by design).
        var scopeRows = await DbContext.CriterionConditionScopeItems
            .Where(s => s.ItemId == itemId)
            .ToListAsync(cancellationToken);
        DbContext.CriterionConditionScopeItems.RemoveRange(scopeRows);
        DbContext.HabitItems.Remove(item);

        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
