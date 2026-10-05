using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;

namespace aclearningutil.Controllers;

/// <summary>
/// Punch sessions: record, list, edit, delete. New punches are only accepted while the
/// habit is active and the PUNCH DATE falls inside the habit's active window (FR-3.3 —
/// the gate follows the punched day, not "today"); existing records stay
/// editable/deletable even after deactivation or the end date (FR-3.4). A punch may
/// carry a client-supplied <c>punchDate</c> to back-fill a missed past day (never a
/// future one — NFR-5); omitted means the server's local today. The date is immutable
/// afterwards: move a punch by delete + recreate.
/// See docs/design-habit-api.md § Punches.
/// </summary>
[Route("api/Habits")]
[ApiController]
[Authorize]
[HabitExceptionFilter]
public class HabitPunchesController : HabitControllerBase
{
    private readonly ILogger<HabitPunchesController> _logger;

    public HabitPunchesController(AppDbContext dbContext, ILogger<HabitPunchesController> logger)
        : base(dbContext)
    {
        _logger = logger;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private sealed record PropInfo(int Id, int ItemId, string Name, PropertyType PropertyType, ItemUniqueness? Uniqueness);

    private async Task<Dictionary<int, PropInfo>> LoadPropMapAsync(int habitId, CancellationToken ct)
    {
        var props = await DbContext.ItemProperties
            .Where(p => p.HabitId == habitId)
            .ToListAsync(ct);
        return props.ToDictionary(p => p.Id, p => new PropInfo(p.Id, p.ItemId, p.Name, p.PropertyType, p.ItemUniqueness));
    }

    private static PunchOutDto MapPunch(Punch punch, Dictionary<int, PropInfo> props) => new()
    {
        Id = punch.Id,
        HabitId = punch.HabitId,
        ItemId = punch.ItemId,
        PunchedAt = punch.PunchedAt,
        PunchDate = punch.PunchDate,
        CreatedAt = punch.CreatedAt,
        Values = punch.Values.Select(v =>
        {
            props.TryGetValue(v.PropertyId, out var info);
            return new PropertyValueOutDto
            {
                PropertyId = v.PropertyId,
                PropertyName = info?.Name ?? string.Empty,
                PropertyType = info?.PropertyType ?? PropertyType.Boolean,
                BoolValue = v.BoolValue,
                NumValue = v.NumValue,
                ListEntries = v.ListEntries == null ? null : HabitEvaluationService.ParseEntries(v.ListEntries),
            };
        }).ToList(),
    };

    // GET: api/Habits/{habitId}/Punches?from=&to= — habit-scoped aggregate view.
    [HttpGet("{habitId:int}/Punches")]
    public async Task<ActionResult<List<PunchOutDto>>> GetHabitPunches(
        int habitId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        ValidateQueryRange(from, to);
        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var punches = await QueryPunchesAsync(habitId, itemId: null, from, to, cancellationToken);
        var props = await LoadPropMapAsync(habitId, cancellationToken);
        return punches.Select(p => MapPunch(p, props)).ToList();
    }

    // GET: api/Habits/{habitId}/Items/{itemId}/Punches?from=&to=
    [HttpGet("{habitId:int}/Items/{itemId:int}/Punches")]
    public async Task<ActionResult<List<PunchOutDto>>> GetItemPunches(
        int habitId, int itemId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        ValidateQueryRange(from, to);
        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);
        var punches = await QueryPunchesAsync(habitId, itemId, from, to, cancellationToken);
        var props = await LoadPropMapAsync(habitId, cancellationToken);
        return punches.Select(p => MapPunch(p, props)).ToList();
    }

    // GET: api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}
    [HttpGet("{habitId:int}/Items/{itemId:int}/Punches/{punchId:int}")]
    public async Task<ActionResult<PunchOutDto>> GetById(
        int habitId, int itemId, int punchId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var punch = await LoadOwnedPunchAsync(habitId, itemId, punchId, userId, cancellationToken);
        var props = await LoadPropMapAsync(habitId, cancellationToken);
        return MapPunch(punch, props);
    }

    // POST: api/Habits/{habitId}/Items/{itemId}/Punches
    [HttpPost("{habitId:int}/Items/{itemId:int}/Punches")]
    public async Task<ActionResult<PunchOutDto>> Create(
        int habitId, int itemId, [FromBody] PunchCreateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        await LoadOwnedItemAsync(habitId, itemId, userId, cancellationToken);

        if (habit.State != HabitState.Active)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.HabitInactive, "Habit is inactive and no longer accepts punches.");
        }

        // FR-3.3 (revised spec): the state + window checks apply to the PUNCH DATE, not
        // to "today" — an active habit whose period already ended still refuses new
        // punches (no in-window date exists anymore), while a habit that has not ended
        // accepts a back-fill for any past in-window day. Editing/deleting existing
        // records is exempt (handled in Update/Delete, which never check state/window).
        var today = Today;
        var punchDay = dto.PunchDate ?? today;
        if (punchDay > today)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.OutOfWindow, "A punch cannot be recorded for a future date.");
        }
        if (punchDay < habit.StartDate || (habit.EndDate.HasValue && punchDay > habit.EndDate.Value))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.OutOfWindow, "The punch date is outside the habit's active date range.");
        }

        var props = await LoadPropMapAsync(habitId, cancellationToken);
        var validated = ValidateValues(dto.Values, props, itemId);

        // The same-day read-then-write below runs inside one explicit transaction (design
        // § PunchValues: boolean upsert "enforced inside a single EF transaction").
        // Serializable isolation → Microsoft.Data.Sqlite issues BEGIN IMMEDIATE: the write
        // lock is taken BEFORE the read, so a concurrent punch for the same day can never
        // interleave between read and insert (both readers missing each other's row would
        // double-insert the one-per-day boolean). Verified against the provider
        // (SqliteSerializableTransactionTests): the default BeginTransactionAsync(ct)
        // overload already resolves to this immediate BEGIN via the provider's
        // DefaultIsolationLevel — the explicit level PINS that guarantee instead of
        // depending on the provider default.
        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        // Load the punch day's cycle-window punches once: they serve both the list
        // uniqueness pre-check (FR-3.2) and the boolean same-day upsert targets. For a
        // back-fill this is the window CONTAINING the punched day, not the current one.
        // An open-ended whole cycle has no upper bound (cycle window end is NULL).
        var (windowFrom, windowTo) = HabitEvaluationService.ComputeCycleWindow(habit, punchDay);
        var windowQuery = DbContext.Punches
            .Where(p => p.HabitId == habitId && p.ItemId == itemId && p.PunchDate >= windowFrom);
        if (windowTo.HasValue)
        {
            windowQuery = windowQuery.Where(p => p.PunchDate <= windowTo.Value);
        }
        var windowPunches = await windowQuery
            .Include(p => p.Values)
            .ToListAsync(cancellationToken);

        foreach (var v in validated.Where(v => v.Property.PropertyType == PropertyType.List))
        {
            var perCycle = v.Property.Uniqueness == ItemUniqueness.PerCycle;
            var scoped = perCycle
                ? windowPunches
                : windowPunches.Where(p => p.PunchDate == punchDay).ToList();
            var already = scoped
                .SelectMany(p => p.Values)
                .Where(pv => pv.PropertyId == v.Property.Id && pv.ListEntries != null)
                .SelectMany(pv => HabitEvaluationService.ParseEntries(pv.ListEntries!))
                .ToList();
            RejectDuplicates(v.Entries!, already, v.Property.Name);
        }

        // Boolean same-day upsert targets within this item — for the punch day.
        var upsertTargets = BuildBooleanDayIndex(windowPunches, punchDay);

        var now = DateTime.UtcNow;
        var newValues = new List<PunchValue>();
        Punch? affected = null;

        foreach (var v in validated)
        {
            if (v.Property.PropertyType == PropertyType.Boolean
                && upsertTargets.TryGetValue(v.Property.Id, out var existingRow))
            {
                existingRow.BoolValue = v.Bool;
                existingRow.Punch!.PunchedAt = now;
                affected = existingRow.Punch;
                continue;
            }

            newValues.Add(new PunchValue
            {
                PropertyId = v.Property.Id,
                BoolValue = v.Property.PropertyType == PropertyType.Boolean ? v.Bool : null,
                NumValue = v.Property.PropertyType == PropertyType.Numeric ? v.Num : null,
                ListEntries = v.Property.PropertyType == PropertyType.List ? HabitEvaluationService.SerializeEntries(v.Entries!) : null,
            });
        }

        Punch punch;
        if (newValues.Count > 0)
        {
            punch = new Punch
            {
                HabitId = habitId,
                ItemId = itemId,
                OwnerId = userId,
                PunchedAt = now,
                PunchDate = punchDay,
                CreatedAt = now,
                Values = newValues,
            };
            foreach (var nv in newValues)
            {
                nv.Punch = punch;
            }
            DbContext.Punches.Add(punch);
            affected = punch;
        }
        else
        {
            // Pure boolean-update session: the upserted existing punch is the affected record.
            punch = affected!;
        }

        await DbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("Punch {PunchId} recorded for habit {HabitId}, item {ItemId}.", punch.Id, habitId, itemId);
        return CreatedAtAction(nameof(GetById), new { habitId, itemId, punchId = punch.Id }, MapPunch(punch, props));
    }

    // PUT: api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}
    [HttpPut("{habitId:int}/Items/{itemId:int}/Punches/{punchId:int}")]
    public async Task<ActionResult<PunchOutDto>> Update(
        int habitId, int itemId, int punchId, [FromBody] PunchUpdateDto dto, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        var habit = await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var punch = await LoadOwnedPunchAsync(habitId, itemId, punchId, userId, cancellationToken);

        var props = await LoadPropMapAsync(habitId, cancellationToken);
        var validated = ValidateValues(dto.Values, props, itemId);

        // The uniqueness re-check and the same-day boolean upsert below read before they
        // write — run them in one explicit Serializable (BEGIN IMMEDIATE) transaction,
        // same rule as Create: the write lock taken at BEGIN keeps a concurrent writer
        // from inserting the day's second boolean row between our read and write.
        await using var transaction = await DbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, cancellationToken);

        // Uniqueness re-evaluation excludes the punch being edited (FR-3.2).
        var day = punch.PunchDate;
        var (windowFrom, windowTo) = HabitEvaluationService.ComputeCycleWindow(habit, day);
        var othersQuery = DbContext.PunchValues
            .Where(v => v.Property!.HabitId == habitId
                       && v.Punch!.ItemId == itemId
                       && v.Punch.PunchDate >= windowFrom
                       && v.PunchId != punchId);
        if (windowTo.HasValue)
        {
            othersQuery = othersQuery.Where(v => v.Punch!.PunchDate <= windowTo.Value);
        }
        var otherPunchValues = await othersQuery.ToListAsync(cancellationToken);

        foreach (var v in validated.Where(v => v.Property.PropertyType == PropertyType.List))
        {
            var scoped = v.Property.Uniqueness == ItemUniqueness.PerCycle
                ? otherPunchValues.Where(pv => pv.PropertyId == v.Property.Id)
                : otherPunchValues.Where(pv => pv.PropertyId == v.Property.Id && pv.Punch!.PunchDate == day);
            var existing = new List<string>();
            foreach (var pv in scoped)
            {
                existing.AddRange(HabitEvaluationService.ParseEntries(pv.ListEntries ?? "[]"));
            }
            RejectDuplicates(v.Entries!, existing, v.Property.Name);
        }

        // The day's live boolean rows across every session of this item — editing a punch
        // upserts them instead of inserting a second boolean row for the same day (the
        // same promise the Create upsert makes: one boolean row per property/item/day).
        var sameDayPunches = await DbContext.Punches
            .Where(p => p.HabitId == habitId && p.ItemId == itemId && p.PunchDate == day)
            .Include(p => p.Values)
            .ToListAsync(cancellationToken);
        var booleanDayIndex = BuildBooleanDayIndex(sameDayPunches);

        // Replace the session's values (rows for omitted properties are removed).
        var stale = punch.Values.Where(pv => validated.All(v => v.Property.Id != pv.PropertyId)).ToList();
        DbContext.PunchValues.RemoveRange(stale);
        foreach (var s in stale)
        {
            punch.Values.Remove(s);
        }

        var now = DateTime.UtcNow;
        foreach (var v in validated)
        {
            var existingRow = punch.Values.FirstOrDefault(pv => pv.PropertyId == v.Property.Id);

            if (v.Property.PropertyType == PropertyType.Boolean
                && existingRow is null
                && booleanDayIndex.TryGetValue(v.Property.Id, out var sameDayRow))
            {
                // The day's boolean value lives in another session's punch — update it in
                // place (last write wins) rather than creating a duplicate live row here.
                sameDayRow.BoolValue = v.Bool;
                sameDayRow.Punch!.PunchedAt = now;
                continue;
            }

            if (existingRow != null)
            {
                existingRow.BoolValue = v.Property.PropertyType == PropertyType.Boolean ? v.Bool : null;
                existingRow.NumValue = v.Property.PropertyType == PropertyType.Numeric ? v.Num : null;
                existingRow.ListEntries = v.Property.PropertyType == PropertyType.List
                    ? HabitEvaluationService.SerializeEntries(v.Entries!)
                    : null;
            }
            else
            {
                punch.Values.Add(new PunchValue
                {
                    PunchId = punch.Id,
                    PropertyId = v.Property.Id,
                    BoolValue = v.Property.PropertyType == PropertyType.Boolean ? v.Bool : null,
                    NumValue = v.Property.PropertyType == PropertyType.Numeric ? v.Num : null,
                    ListEntries = v.Property.PropertyType == PropertyType.List
                        ? HabitEvaluationService.SerializeEntries(v.Entries!)
                        : null,
                });
            }
        }

        punch.PunchedAt = now;
        await DbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MapPunch(punch, props);
    }

    /// <summary>
    /// Maps property id → the day's live boolean row across all punch sessions of an item
    /// (a boolean property keeps at most one value row per property/item/day; a later write
    /// replaces it — design § PunchValues). Shared by Create (from the already-loaded cycle
    /// window punches, filtered to the punch day) and Update (from the edited punch's day punches).
    /// </summary>
    private static Dictionary<int, PunchValue> BuildBooleanDayIndex(IEnumerable<Punch> punches, DateOnly? day = null)
    {
        var index = new Dictionary<int, PunchValue>();
        var scoped = day is null ? punches : punches.Where(p => p.PunchDate == day.Value);
        foreach (var p in scoped)
        {
            foreach (var pv in p.Values.Where(pv => pv.BoolValue != null))
            {
                index[pv.PropertyId] = pv;
            }
        }
        return index;
    }

    // DELETE: api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}
    [HttpDelete("{habitId:int}/Items/{itemId:int}/Punches/{punchId:int}")]
    public async Task<IActionResult> Delete(
        int habitId, int itemId, int punchId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            throw HabitException.Unauthenticated("User ID not found in token.");
        }

        await LoadOwnedHabitAsync(habitId, userId, cancellationToken);
        var punch = await LoadOwnedPunchAsync(habitId, itemId, punchId, userId, cancellationToken);

        DbContext.PunchValues.RemoveRange(punch.Values);
        DbContext.Punches.Remove(punch);
        await DbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Inverted ranges are rejected like History does (422 invalidDateRange).</summary>
    private static void ValidateQueryRange(DateOnly? from, DateOnly? to)
    {
        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidDateRange, "from must be on or before to.");
        }
    }

    private sealed record ValidatedValue(PropInfo Property, bool? Bool, double? Num, List<string>? Entries);

    private static List<ValidatedValue> ValidateValues(
        List<PropertyValueCreateDto> values, Dictionary<int, PropInfo> props, int itemId)
    {
        if (values.Count == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType, "A punch requires at least one value.");
        }
        if (values.Count > HabitRuleValidator.MaxValuesPerPunch)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"A punch session records at most {HabitRuleValidator.MaxValuesPerPunch} values (got {values.Count}).");
        }

        var result = new List<ValidatedValue>();
        var seenProperty = new HashSet<int>();
        foreach (var v in values)
        {
            if (!seenProperty.Add(v.PropertyId))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                    $"Duplicate value for property {v.PropertyId} in one session.");
            }
            if (!props.TryGetValue(v.PropertyId, out var prop) || prop.ItemId != itemId)
            {
                // A property from a different item (or another habit) is treated as missing.
                throw HabitException.NotFound("Property");
            }

            var provided = new[] { v.BoolValue != null, v.NumValue != null, v.ListEntries != null }.Count(x => x);
            if (provided != 1)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                    $"Property '{prop.Name}': exactly one of boolValue/numValue/listEntries must be provided.");
            }

            switch (prop.PropertyType)
            {
                case PropertyType.Boolean:
                    if (v.BoolValue == null)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                            $"Property '{prop.Name}' requires a boolean value.");
                    }
                    result.Add(new ValidatedValue(prop, v.BoolValue, null, null));
                    break;

                case PropertyType.Numeric:
                    // !IsFinite first: NaN/Infinity slip past `<= 0`.
                    if (v.NumValue == null || !double.IsFinite(v.NumValue.Value) || v.NumValue.Value <= 0)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                            $"Property '{prop.Name}' requires a positive numeric value.");
                    }
                    result.Add(new ValidatedValue(prop, null, v.NumValue, null));
                    break;

                default: // List
                    if (v.ListEntries == null || v.ListEntries.Count == 0
                        || v.ListEntries.Any(e => string.IsNullOrWhiteSpace(e)))
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                            $"Property '{prop.Name}' requires at least one non-empty list entry.");
                    }
                    if (v.ListEntries.Count > HabitRuleValidator.MaxEntriesPerValue)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                            $"Property '{prop.Name}': a punch value carries at most {HabitRuleValidator.MaxEntriesPerValue} entries (got {v.ListEntries.Count}).");
                    }
                    var tooLong = v.ListEntries.FirstOrDefault(e => e.Length > HabitRuleValidator.MaxEntryLength);
                    if (tooLong is not null)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                            $"Property '{prop.Name}': list entries must be at most {HabitRuleValidator.MaxEntryLength} characters (got {tooLong.Length}).");
                    }
                    // Duplicates inside a single submission violate uniqueness for the day.
                    if (v.ListEntries.Distinct(StringComparer.Ordinal).Count() != v.ListEntries.Count)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.DuplicateEntry,
                            $"Property '{prop.Name}': submission contains duplicate entries.");
                    }
                    result.Add(new ValidatedValue(prop, null, null, v.ListEntries.ToList()));
                    break;
            }
        }
        return result;
    }

    private static void RejectDuplicates(List<string> submitted, List<string> already, string propertyName)
    {
        var clash = submitted.FirstOrDefault(e => already.Contains(e, StringComparer.Ordinal));
        if (clash != null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateEntry,
                $"Entry '{clash}' was already recorded for '{propertyName}' in this period.");
        }
    }

    private async Task<HabitItem> LoadOwnedItemAsync(int habitId, int itemId, string userId, CancellationToken ct)
    {
        return await DbContext.HabitItems
            .FirstOrDefaultAsync(i => i.Id == itemId && i.HabitId == habitId && i.OwnerId == userId, ct)
            ?? throw HabitException.NotFound("Item");
    }

    private async Task<Punch> LoadOwnedPunchAsync(int habitId, int itemId, int punchId, string userId, CancellationToken ct)
    {
        return await DbContext.Punches
            .Where(p => p.Id == punchId && p.HabitId == habitId && p.ItemId == itemId && p.OwnerId == userId)
            .Include(p => p.Values)
            .FirstOrDefaultAsync(ct)
            ?? throw HabitException.NotFound("Punch");
    }

    private async Task<List<Punch>> QueryPunchesAsync(
        int habitId, int? itemId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        // The caller's ownership of the habit was validated before this point; punches are
        // habit-scoped rows, so the HabitId filter alone is tenant-safe here.
        var query = DbContext.Punches
            .Where(p => p.HabitId == habitId)
            .Include(p => p.Values)
            .AsQueryable();

        if (itemId.HasValue)
        {
            query = query.Where(p => p.ItemId == itemId.Value);
        }
        if (from.HasValue)
        {
            query = query.Where(p => p.PunchDate >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(p => p.PunchDate <= to.Value);
        }

        return await query
            .OrderByDescending(p => p.PunchDate).ThenByDescending(p => p.PunchedAt)
            .ToListAsync(ct);
    }
}
