using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Success criteria of a habit. Leaves bind the property by NAME in every phase;
/// scope items and composite operands reference assigned ids here (names during the
/// name-based HabitCreate payload). Root transitions are atomic; deletion of the root
/// or of an in-use operand is blocked. See docs/design-habit-api.md § Criteria.
/// </summary>
[Route("api/Habits/{habitId:int}/Criteria")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class HabitCriteriaController : HabitControllerBase
{
    public HabitCriteriaController(AppDbContext dbContext) : base(dbContext)
    {
    }

    private static CriterionOutDto ToDto(Criterion c) => new()
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

    private async Task<Criterion> LoadOwnedCriterionAsync(int habitId, int criterionId, string userId, CancellationToken ct)
    {
        await LoadOwnedHabitAsync(habitId, userId, ct);
        return await DbContext.Criteria
            .Where(c => c.Id == criterionId && c.HabitId == habitId && c.OwnerId == userId)
            .Include(c => c.Condition).ThenInclude(l => l!.ScopeItems)
            .Include(c => c.Composite).ThenInclude(co => co!.Operands)
            .FirstOrDefaultAsync(ct)
            ?? throw HabitException.NotFound("Criterion");
    }

    // GET: api/Habits/{habitId}/Criteria
    [HttpGet]
    public async Task<ActionResult<List<CriterionOutDto>>> GetAll(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var criteria = await DbContext.Criteria
            .Where(c => c.HabitId == habitId && c.OwnerId == userId)
            .Include(c => c.Condition).ThenInclude(l => l!.ScopeItems)
            .Include(c => c.Composite).ThenInclude(co => co!.Operands)
            .OrderBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return criteria.Select(ToDto).ToList();
    }

    // GET: api/Habits/{habitId}/Criteria/{criterionId}
    [HttpGet("{criterionId:int}")]
    public async Task<ActionResult<CriterionOutDto>> GetById(int habitId, int criterionId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        return ToDto(await LoadOwnedCriterionAsync(habitId, criterionId, userId, cancellationToken));
    }

    // POST: api/Habits/{habitId}/Criteria — standalone add (id-based references).
    [HttpPost]
    public async Task<ActionResult<CriterionOutDto>> Create(
        int habitId, [FromBody] CriterionCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        HabitRuleValidator.RequireName(dto.Name, "Criterion");

        // An omitted criterionType is 422 missingCriterionType — never a silent condition = 0
        // (the wire field is nullable precisely for this; see Models/HabitRequests.cs).
        var criterionType = HabitRuleValidator.RequireCriterionType(dto.CriterionType, dto.Name);
        if (!Enum.IsDefined(criterionType))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Criterion '{dto.Name}': unknown criterionType '{(int)criterionType}'.");
        }

        // Post-creation phase references scope items/operands by id; name-based refs belong
        // to HabitCreate payloads only. (Leaves bind the property by name in EVERY phase.)
        if (dto.ScopeItemNames is not null || dto.OperandCriterionNames is not null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                "Standalone criterion creation must use id-based references (scopeItemIds/operandCriterionIds).");
        }

        if (await DbContext.Criteria.AnyAsync(c => c.HabitId == habitId && c.Name == dto.Name, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Criterion name '{dto.Name}' already exists in this habit.");
        }

        var existingIds = await DbContext.Criteria
            .Where(c => c.HabitId == habitId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var existingProperties = (await DbContext.ItemProperties
                .Where(p => p.HabitId == habitId)
                .Select(p => new { p.Name, p.PropertyType })
                .ToListAsync(cancellationToken))
            .Select(p => (p.Name, p.PropertyType))
            .ToList();
        var existingItemIds = await DbContext.HabitItems
            .Where(i => i.HabitId == habitId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        if (criterionType == CriterionType.Condition)
        {
            IReadOnlyCollection<string>? scopedNames = null;
            if (dto.ItemScope == ItemScope.Subset && dto.ScopeItemIds is { Count: > 0 })
            {
                scopedNames = await DbContext.ItemProperties
                    .Where(p => p.HabitId == habitId && dto.ScopeItemIds.Contains(p.ItemId))
                    .Select(p => p.Name)
                    .ToListAsync(cancellationToken);
            }
            ValidateStandaloneCondition(dto, existingProperties, existingItemIds, scopedNames);
        }
        else
        {
            ValidateStandaloneComposite(dto, existingIds);
        }
        HabitRuleValidator.ValidateTargets(dto, habit.Cycle);

        var criterion = new Criterion
        {
            HabitId = habitId,
            OwnerId = userId,
            Name = dto.Name,
            CriterionType = criterionType,
            IsRoot = dto.IsRoot,
            SuccessType = dto.IsRoot ? dto.SuccessType : null,
            CycleTarget = dto.IsRoot && dto.SuccessType == SuccessType.Daily ? dto.CycleTarget : null,
            CreatedAt = DateTime.UtcNow,
        };

        if (criterion.CriterionType == CriterionType.Condition)
        {
            criterion.Condition = new CriterionCondition
            {
                Criterion = criterion,
                PropertyName = dto.PropertyName!,
                AggregationMode = dto.AggregationMode,
                ItemScope = dto.ItemScope ?? ItemScope.All,
                Threshold = dto.Threshold!.Value,
            };
            if (criterion.Condition.ItemScope == ItemScope.Subset)
            {
                foreach (var itemId in dto.ScopeItemIds!)
                {
                    criterion.Condition.ScopeItems.Add(new CriterionConditionScopeItem
                    {
                        CriterionCondition = criterion.Condition,
                        ItemId = itemId,
                    });
                }
            }
        }
        else
        {
            criterion.Composite = new CriterionComposite
            {
                Criterion = criterion,
                Operator = dto.Operator!.Value,
            };
            var order = 0;
            foreach (var operandId in dto.OperandCriterionIds!)
            {
                criterion.Composite.Operands.Add(new CriterionCompositeOperand
                {
                    Composite = criterion.Composite,
                    OperandCriterionId = operandId,
                    Order = order++,
                });
            }
        }

        DbContext.Criteria.Add(criterion);

        // Root transition: clearing the previous root happens in the same SaveChanges.
        // The demoted row must lose its targets too — targets are only valid on the root
        // (see HabitRuleValidator.ValidateTargets).
        if (dto.IsRoot)
        {
            var previousRoot = await DbContext.Criteria
                .FirstOrDefaultAsync(c => c.HabitId == habitId && c.IsRoot && c.Id != criterion.Id, cancellationToken);
            if (previousRoot != null)
            {
                previousRoot.IsRoot = false;
                previousRoot.SuccessType = null;
                previousRoot.CycleTarget = null;
            }
        }

        await DbContext.SaveChangesAsync(cancellationToken);

        // A standalone add has no incoming edges yet (its id is new), so cycles cannot be
        // introduced; the operand-existence checks above are sufficient.
        return CreatedAtAction(nameof(GetById), new { habitId, criterionId = criterion.Id }, ToDto(criterion));
    }

    private static void ValidateStandaloneCondition(
        CriterionCreateDto dto,
        List<(string Name, PropertyType Type)> habitProperties,
        List<int> itemIds,
        IReadOnlyCollection<string>? scopedPropertyNames = null)
    {
        if (string.IsNullOrWhiteSpace(dto.PropertyName))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                "Condition criterion requires propertyName (conditions bind the property by name).");
        }
        if (dto.PropertyName.Length > HabitRuleValidator.MaxNameLength)
        {
            // Same wire bound as the HabitCreate payload path (stored column max length 500).
            throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                $"Criterion '{dto.Name}': propertyName must be at most {HabitRuleValidator.MaxNameLength} characters (got {dto.PropertyName.Length}).");
        }
        var target = habitProperties.FirstOrDefault(p => p.Name == dto.PropertyName);
        if (target.Name is null)
        {
            throw HabitException.NotFound("Property");
        }
        HabitRuleValidator.ValidateAggregationMode(dto.PropertyName, target.Type, dto.AggregationMode);
        // !IsFinite first: NaN/Infinity pass neither `<= 0` nor the integral test below.
        if (dto.Threshold is null || !double.IsFinite(dto.Threshold.Value) || dto.Threshold.Value <= 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidThreshold,
                "Condition criterion requires a threshold greater than zero.");
        }
        if (target.Type == PropertyType.Boolean && Math.Floor(dto.Threshold.Value) != dto.Threshold.Value)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidThreshold,
                "Boolean criterion requires an integer item-count threshold.");
        }
        if (dto.ItemScope == ItemScope.Subset)
        {
            if (dto.ScopeItemIds is null || dto.ScopeItemIds.Count == 0)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.ScopeItemsRequired, "Subset scope requires scopeItemIds.");
            }
            // Duplicate ids would violate the scope-items PK at SaveChanges → 500; reject as 422.
            HabitRuleValidator.RejectDuplicateReferences(dto.ScopeItemIds, "scopeItemIds", dto.Name);
            HabitRuleValidator.ValidateReferenceCount(dto.ScopeItemIds, "scopeItemIds", dto.Name);
            if (dto.ScopeItemIds.Any(id => !itemIds.Contains(id)))
            {
                throw HabitException.NotFound("Item");
            }
            // The bound name must exist on at least one in-scope item (spec).
            if (scopedPropertyNames is not null && !scopedPropertyNames.Contains(dto.PropertyName, StringComparer.Ordinal))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                    $"Property '{dto.PropertyName}' is not defined on any item in the subset scope of criterion '{dto.Name}'.");
            }
        }
    }

    private static void ValidateStandaloneComposite(CriterionCreateDto dto, List<int> criterionIds)
    {
        if (dto.Operator is null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                "Composite criterion requires an operator.");
        }
        var operands = dto.OperandCriterionIds;
        if (operands is null || operands.Count == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidOperandCount,
                "Composite criterion requires operands.");
        }
        HabitRuleValidator.ValidateOperandCount(dto.Operator.Value, operands.Count, dto.Name);
        // Duplicate operand ids would violate the operands PK (CompositeId, OperandCriterionId)
        // at SaveChanges → 500; reject as 422 before insert. Checked after the count rule so a
        // NOT with two (equal) refs still reports invalidOperandCount as the shape error.
        HabitRuleValidator.RejectDuplicateReferences(operands, "operandCriterionIds", dto.Name);
        HabitRuleValidator.ValidateReferenceCount(operands, "operandCriterionIds", dto.Name);
        if (operands.Any(id => !criterionIds.Contains(id)))
        {
            throw HabitException.NotFound("Operand criterion");
        }
    }

    // PUT: api/Habits/{habitId}/Criteria/{criterionId}
    [HttpPut("{criterionId:int}")]
    public async Task<ActionResult<CriterionOutDto>> Update(
        int habitId, int criterionId, [FromBody] CriterionUpdateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var criterion = await LoadOwnedCriterionAsync(habitId, criterionId, userId, cancellationToken);

        HabitRuleValidator.RequireName(dto.Name, "Criterion");
        if (await DbContext.Criteria.AnyAsync(
                c => c.HabitId == habitId && c.Name == dto.Name && c.Id != criterionId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"Criterion name '{dto.Name}' already exists in this habit.");
        }

        if (!dto.IsRoot && criterion.IsRoot)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.RootRequired,
                "Cannot remove root status without designating another root.");
        }

        var createShape = new CriterionCreateDto
        {
            Name = dto.Name,
            IsRoot = dto.IsRoot,
            CriterionType = criterion.CriterionType,
            PropertyName = dto.PropertyName,
            AggregationMode = dto.AggregationMode,
            ItemScope = dto.ItemScope,
            ScopeItemIds = dto.ScopeItemIds,
            Threshold = dto.Threshold,
            Operator = dto.Operator,
            OperandCriterionIds = dto.OperandCriterionIds,
            SuccessType = dto.SuccessType,
            CycleTarget = dto.CycleTarget,
        };

        var existingIds = await DbContext.Criteria
            .Where(c => c.HabitId == habitId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var existingProperties = (await DbContext.ItemProperties
                .Where(p => p.HabitId == habitId)
                .Select(p => new { p.Name, p.PropertyType })
                .ToListAsync(cancellationToken))
            .Select(p => (p.Name, p.PropertyType))
            .ToList();
        var existingItemIds = await DbContext.HabitItems
            .Where(i => i.HabitId == habitId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        if (criterion.CriterionType == CriterionType.Condition)
        {
            IReadOnlyCollection<string>? scopedNames = null;
            if (dto.ItemScope == ItemScope.Subset && dto.ScopeItemIds is { Count: > 0 })
            {
                scopedNames = await DbContext.ItemProperties
                    .Where(p => p.HabitId == habitId && dto.ScopeItemIds.Contains(p.ItemId))
                    .Select(p => p.Name)
                    .ToListAsync(cancellationToken);
            }
            ValidateStandaloneCondition(createShape, existingProperties, existingItemIds, scopedNames);
        }
        else
        {
            ValidateStandaloneComposite(createShape, existingIds);
        }
        HabitRuleValidator.ValidateTargets(createShape, habit.Cycle);

        // DAG check with the updated operand edges overlaid on the rest of the habit graph.
        var habitComposites = await DbContext.CriterionComposites
            .Where(co => co.Criterion!.HabitId == habitId)
            .Include(co => co.Operands)
            .ToListAsync(cancellationToken);
        var edges = habitComposites.ToDictionary(
            co => co.CriterionId,
            co => (IReadOnlyList<int>)co.Operands.Select(o => o.OperandCriterionId).ToList());
        if (criterion.CriterionType == CriterionType.Composite)
        {
            edges[criterionId] = createShape.OperandCriterionIds ?? new List<int>();
        }
        HabitRuleValidator.AssertAcyclic(edges);

        criterion.Name = dto.Name;
        criterion.SuccessType = dto.IsRoot ? dto.SuccessType : null;
        criterion.CycleTarget = dto.IsRoot && dto.SuccessType == SuccessType.Daily ? dto.CycleTarget : null;

        if (criterion.CriterionType == CriterionType.Condition)
        {
            var condition = criterion.Condition!;
            condition.PropertyName = dto.PropertyName!;
            condition.AggregationMode = dto.AggregationMode;
            condition.ItemScope = dto.ItemScope ?? ItemScope.All;
            condition.Threshold = dto.Threshold!.Value;
            DbContext.CriterionConditionScopeItems.RemoveRange(condition.ScopeItems.ToList());
            condition.ScopeItems.Clear();
            if (condition.ItemScope == ItemScope.Subset)
            {
                foreach (var itemId in dto.ScopeItemIds!)
                {
                    condition.ScopeItems.Add(new CriterionConditionScopeItem { CriterionCondition = condition, ItemId = itemId });
                }
            }
        }
        else
        {
            var composite = criterion.Composite!;
            composite.Operator = dto.Operator!.Value;
            DbContext.CriterionCompositeOperands.RemoveRange(composite.Operands.ToList());
            composite.Operands.Clear();
            var order = 0;
            foreach (var operandId in dto.OperandCriterionIds!)
            {
                composite.Operands.Add(new CriterionCompositeOperand
                {
                    Composite = composite,
                    OperandCriterionId = operandId,
                    Order = order++,
                });
            }
        }

        if (dto.IsRoot && !criterion.IsRoot)
        {
            // Demote the previous root and clear its targets in the same SaveChanges
            // (targets are valid on the root only — ValidateTargets enforces this at write time).
            var previousRoot = await DbContext.Criteria
                .FirstOrDefaultAsync(c => c.HabitId == habitId && c.IsRoot && c.Id != criterionId, cancellationToken);
            if (previousRoot != null)
            {
                previousRoot.IsRoot = false;
                previousRoot.SuccessType = null;
                previousRoot.CycleTarget = null;
            }
            criterion.IsRoot = true;
        }

        await DbContext.SaveChangesAsync(cancellationToken);
        return ToDto(criterion);
    }

    // DELETE: api/Habits/{habitId}/Criteria/{criterionId}
    [HttpDelete("{criterionId:int}")]
    public async Task<IActionResult> Delete(int habitId, int criterionId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var criterion = await LoadOwnedCriterionAsync(habitId, criterionId, userId, cancellationToken);

        if (criterion.IsRoot)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.RootCriterionProtected,
                "Cannot delete the root criterion — designate another root first.");
        }
        if (await DbContext.CriterionCompositeOperands.AnyAsync(
                o => o.OperandCriterionId == criterionId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.CriterionInUse,
                "Criterion is referenced as an operand by another criterion.");
        }

        if (criterion.Condition != null)
        {
            DbContext.CriterionConditionScopeItems.RemoveRange(criterion.Condition.ScopeItems);
            DbContext.CriterionConditions.Remove(criterion.Condition);
        }
        if (criterion.Composite != null)
        {
            DbContext.CriterionCompositeOperands.RemoveRange(criterion.Composite.Operands);
            DbContext.CriterionComposites.Remove(criterion.Composite);
        }
        DbContext.Criteria.Remove(criterion);

        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
