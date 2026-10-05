using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Habit CRUD, deactivation, and per-day history. User-scoped via JWT claims — every
/// query filters on OwnerId (FR-6). Progress is embedded in habit responses so the UI
/// can render live cycle state without extra calls. See docs/design-habit-api.md.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class HabitsController : HabitControllerBase
{
    private readonly HabitEvaluationService _evaluation;
    private readonly ILogger<HabitsController> _logger;

    public HabitsController(AppDbContext dbContext, HabitEvaluationService evaluation, ILogger<HabitsController> logger)
        : base(dbContext)
    {
        _evaluation = evaluation;
        _logger = logger;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

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

    // GET: api/Habits
    [HttpGet]
    public async Task<ActionResult<List<HabitOutDto>>> GetAll(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habits = await DbContext.Habits
            .Where(h => h.OwnerId == userId)
            .OrderBy(h => h.Id)
            .ToListAsync(cancellationToken);

        var punchHabitIds = (await DbContext.Punches
            .Where(p => p.OwnerId == userId)
            .Select(p => p.HabitId)
            .Distinct()
            .ToListAsync(cancellationToken)).ToHashSet();

        var result = new List<HabitOutDto>(habits.Count);
        foreach (var habit in habits)
        {
            result.Add(await ToHabitOutAsync(habit, punchHabitIds.Contains(habit.Id), cancellationToken));
        }

        return result;
    }

    // GET: api/Habits/{habitId}
    [HttpGet("{habitId:int}")]
    public async Task<ActionResult<HabitOutDto>> GetById(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        return await ToHabitOutAsync(habit, await HasPunchesAsync(habitId, cancellationToken), cancellationToken);
    }

    // POST: api/Habits
    [HttpPost]
    public async Task<ActionResult<HabitOutDto>> Create([FromBody] HabitCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        HabitRuleValidator.RequireName(dto.Name, "Habit");
        HabitRuleValidator.RequireDescriptionLength(dto.Description);
        HabitRuleValidator.ValidateDateRange(dto.StartDate, dto.EndDate);

        // A nullable enum: an omitted cycle arrives as null (an integer cycle value is a 400
        // from the string-only enum converter — see Program.cs). Never bind to Daily silently.
        if (dto.Cycle is null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.MissingCycle,
                "A habit requires a cycle ('daily', 'weekly', 'monthly' or 'whole').");
        }
        var cycle = dto.Cycle.Value;

        if (dto.Items.Count == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.NoItems, "A habit requires at least one item.");
        }
        if (dto.Criteria.Count == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.NoCriteria, "A habit requires at least one criterion.");
        }

        var itemNames = new HashSet<string>(StringComparer.Ordinal);
        var nameTypePairs = new List<(string Name, Data.Entities.PropertyType Type)>();
        foreach (var item in dto.Items)
        {
            HabitRuleValidator.ValidateItemShape(item);
            if (!itemNames.Add(item.Name))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                    $"Duplicate item name '{item.Name}' within the habit.");
            }

            // Habit-wide same-name ⇒ same-type rule (spec): a criterion binds a property
            // by name across the scope, so the name must carry exactly one type.
            foreach (var p in item.Properties)
            {
                var propertyType = HabitRuleValidator.RequirePropertyType(p.PropertyType, p.Name);
                HabitRuleValidator.ValidatePropertyTypeConsistency(nameTypePairs, p.Name, propertyType);
                nameTypePairs.Add((p.Name, propertyType));
            }
        }

        // Full payload validation (root count, criterion shapes, name resolution, DAG, targets)
        // runs BEFORE any entity is added, so a rejection conditions no partial state.
        HabitRuleValidator.ValidateCriterionCreatePayload(dto.Criteria, cycle, dto.Items);

        var habit = new Habit
        {
            OwnerId = userId,
            Name = dto.Name,
            Description = dto.Description,
            Cycle = cycle,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            State = HabitState.Active,
            CreatedAt = DateTime.UtcNow,
        };

        var itemIdsByName = new Dictionary<string, HabitItem>(StringComparer.Ordinal);
        for (var i = 0; i < dto.Items.Count; i++)
        {
            var itemDto = dto.Items[i];
            var item = new HabitItem
            {
                Habit = habit,
                OwnerId = userId,
                Name = itemDto.Name,
                Order = itemDto.Order,
                CreatedAt = DateTime.UtcNow,
            };
            foreach (var propDto in itemDto.Properties)
            {
                item.Properties.Add(new ItemProperty
                {
                    Item = item,
                    Habit = habit,
                    OwnerId = userId,
                    Name = propDto.Name,
                    // Types were already required + validated (ValidateItemShape); resolving
                    // again here keeps the nullable wire field out of the non-nullable entity.
                    PropertyType = HabitRuleValidator.RequirePropertyType(propDto.PropertyType, propDto.Name),
                    BaseRate = propDto.BaseRate,
                    ItemUniqueness = propDto.ItemUniqueness,
                    Order = propDto.Order,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            DbContext.HabitItems.Add(item);
            itemIdsByName[item.Name] = item;
        }

        // Criteria are created first without their cross references (which need assigned ids);
        // the graph is in memory so temporaries resolve by name before a single SaveChanges.
        var criteriaByTempKey = new Dictionary<int, Criterion>();
        for (var i = 0; i < dto.Criteria.Count; i++)
        {
            var cDto = dto.Criteria[i];
            var criterion = new Criterion
            {
                Habit = habit,
                OwnerId = userId,
                Name = cDto.Name,
                // Type was already required by ValidateCriterionCreatePayload.
                CriterionType = HabitRuleValidator.RequireCriterionType(cDto.CriterionType, cDto.Name),
                IsRoot = cDto.IsRoot,
                SuccessType = cDto.IsRoot ? cDto.SuccessType : null,
                CycleTarget = cDto.IsRoot && cDto.SuccessType == SuccessType.Daily ? cDto.CycleTarget : null,
                CreatedAt = DateTime.UtcNow,
            };
            DbContext.Criteria.Add(criterion);
            criteriaByTempKey[i] = criterion;
        }

        for (var i = 0; i < dto.Criteria.Count; i++)
        {
            var cDto = dto.Criteria[i];
            var criterion = criteriaByTempKey[i];

            if (cDto.CriterionType == CriterionType.Condition)
            {
                // The condition binds the property NAME (spec): aggregation resolves every
                // in-scope item's same-named row at evaluation time.
                var condition = new CriterionCondition
                {
                    Criterion = criterion,
                    PropertyName = cDto.PropertyName!,
                    AggregationMode = cDto.AggregationMode,
                    ItemScope = cDto.ItemScope ?? ItemScope.All,
                    Threshold = cDto.Threshold!.Value,
                };

                if (condition.ItemScope == ItemScope.Subset && cDto.ScopeItemNames is { Count: > 0 })
                {
                    foreach (var scopeName in cDto.ScopeItemNames)
                    {
                        condition.ScopeItems.Add(new CriterionConditionScopeItem
                        {
                            CriterionCondition = condition,
                            Item = itemIdsByName[scopeName],
                        });
                    }
                }

                DbContext.CriterionConditions.Add(condition);
            }
            else
            {
                var composite = new CriterionComposite
                {
                    Criterion = criterion,
                    Operator = cDto.Operator!.Value,
                };
                var order = 0;
                foreach (var operandName in cDto.OperandCriterionNames!)
                {
                    var index = dto.Criteria.FindIndex(c => c.Name == operandName);
                    composite.Operands.Add(new CriterionCompositeOperand
                    {
                        Composite = composite,
                        OperandCriterion = criteriaByTempKey[index],
                        Order = order++,
                    });
                }
                DbContext.CriterionComposites.Add(composite);
            }
        }

        await DbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Habit {HabitId} created for user {UserId}.", habit.Id, userId);
        var created = await ToHabitOutAsync(habit, hasPunches: false, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { habitId = habit.Id }, created);
    }

    // PUT: api/Habits/{habitId}
    [HttpPut("{habitId:int}")]
    public async Task<ActionResult<HabitOutDto>> Update(int habitId, [FromBody] HabitUpdateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);

        HabitRuleValidator.RequireName(dto.Name, "Habit");
        HabitRuleValidator.RequireDescriptionLength(dto.Description);
        HabitRuleValidator.ValidateDateRange(dto.StartDate, dto.EndDate);

        // Same null-means-omitted rule as Create: never fall back to Daily silently.
        if (dto.Cycle is null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.MissingCycle,
                "A habit requires a cycle ('daily', 'weekly', 'monthly' or 'whole').");
        }
        var cycle = dto.Cycle.Value;

        // Cycle change would make historical cycle boundaries ambiguous.
        if (cycle != habit.Cycle && await HasPunchesAsync(habitId, cancellationToken))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.StructuralChangeBlocked,
                "cycle cannot be changed once punch records exist.");
        }

        habit.Name = dto.Name;
        habit.Description = dto.Description;
        habit.Cycle = cycle;
        habit.StartDate = dto.StartDate;
        habit.EndDate = dto.EndDate;
        await DbContext.SaveChangesAsync(cancellationToken);

        return await ToHabitOutAsync(habit, await HasPunchesAsync(habitId, cancellationToken), cancellationToken);
    }

    // POST: api/Habits/{habitId}/Deactivate — irreversible.
    [HttpPost("{habitId:int}/Deactivate")]
    public async Task<ActionResult<HabitOutDto>> Deactivate(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        if (habit.State == HabitState.Inactive)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.AlreadyInactive, "Habit is already inactive.");
        }

        habit.State = HabitState.Inactive;
        habit.DeactivatedDate = Today; // spec FR-2.4: drives the last-actual-cycle display (FR-2.2)
        await DbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Habit {HabitId} deactivated for user {UserId}.", habitId, userId);
        return await ToHabitOutAsync(habit, await HasPunchesAsync(habitId, cancellationToken), cancellationToken);
    }

    // ── Shares: per-habit invitations (owner-managed, read-only for invitees) ──

    // GET: api/Habits/{habitId}/Shares — the owner's invitee list.
    [HttpGet("{habitId:int}/Shares")]
    public async Task<ActionResult<List<ShareGrantOutDto>>> GetShares(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        return await DbContext.HabitShareGrants
            .Where(g => g.HabitId == habitId)
            .OrderBy(g => g.Id)
            .Select(g => new ShareGrantOutDto
            {
                Id = g.Id,
                GranteeUserId = g.GranteeUserId,
                GranteeUserName = g.GranteeUserName,
                CreatedAt = g.CreatedAt,
            })
            .ToListAsync(cancellationToken);
    }

    // POST: api/Habits/{habitId}/Shares — invite one user (immediate, no accept step).
    [HttpPost("{habitId:int}/Shares")]
    public async Task<ActionResult<ShareGrantOutDto>> AddShare(
        int habitId, [FromBody] ShareGrantCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);

        var granteeId = dto.GranteeUserId.Trim();
        var granteeName = dto.GranteeUserName.Trim();
        if (granteeId.Length == 0 || granteeName.Length == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidGrantee,
                "A share grant requires a non-empty granteeUserId and granteeUserName.");
        }
        // Design DDL lengths (SQLite does not enforce them): GranteeUserId 200,
        // GranteeUserName 500.
        if (granteeId.Length > 200)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                $"granteeUserId must be at most 200 characters (got {granteeId.Length}).");
        }
        if (granteeName.Length > 500)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                $"granteeUserName must be at most 500 characters (got {granteeName.Length}).");
        }

        // A-L7: compare the TRIMMED grantee against the (trimmed) claim id — a padded
        // self-invite would otherwise slip past the check and store a grant that never
        // matches (SharedHabits lookups compare on the trimmed grant value).
        if (string.Equals(granteeId, userId.Trim(), StringComparison.Ordinal))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidGrantee,
                "A habit's owner cannot be invited to their own habit.");
        }

        // Owner display name from the CALLER'S token (server-side snapshot — a client can
        // never forge someone else's name). Missing claim → the viewer would see an
        // anonymous credit, so reject explicitly (the audience's tokens always carry it).
        var ownerName = GetUserName();
        if (string.IsNullOrEmpty(ownerName))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.MissingShareOwnerName,
                "The caller's token carries no display name to credit viewers with.");
        }

        // Read-then-write duplicate check: run the pre-check and the insert inside ONE
        // Serializable transaction (SQLite: BEGIN IMMEDIATE — the writer takes its lock
        // up front, so two concurrent invites for the same grantee serialize instead of
        // both passing the AnyAsync check). The (HabitId, GranteeUserId) unique index is
        // the backstop; the exception filter maps its violation to 422 duplicateName too.
        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        if (await DbContext.HabitShareGrants.AnyAsync(
            g => g.HabitId == habitId && g.GranteeUserId == granteeId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                $"User '{granteeName}' is already invited to this habit.");
        }

        var grant = new HabitShareGrant
        {
            HabitId = habitId,
            OwnerId = userId,
            GranteeUserId = granteeId,
            GranteeUserName = granteeName,
            OwnerName = ownerName,
            CreatedAt = DateTime.UtcNow,
        };
        DbContext.HabitShareGrants.Add(grant);
        await DbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Habit {HabitId} shared with user {GranteeId} by {UserId}.", habitId, granteeId, userId);
        return CreatedAtAction(
            nameof(GetShares),
            new { habitId },
            new ShareGrantOutDto
            {
                Id = grant.Id,
                GranteeUserId = grant.GranteeUserId,
                GranteeUserName = grant.GranteeUserName,
                CreatedAt = grant.CreatedAt,
            });
    }

    // DELETE: api/Habits/{habitId}/Shares/{grantId} — revoke (re-privatizes instantly).
    [HttpDelete("{habitId:int}/Shares/{grantId:int}")]
    public async Task<IActionResult> RemoveShare(int habitId, int grantId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);

        // Grant-scoped 404: a foreign grantId under a valid habitId must not leak either.
        var grant = await DbContext.HabitShareGrants
            .FirstOrDefaultAsync(g => g.Id == grantId && g.HabitId == habitId, cancellationToken);
        if (grant == null)
        {
            throw HabitException.NotFound("Share grant");
        }

        DbContext.HabitShareGrants.Remove(grant);
        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // DELETE: api/Habits/{habitId}
    [HttpDelete("{habitId:int}")]
    public async Task<IActionResult> Delete(int habitId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await DbContext.Habits
            .Include(h => h.Items).ThenInclude(i => i.Properties)
            .Include(h => h.Criteria).ThenInclude(c => c.Condition!.ScopeItems)
            .Include(h => h.Criteria).ThenInclude(c => c.Composite!.Operands)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.OwnerId == userId, cancellationToken);
        if (habit == null)
        {
            throw HabitException.NotFound("Habit");
        }

        // Load and remove the full graph explicitly (punches + values + share grants) so
        // cascade semantics do not depend on provider-level FK enforcement.
        var punches = await DbContext.Punches
            .Where(p => p.HabitId == habitId)
            .Include(p => p.Values)
            .ToListAsync(cancellationToken);
        DbContext.PunchValues.RemoveRange(punches.SelectMany(p => p.Values));
        DbContext.Punches.RemoveRange(punches);
        DbContext.HabitShareGrants.RemoveRange(
            await DbContext.HabitShareGrants.Where(g => g.HabitId == habitId).ToListAsync(cancellationToken));
        DbContext.Remove(habit);

        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // GET: api/Habits/{habitId}/History?from=&to=
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

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        return await _evaluation.BuildHistoryAsync(habit, from, to, Today, cancellationToken);
    }
}
