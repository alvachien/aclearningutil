using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Property definitions on an item. PropertyType and ItemUniqueness are immutable after
/// creation; deletion is blocked while punch values reference the property or a condition
/// criterion anchors on it. See docs/design-habit-api.md § Item Properties.
/// </summary>
[Route("api/Habits/{habitId:int}/Items/{itemId:int}/Properties")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class ItemPropertiesController : HabitControllerBase
{
    private readonly HabitEvaluationService _evaluation;

    public ItemPropertiesController(AppDbContext dbContext, HabitEvaluationService evaluation)
        : base(dbContext)
    {
        _evaluation = evaluation;
    }

    private async Task<(Habit Habit, HabitItem Item)> LoadOwnedItemAsync(
        int habitId, int itemId, string userId, CancellationToken ct)
    {
        var habit = await LoadOwnedHabitAsync(habitId, userId, ct);
        var item = await DbContext.HabitItems
            .Where(i => i.Id == itemId && i.HabitId == habitId && i.OwnerId == userId)
            .Include(i => i.Properties)
            .FirstOrDefaultAsync(ct)
            ?? throw HabitException.NotFound("Item");
        return (habit, item);
    }

    private static PropertyOutDto ToDto(ItemProperty p, Dictionary<int, (double CurrentCycleValue, double? TodayValue)> aggregates) =>
        new()
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
        };

    // POST: api/Habits/{habitId}/Items/{itemId}/Properties
    [HttpPost]
    public async Task<ActionResult<PropertyOutDto>> Create(
        int habitId, int itemId, [FromBody] PropertyCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var (habit, item) = await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);
        // An omitted propertyType is 422 missingPropertyType — never a silent boolean = 0
        // (the wire field is nullable precisely for this; see Models/HabitRequests.cs).
        var propertyType = HabitRuleValidator.RequirePropertyType(dto.PropertyType, dto.Name);
        HabitRuleValidator.ValidatePropertyDefinition(dto.Name, propertyType, dto.BaseRate, dto.ItemUniqueness);

        if (item.Properties.Any(p => p.Name == dto.Name))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Property name '{dto.Name}' already exists on this item.");
        }

        // Habit-wide same-name ⇒ same-type rule (spec).
        var existingPairs = (await DbContext.ItemProperties
                .Where(p => p.HabitId == habitId)
                .Select(p => new { p.Name, p.PropertyType })
                .ToListAsync(cancellationToken))
            .Select(p => (p.Name, p.PropertyType))
            .ToList();
        HabitRuleValidator.ValidatePropertyTypeConsistency(existingPairs, dto.Name, propertyType);

        var property = new ItemProperty
        {
            ItemId = itemId,
            HabitId = habitId,
            OwnerId = userId,
            Name = dto.Name,
            PropertyType = propertyType,
            BaseRate = dto.BaseRate,
            ItemUniqueness = dto.ItemUniqueness,
            Order = dto.Order,
            CreatedAt = DateTime.UtcNow,
        };
        DbContext.ItemProperties.Add(property);
        await DbContext.SaveChangesAsync(cancellationToken);

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return CreatedAtAction(nameof(GetById), new { habitId, itemId, propertyId = property.Id },
            ToDto(property, aggregates));
    }

    // GET: api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}
    [HttpGet("{propertyId:int}")]
    public async Task<ActionResult<PropertyOutDto>> GetById(
        int habitId, int itemId, int propertyId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var (habit, item) = await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);
        var property = item.Properties.FirstOrDefault(p => p.Id == propertyId)
            ?? throw HabitException.NotFound("Property");

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return ToDto(property, aggregates);
    }

    // PUT: api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}
    // PropertyType and ItemUniqueness cannot change — absent from the update payload.
    [HttpPut("{propertyId:int}")]
    public async Task<ActionResult<PropertyOutDto>> Update(
        int habitId, int itemId, int propertyId, [FromBody] PropertyUpdateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var (habit, item) = await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);
        var property = item.Properties.FirstOrDefault(p => p.Id == propertyId)
            ?? throw HabitException.NotFound("Property");

        HabitRuleValidator.RequireName(dto.Name, "Property");
        if (item.Properties.Any(p => p.Name == dto.Name && p.Id != propertyId))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Property name '{dto.Name}' already exists on this item.");
        }

        // Renaming must keep the habit-wide same-name ⇒ same-type rule intact.
        if (dto.Name != property.Name)
        {
            var otherPairs = (await DbContext.ItemProperties
                    .Where(p => p.HabitId == habitId && p.Id != propertyId)
                    .Select(p => new { p.Name, p.PropertyType })
                    .ToListAsync(cancellationToken))
                .Select(p => (p.Name, p.PropertyType))
                .ToList();
            HabitRuleValidator.ValidatePropertyTypeConsistency(otherPairs, dto.Name, property.PropertyType);
        }

        if (dto.BaseRate.HasValue)
        {
            if (property.PropertyType != PropertyType.Numeric)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidBaseRate,
                    "baseRate only applies to numeric properties.");
            }
            if (!double.IsFinite(dto.BaseRate.Value) || dto.BaseRate.Value <= 0)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidBaseRate,
                    "baseRate must be a positive finite number.");
            }
        }

        property.Name = dto.Name;
        property.BaseRate = property.PropertyType == PropertyType.Numeric ? dto.BaseRate : null;
        property.Order = dto.Order;
        await DbContext.SaveChangesAsync(cancellationToken);

        var aggregates = await _evaluation.ComputePropertyAggregatesAsync(habit, DateOnly.FromDateTime(DateTime.Now), cancellationToken);
        return ToDto(property, aggregates);
    }

    // DELETE: api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}
    [HttpDelete("{propertyId:int}")]
    public async Task<IActionResult> Delete(
        int habitId, int itemId, int propertyId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);
        var property = await DbContext.ItemProperties
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.ItemId == itemId && p.HabitId == habitId && p.OwnerId == userId, cancellationToken)
            ?? throw HabitException.NotFound("Property");

        if (await DbContext.PunchValues.AnyAsync(v => v.PropertyId == propertyId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ItemHasPunches,
                "Property cannot be deleted while punch values reference it.");
        }

        // A criterion bound to this property's NAME is NOT a blocker (spec FR-2.3): the
        // binding survives; if this was the last in-scope definition of the name the
        // criterion simply stops passing.

        DbContext.ItemProperties.Remove(property);
        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
