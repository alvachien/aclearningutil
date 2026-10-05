using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using Microsoft.EntityFrameworkCore;

namespace aclearningutil.Services;

/// <summary>
/// Cycle-window computation and criterion-tree evaluation for habit progress and
/// history (docs/design-habit-api.md § Cycle Window / § Criterion Evaluation).
/// Evaluation is bottom-up over the criterion DAG. Leaves bind a property NAME and
/// aggregate over every in-scope item that defines it, honoring the condition's
/// aggregation mode; pass/fail compares the aggregate against the condition threshold
/// with the single supported operator (&gt;=). Cumulative pass is latched within a
/// cycle (spec: a passed cycle cannot be revoked). History derives every day from
/// current punches and current criteria — a fully derived view, no snapshots.
/// </summary>
public sealed class HabitEvaluationService
{
    private readonly AppDbContext _db;

    public HabitEvaluationService(AppDbContext db)
    {
        _db = db;
    }

    // ── Cycle window ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The reported cycle window [From, To] for the habit relative to <paramref name="today"/>.
    /// Standard calendar window (daily = today; weekly = Monday–Sunday; monthly = 1st–last),
    /// clamped forward to StartDate on the first cycle and back to EndDate on the last.
    /// Before StartDate the first cycle's window is returned; after EndDate — or after
    /// deactivation — the LAST actual cycle (the one containing the habit's final day,
    /// i.e. min(EndDate, DeactivatedDate − 1 day); spec FR-2.2).
    /// <see cref="HabitCycle.Whole"/> is the single-cycle special case: [StartDate, EndDate],
    /// with a NULL To for an open-ended habit (spec FR-4.1 — never a sentinel date).
    /// </summary>
    public static (DateOnly From, DateOnly? To) ComputeCycleWindow(Habit habit, DateOnly today)
    {
        if (habit.Cycle == HabitCycle.Whole)
        {
            return (habit.StartDate, habit.EndDate);
        }

        var anchor = today;
        if (anchor < habit.StartDate)
        {
            anchor = habit.StartDate; // future start: show the first cycle
        }
        if (habit.State == HabitState.Inactive && habit.DeactivatedDate.HasValue)
        {
            var lastActive = habit.DeactivatedDate.Value.AddDays(-1);
            if (lastActive < habit.StartDate)
            {
                lastActive = habit.StartDate;
            }
            if (anchor > lastActive)
            {
                anchor = lastActive;
            }
        }
        if (habit.EndDate.HasValue && anchor > habit.EndDate.Value)
        {
            anchor = habit.EndDate.Value;
        }

        var (natFrom, natTo) = NaturalWindow(habit.Cycle, anchor);

        var from = natFrom < habit.StartDate ? habit.StartDate : natFrom;
        DateOnly? to = natTo;
        if (habit.EndDate.HasValue && habit.EndDate.Value < natTo)
        {
            to = habit.EndDate.Value;
        }
        return (from, to);
    }

    /// <summary>Aggregation bound of a possibly-open window: the window end, capped at today.</summary>
    private static DateOnly AggregateTo(DateOnly? windowTo, DateOnly today) =>
        windowTo is null || windowTo > today ? today : windowTo.Value;

    private static (DateOnly From, DateOnly To) NaturalWindow(HabitCycle cycle, DateOnly anchor)
    {
        switch (cycle)
        {
            case HabitCycle.Daily:
                return (anchor, anchor);
            case HabitCycle.Weekly:
                // ISO week: Monday through Sunday.
                var offset = ((int)anchor.DayOfWeek + 6) % 7;
                var monday = anchor.AddDays(-offset);
                return (monday, monday.AddDays(6));
            case HabitCycle.Monthly:
                var first = new DateOnly(anchor.Year, anchor.Month, 1);
                var last = anchor.AddDays(DateTime.DaysInMonth(anchor.Year, anchor.Month) - anchor.Day);
                return (first, last);
            default:
                // HabitCycle.Whole is short-circuited in ComputeCycleWindow and has
                // no natural calendar window; anything else is a missing case.
                throw new ArgumentOutOfRangeException(nameof(cycle), cycle, "No natural window for this cycle.");
        }
    }

    // ── Shared loading ──────────────────────────────────────────────────────────────

    internal sealed record PropMeta(int ItemId, string Name, PropertyType Type, double BaseRate, ItemUniqueness? Uniqueness);

    internal sealed record PunchFact(
        int PunchId, int PropertyId, int ItemId, DateOnly Day,
        bool? Bool, double? Num, IReadOnlyList<string>? Entries,
        double BaseRate, ItemUniqueness? Uniqueness);

    private sealed class EvalContext
    {
        public required Dictionary<int, Criterion> CriteriaById { get; init; }
        public required Dictionary<int, PropMeta> Props { get; init; }
        public required List<PunchFact> Facts { get; init; }
    }

    private async Task<(List<Criterion> Criteria, Dictionary<int, PropMeta> Props)> LoadGraphAsync(
        int habitId, CancellationToken ct)
    {
        var criteria = await _db.Criteria
            .Where(c => c.HabitId == habitId)
            .Include(c => c.Condition).ThenInclude(l => l!.ScopeItems)
            .Include(c => c.Composite).ThenInclude(co => co!.Operands)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var props = (await _db.ItemProperties
                .Where(p => p.HabitId == habitId)
                .ToListAsync(ct))
            .ToDictionary(p => p.Id, p => new PropMeta(p.ItemId, p.Name, p.PropertyType, p.BaseRate ?? 1.0, p.ItemUniqueness));

        return (criteria, props);
    }

    private async Task<List<PunchFact>> LoadFactsAsync(int habitId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var punchQuery = _db.Punches
            .Where(p => p.HabitId == habitId)
            .AsQueryable();
        if (from.HasValue)
        {
            punchQuery = punchQuery.Where(p => p.PunchDate >= from.Value);
        }
        if (to.HasValue)
        {
            punchQuery = punchQuery.Where(p => p.PunchDate <= to.Value);
        }

        // Total order (date, instant, id) so per-punch aggregation and the `latest`
        // modes are deterministic even for same-timestamp punches.
        var punches = await punchQuery
            .Include(p => p.Values)
            .OrderBy(p => p.PunchDate).ThenBy(p => p.PunchedAt).ThenBy(p => p.Id)
            .ToListAsync(ct);

        var propMeta = await _db.ItemProperties
            .Where(p => p.HabitId == habitId)
            .ToDictionaryAsync(p => p.Id, p => new PropMeta(p.ItemId, p.Name, p.PropertyType, p.BaseRate ?? 1.0, p.ItemUniqueness), ct);

        var facts = new List<PunchFact>();
        foreach (var punch in punches)
        {
            foreach (var value in punch.Values)
            {
                if (!propMeta.TryGetValue(value.PropertyId, out var meta))
                {
                    continue;
                }
                var entries = value.ListEntries == null
                    ? null
                    : ParseEntries(value.ListEntries);
                facts.Add(new PunchFact(punch.Id, value.PropertyId, punch.ItemId, punch.PunchDate,
                    value.BoolValue, value.NumValue, entries, meta.BaseRate, meta.Uniqueness));
            }
        }
        return facts;
    }

    internal static List<string> ParseEntries(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new List<string>();
        }
    }

    internal static string SerializeEntries(IReadOnlyList<string> entries) =>
        System.Text.Json.JsonSerializer.Serialize(entries);

    /// <summary>The effective aggregation mode of a condition once its property type is known.</summary>
    internal static AggregationMode EffectiveMode(CriterionCondition condition, PropertyType type) =>
        condition.AggregationMode ?? (type switch
        {
            PropertyType.Boolean => AggregationMode.EverTrue,
            PropertyType.Numeric => AggregationMode.Sum,
            _ => AggregationMode.UnionDistinct,
        });

    // ── Criterion evaluation ────────────────────────────────────────────────────────

    /// <summary>
    /// Bottom-up evaluation of one criterion over the inclusive day range.
    /// Condition value semantics (per the condition's effective aggregation mode):
    ///   boolean  EverTrue — count of in-scope items whose property was true on at least
    ///                      one day in the range (single-day range = items true that day).
    ///   numeric  Sum/Avg/Max/Min/Latest over (numValue × baseRate) of every session in
    ///                      scope; items without baseRate contribute raw values.
    ///   list     UnionDistinct — per-item DISTINCT-entry count in the range, SUMMED over
    ///                      in-scope items (dedup is per item, not per condition: the same
    ///                      text under two items counts twice — spec RC-9). Latest — the
    ///                      per-item entries of that item's most recent punch in range.
    /// In-scope items lacking the property contribute 0 / are not counted; if no in-scope
    /// item defines the name at all the aggregate is permanently 0 (never passes — threshold
    /// is &gt; 0). Pass is always aggregate &gt;= threshold (the only operator).
    /// Composite: value = count of passing operands; pass by operator rule.
    /// </summary>
    private static (double Value, bool Passed) Evaluate(
        Criterion criterion, DateOnly from, DateOnly to, EvalContext ctx,
        Dictionary<int, (double Value, bool Passed)> memo)
    {
        if (memo.TryGetValue(criterion.Id, out var cached))
        {
            return cached;
        }

        // Guard against a malformed (cyclic) graph — should not occur; write-time checks prevent it.
        memo[criterion.Id] = (0, false);

        var result = criterion.CriterionType == CriterionType.Condition
            ? EvaluateCondition(criterion, from, to, ctx)
            : EvaluateComposite(criterion, from, to, ctx, memo);

        memo[criterion.Id] = result;
        return result;
    }

    private static (double Value, bool Passed) EvaluateCondition(
        Criterion criterion, DateOnly from, DateOnly to, EvalContext ctx)
    {
        var condition = criterion.Condition
            ?? throw new HabitException(422, HabitErrorCodes.InvalidTarget, $"Criterion '{criterion.Name}' has no condition definition.");

        // Resolve the scope: every property row in scope carrying the bound name.
        var scopedItemIds = condition.ItemScope == ItemScope.Subset
            ? new HashSet<int>(condition.ScopeItems.Select(s => s.ItemId))
            : new HashSet<int>(ctx.Props.Values.Select(p => p.ItemId));

        var matching = ctx.Props
            .Where(kv => scopedItemIds.Contains(kv.Value.ItemId) && kv.Value.Name == condition.PropertyName)
            .ToList();

        if (matching.Count == 0)
        {
            // Entire scope lacks the property: permanently 0, never passes (spec FR-2.3).
            return (0, false);
        }

        var type = matching[0].Value.Type;
        var mode = EffectiveMode(condition, type);
        var rowIds = matching.Select(kv => kv.Key).ToHashSet();

        double value;
        switch (type)
        {
            case PropertyType.Boolean:
                // EverTrue: count of in-scope rows true on at least one day in the range.
                value = rowIds.Count(pid => ctx.Facts.Any(f =>
                    f.PropertyId == pid && f.Day >= from && f.Day <= to && f.Bool == true));
                break;

            case PropertyType.Numeric:
                var weighted = ctx.Facts
                    .Where(f => rowIds.Contains(f.PropertyId) && f.Day >= from && f.Day <= to)
                    .Select(f => (f.Num ?? 0) * f.BaseRate)
                    .ToList();
                value = mode switch
                {
                    AggregationMode.Sum => weighted.Sum(),
                    AggregationMode.Avg => weighted.Count == 0 ? 0 : weighted.Average(),
                    AggregationMode.Max => weighted.Count == 0 ? 0 : weighted.Max(),
                    AggregationMode.Min => weighted.Count == 0 ? 0 : weighted.Min(),
                    AggregationMode.Latest => weighted.Count == 0 ? 0 : weighted[^1],
                    _ => weighted.Sum(),
                };
                break;

            default: // List
                value = rowIds.Sum(pid =>
                {
                    var rows = ctx.Facts
                        .Where(f => f.PropertyId == pid && f.Day >= from && f.Day <= to && f.Entries != null)
                        .ToList();
                    if (mode == AggregationMode.Latest)
                    {
                        // Entries of this item's most recent punch in the range.
                        var lastPunchId = rows.Select(f => f.PunchId).DefaultIfEmpty(-1).Last();
                        return rows.Where(f => f.PunchId == lastPunchId)
                            .SelectMany(f => f.Entries!).Distinct(StringComparer.Ordinal).Count();
                    }

                    // UnionDistinct: distinct entries across the whole window for this item,
                    // regardless of item_uniqueness (per_day duplicates add no new distinct
                    // string — spec RC-5).
                    return rows.SelectMany(f => f.Entries!).Distinct(StringComparer.Ordinal).Count();
                });
                break;
        }

        return (value, value >= condition.Threshold);
    }

    private static (double Value, bool Passed) EvaluateComposite(
        Criterion criterion, DateOnly from, DateOnly to, EvalContext ctx,
        Dictionary<int, (double Value, bool Passed)> memo)
    {
        var composite = criterion.Composite
            ?? throw new HabitException(422, HabitErrorCodes.InvalidTarget, $"Criterion '{criterion.Name}' has no composite definition.");

        var operands = composite.Operands
            .OrderBy(o => o.Order)
            .Select(o => ctx.CriteriaById.TryGetValue(o.OperandCriterionId, out var c) ? c : null)
            .Where(c => c != null)
            .Cast<Criterion>()
            .ToList();

        var results = operands.Select(o => Evaluate(o, from, to, ctx, memo)).ToList();
        var passing = results.Count(r => r.Passed);

        if (composite.Operator == CompositeOperator.Not)
        {
            var operandPassed = results.Count > 0 && results[0].Passed;
            return (operandPassed ? 0 : 1, !operandPassed);
        }

        var passed = composite.Operator == CompositeOperator.And
            ? passing == operands.Count && operands.Count > 0
            : passing >= 1;

        return (passing, passed);
    }

    /// <summary>
    /// Daily mode (root success_type = daily) applies to BOTH condition and composite roots.
    /// A day passes when the ROOT EVALUATES TRUE against its own rule — a condition root
    /// compares the day's aggregate with its own threshold; a composite root contributes
    /// the 1/0 pass bit (never the operand count — that would conflate AND with OR).
    /// </summary>
    private static bool RootUsesDayCount(Criterion root) => root.SuccessType == SuccessType.Daily;

    /// <summary>
    /// True when the root subtree contains a condition with a non-monotonic aggregation mode
    /// (avg/min/latest) — its aggregate can fall back below the threshold after having
    /// reached it, so the cycle pass must be latched by scanning per-prefix days.
    /// </summary>
    private static bool ContainsNonMonotonicAggregation(Criterion criterion, EvalContext ctx)
    {
        if (criterion.CriterionType == CriterionType.Condition)
        {
            var condition = criterion.Condition;
            if (condition is null)
            {
                return false;
            }
            var type = ctx.Props.Values.FirstOrDefault(p => p.Name == condition.PropertyName)?.Type;
            if (type is null)
            {
                return false;
            }
            return EffectiveMode(condition, type.Value)
                is AggregationMode.Avg or AggregationMode.Min or AggregationMode.Latest;
        }

        return criterion.Composite?.Operands
            .Select(o => ctx.CriteriaById.TryGetValue(o.OperandCriterionId, out var c) ? c : null)
            .Where(c => c != null)
            .Any(c => ContainsNonMonotonicAggregation(c!, ctx)) ?? false;
    }

    // ── Progress ────────────────────────────────────────────────────────────────────

    public async Task<ProgressOutDto> BuildProgressAsync(Habit habit, DateOnly today, CancellationToken ct = default)
    {
        var (from, to) = ComputeCycleWindow(habit, today);

        var (criteria, props) = await LoadGraphAsync(habit.Id, ct);
        var aggTo = AggregateTo(to, today);
        var facts = await LoadFactsAsync(habit.Id, from, aggTo, ct);
        var ctx = new EvalContext
        {
            CriteriaById = criteria.ToDictionary(c => c.Id),
            Props = props,
            Facts = facts,
        };

        var root = criteria.FirstOrDefault(c => c.IsRoot)
            ?? throw new HabitException(422, HabitErrorCodes.NoRootCriterion, $"Habit '{habit.Name}' has no root criterion.");

        // Aggregate only through today (future days in the window hold no punches anyway).
        var hasRange = from <= aggTo;

        // Non-root criteria over the cycle window.
        var nonRootDtos = new List<CriterionProgressOutDto>();
        foreach (var c in criteria.Where(c => !c.IsRoot))
        {
            var (value, passed) = hasRange
                ? Evaluate(c, from, aggTo, ctx, new Dictionary<int, (double, bool)>())
                : (0d, false);
            nonRootDtos.Add(BuildProgressDto(c, value, passed, ctx, includeRootFields: false));
        }

        var dayCount = RootUsesDayCount(root);
        double rootValue;
        bool rootPassed;
        int? successfulDays = null;
        double? currentDayValue = null;

        if (dayCount)
        {
            // Daily mode: a day passes when the root evaluates true with only that day's
            // punches; the cycle succeeds when successful days reach cycle_target.
            var days = 0;
            if (hasRange)
            {
                for (var d = from; d <= aggTo; d = d.AddDays(1))
                {
                    var (_, passed) = Evaluate(root, d, d, ctx, new Dictionary<int, (double, bool)>());
                    if (passed)
                    {
                        days++;
                    }
                }
            }

            successfulDays = days;
            rootValue = days;
            rootPassed = days >= (root.CycleTarget ?? 1);

            var todayActive = habit.State == HabitState.Active
                && today >= habit.StartDate
                && (!habit.EndDate.HasValue || today <= habit.EndDate.Value)
                && today >= from && (to is null || today <= to.Value);
            if (todayActive)
            {
                var (v, passed) = Evaluate(root, today, today, ctx, new Dictionary<int, (double, bool)>());
                // Condition root shows the day's aggregate; composite root shows the pass bit.
                currentDayValue = root.CriterionType == CriterionType.Condition ? v : (passed ? 1.0 : 0.0);
            }
        }
        else
        {
            var (value, passed) = hasRange ? Evaluate(root, from, aggTo, ctx, new Dictionary<int, (double, bool)>()) : (0d, false);
            rootValue = value;
            rootPassed = hasRange && passed;

            // Cumulative pass is latched within the cycle (spec): for non-monotonic
            // aggregations an early reach may have fallen back — scan per-prefix days.
            if (!rootPassed && hasRange && ContainsNonMonotonicAggregation(root, ctx))
            {
                for (var d = from; d <= aggTo; d = d.AddDays(1))
                {
                    var (_, prefixPassed) = Evaluate(root, from, d, ctx, new Dictionary<int, (double, bool)>());
                    if (prefixPassed)
                    {
                        rootPassed = true;
                        break;
                    }
                }
            }
        }

        var rootDto = BuildProgressDto(root, rootValue, rootPassed, ctx, includeRootFields: true);
        rootDto = rootDto with { SuccessfulDays = successfulDays, CurrentDayValue = currentDayValue };

        return new ProgressOutDto
        {
            CycleFrom = from,
            CycleTo = to,
            RootCriterion = rootDto,
            Criteria = nonRootDtos,
        };
    }

    private static CriterionProgressOutDto BuildProgressDto(
        Criterion c, double value, bool passed, EvalContext ctx, bool includeRootFields)
    {
        var dto = new CriterionProgressOutDto
        {
            CriterionId = c.Id,
            Name = c.Name,
            IsRoot = c.IsRoot,
            CriterionType = c.CriterionType,
            Passed = passed,
            CurrentValue = value,
        };

        if (c.CriterionType == CriterionType.Condition && c.Condition != null)
        {
            var type = ctx.Props.Values.FirstOrDefault(p => p.Name == c.Condition.PropertyName)?.Type;
            dto = dto with
            {
                PropertyName = c.Condition.PropertyName,
                AggregationMode = type is null ? c.Condition.AggregationMode : EffectiveMode(c.Condition, type.Value),
                Threshold = c.Condition.Threshold,
            };
        }
        else if (c.Composite != null)
        {
            var operandIds = c.Composite.Operands.OrderBy(o => o.Order).Select(o => o.OperandCriterionId).ToList();
            dto = dto with
            {
                Operator = c.Composite.Operator,
                OperandIds = operandIds,
                Threshold = c.Composite.Operator == CompositeOperator.And ? operandIds.Count : 1,
            };
        }

        if (includeRootFields)
        {
            dto = dto with
            {
                SuccessType = c.SuccessType,
                // Daily mode exposes the stored cycle_target; cumulative targets are derived
                // (the root's own threshold / required-operand count above) — never stored.
                CycleTarget = c.SuccessType == SuccessType.Daily ? c.CycleTarget : null,
            };
        }

        return dto;
    }

    // ── Per-property aggregates (for ItemOutDto) ────────────────────────────────────

    /// <summary>
    /// Per-property current-cycle and today aggregates keyed by property id.
    /// boolean cycle value = days true; numeric = sum(value × base_rate);
    /// list = distinct-entry count over the whole window for that item. todayValue is
    /// null when nothing was punched for the property today (boolean false today yields
    /// 0.0, not null).
    /// </summary>
    public async Task<Dictionary<int, (double CurrentCycleValue, double? TodayValue)>> ComputePropertyAggregatesAsync(
        Habit habit, DateOnly today, CancellationToken ct = default)
    {
        var (from, to) = ComputeCycleWindow(habit, today);
        var aggTo = AggregateTo(to, today);

        var props = await _db.ItemProperties
            .Where(p => p.HabitId == habit.Id)
            .ToListAsync(ct);
        var facts = await LoadFactsAsync(habit.Id, from, aggTo, ct);

        var result = new Dictionary<int, (double, double?)>();
        foreach (var p in props)
        {
            var propFacts = facts.Where(f => f.PropertyId == p.Id).ToList();
            var inRange = from <= aggTo
                ? propFacts.Where(f => f.Day >= from && f.Day <= aggTo).ToList()
                : new List<PunchFact>();
            var todayFacts = propFacts.Where(f => f.Day == today).ToList();

            double cycleValue;
            double? todayValue;

            switch (p.PropertyType)
            {
                case PropertyType.Boolean:
                    cycleValue = inRange.Any(f => f.Bool == true)
                        ? inRange.Where(f => f.Bool == true).Select(f => f.Day).Distinct().Count()
                        : 0;
                    todayValue = todayFacts.Count == 0
                        ? null
                        : (todayFacts.Any(f => f.Bool == true) ? 1.0 : 0.0);
                    break;

                case PropertyType.Numeric:
                    var rate = p.BaseRate ?? 1.0;
                    cycleValue = inRange.Sum(f => (f.Num ?? 0) * rate);
                    todayValue = todayFacts.Count == 0 ? null : todayFacts.Sum(f => (f.Num ?? 0) * rate);
                    break;

                default: // List
                    cycleValue = inRange.Where(f => f.Entries != null)
                        .SelectMany(f => f.Entries!).Distinct(StringComparer.Ordinal).Count();
                    todayValue = todayFacts.Count == 0
                        ? null
                        : todayFacts.Where(f => f.Entries != null).SelectMany(f => f.Entries!).Distinct(StringComparer.Ordinal).Count();
                    break;
            }

            result[p.Id] = (cycleValue, todayValue);
        }

        return result;
    }

    // ── History ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-day aggregated history: EVERY day in the requested range that falls inside the
    /// habit's active window (and is not in the future) gets a row — punch-less days
    /// appear with an empty session list and their empty-data outcome (spec FR-3.4;
    /// NOT-style criteria depend on this). Rows are newest-first. Everything derives from
    /// current punches and current criteria; no cycle is snapshotted or frozen.
    /// Cumulative is_successful latches on the first day the root passed within the cycle.
    /// The range end is additionally clamped at deactivation: no derived outcomes exist
    /// for days at or after <c>DeactivatedDate</c> (the habit's last active day is the day
    /// before it, spec FR-2.2/FR-2.4).
    /// Punch FACTS load from the start of the cycle containing <c>rangeFrom</c>, not from
    /// rangeFrom itself: every day row evaluates the cumulative roots over
    /// <c>[cycleFrom, day]</c>, and cycleFrom can precede the requested window — a
    /// mid-cycle start must still see the earlier punches of its own cycle.
    /// </summary>
    public async Task<List<DayHistoryOutDto>> BuildHistoryAsync(
        Habit habit, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct = default)
    {
        var rangeFrom = Max(from ?? habit.StartDate, habit.StartDate);

        // Active-window end: habit end / today, further capped at the last active day
        // (DeactivatedDate − 1) for a deactivated habit — days after deactivation never
        // appear, and a punch-less post-deactivation day cannot advertise an achieved
        // NOT-style outcome either.
        var endCap = Min(habit.EndDate ?? today, today, today);
        if (habit.State == HabitState.Inactive && habit.DeactivatedDate is { } deactivated)
        {
            var lastActive = deactivated.AddDays(-1);
            if (lastActive < endCap)
            {
                endCap = lastActive;
            }
        }
        var effectiveTo = Min(to ?? endCap, endCap, today);

        var (criteria, props) = await LoadGraphAsync(habit.Id, ct);
        if (criteria.Count == 0 || rangeFrom > effectiveTo)
        {
            return new List<DayHistoryOutDto>();
        }

        // Facts span back to the cycle start of the first emitted day so cumulative
        // aggregates over [cycleFrom, day] (below) can see punches before the window.
        // ComputeCycleWindow's From never exceeds its anchor, so this is ≤ rangeFrom.
        var factsFrom = ComputeCycleWindow(habit, rangeFrom).From;
        var facts = await LoadFactsAsync(habit.Id, factsFrom, effectiveTo, ct);
        var punches = await _db.Punches
            .Where(p => p.HabitId == habit.Id && p.PunchDate >= rangeFrom && p.PunchDate <= effectiveTo)
            .Include(p => p.Values)
            .ToListAsync(ct);

        var root = criteria.FirstOrDefault(c => c.IsRoot);
        var ctx = new EvalContext
        {
            CriteriaById = criteria.ToDictionary(c => c.Id),
            Props = props,
            Facts = facts,
        };

        // Ascending evaluation (cycle latching needs chronological replay).
        var rows = new List<DayHistoryOutDto>();

        for (var day = rangeFrom; day <= effectiveTo; day = day.AddDays(1))
        {
            var (cycleFrom, cycleTo) = ComputeCycleWindow(habit, day);

            var dayResults = new List<CriterionDayResultOutDto>();
            bool? rootDayOutcome = null;

            foreach (var c in criteria)
            {
                double value;
                bool passed;

                if (c == root && root != null && RootUsesDayCount(root))
                {
                    // Daily mode (condition or composite root): pass/fail of the root on this
                    // single day — condition vs its own threshold, composite as the pass bit.
                    (value, passed) = Evaluate(c, day, day, ctx, new Dictionary<int, (double, bool)>());
                }
                else if (c == root && root != null && root.SuccessType == SuccessType.Cumulative)
                {
                    // Cumulative root: running aggregate from the cycle start through this day.
                    (value, passed) = Evaluate(c, cycleFrom, day, ctx, new Dictionary<int, (double, bool)>());
                    if (!passed && ContainsNonMonotonicAggregation(root, ctx))
                    {
                        // Cumulative pass is LATCHED — it cannot be revoked (spec). With a
                        // non-monotonic aggregation the aggregate can have fallen back below
                        // the threshold by `day`; replay the cycle's prefixes to find the
                        // first day the root passed. The scan starts at cycleFrom (facts load
                        // from there), so latching also holds when the history window opens
                        // on a mid-cycle day that never saw its cycle's pass day.
                        for (var d = cycleFrom; d <= day && !passed; d = d.AddDays(1))
                        {
                            passed = Evaluate(root, cycleFrom, d, ctx, new Dictionary<int, (double, bool)>()).Passed;
                        }
                    }
                }
                else
                {
                    // Single-day evaluation for all non-root criteria.
                    (value, passed) = Evaluate(c, day, day, ctx, new Dictionary<int, (double, bool)>());
                }

                if (c == root)
                {
                    rootDayOutcome = passed;
                }

                dayResults.Add(new CriterionDayResultOutDto
                {
                    CriterionId = c.Id,
                    Name = c.Name,
                    Passed = passed,
                    CurrentValue = value,
                });
            }

            var dayPunches = punches
                .Where(p => p.PunchDate == day)
                .OrderBy(p => p.PunchedAt)
                .Select(p => MapPunch(p, props))
                .ToList();

            rows.Add(new DayHistoryOutDto
            {
                Date = day,
                CycleFrom = cycleFrom,
                CycleTo = cycleTo,
                IsSuccessful = rootDayOutcome ?? false,
                Criteria = dayResults,
                Punches = dayPunches,
            });
        }

        rows.Reverse(); // newest first
        return rows;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a >= b ? a : b;

    private static DateOnly Min(DateOnly a, DateOnly b, DateOnly c)
    {
        var m = a <= b ? a : b;
        return m <= c ? m : c;
    }

    internal static PunchOutDto MapPunch(Punch punch, Dictionary<int, PropMeta> props)
    {
        return new PunchOutDto
        {
            Id = punch.Id,
            HabitId = punch.HabitId,
            ItemId = punch.ItemId,
            PunchedAt = punch.PunchedAt,
            PunchDate = punch.PunchDate,
            CreatedAt = punch.CreatedAt,
            Values = punch.Values.Select(v => new PropertyValueOutDto
            {
                PropertyId = v.PropertyId,
                PropertyName = props.TryGetValue(v.PropertyId, out var m) ? m.Name : string.Empty,
                PropertyType = props.TryGetValue(v.PropertyId, out var mt) ? mt.Type : PropertyType.Boolean,
                BoolValue = v.BoolValue,
                NumValue = v.NumValue,
                ListEntries = v.ListEntries == null ? null : ParseEntries(v.ListEntries),
            }).ToList(),
        };
    }
}
