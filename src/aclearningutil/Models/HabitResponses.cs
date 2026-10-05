using aclearningutil.Data.Entities;

namespace aclearningutil.Models;

// ── Habit tracking response DTOs (see docs/design-habit-api.md) ─────────────────────

/// <summary>
/// Per-property aggregation for the current cycle on a specific item, used for display.
/// </summary>
public sealed record PropertyOutDto
{
    public required int Id { get; init; }
    public required int ItemId { get; init; }
    public required string Name { get; init; }
    public PropertyType PropertyType { get; init; }
    public double? BaseRate { get; init; }
    public ItemUniqueness? ItemUniqueness { get; init; }
    public int Order { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Aggregated value for this property on this item, current cycle, by type:
    /// boolean — count of days this cycle it was true; numeric — sum(value × baseRate);
    /// list — count of distinct entries this cycle.
    /// </summary>
    public double CurrentCycleValue { get; init; }

    /// <summary>
    /// Today's contribution. Boolean: 1.0 true / 0.0 false. Null only when nothing was
    /// punched for this property today at all.
    /// </summary>
    public double? TodayValue { get; init; }
}

public sealed record ItemOutDto
{
    public required int Id { get; init; }
    public required int HabitId { get; init; }
    public required string Name { get; init; }
    public int Order { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>True if any punch references this item across all cycles (computed at query time).</summary>
    public bool HasPunches { get; init; }

    public List<PropertyOutDto> Properties { get; init; } = new();
}

/// <summary>Full criterion definition (as stored).</summary>
public sealed record CriterionOutDto
{
    public required int Id { get; init; }
    public required int HabitId { get; init; }
    public required string Name { get; init; }
    public bool IsRoot { get; init; }
    public CriterionType CriterionType { get; init; }

    /// <summary>Condition: the bound property NAME (never a property-row id).</summary>
    public string? PropertyName { get; init; }

    /// <summary>Condition: the STORED aggregation mode; null = the property type's default
    /// (resolution to the effective mode happens on the progress payload).</summary>
    public AggregationMode? AggregationMode { get; init; }

    public ItemScope? ItemScope { get; init; }
    public List<int>? ScopeItemIds { get; init; }
    public double? Threshold { get; init; }

    public CompositeOperator? Operator { get; init; }
    public List<int>? OperandCriterionIds { get; init; }

    /// <summary>Root only: the habit's success mode.</summary>
    public SuccessType? SuccessType { get; init; }

    /// <summary>Root, daily mode only; null on cumulative roots (derived target).</summary>
    public double? CycleTarget { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Criterion pass/fail and progress, evaluated over the current cycle window.
/// Fields are populated per criterion type (condition vs composite) and role (root vs not).
/// </summary>
public sealed record CriterionProgressOutDto
{
    public required int CriterionId { get; init; }
    public required string Name { get; init; }
    public bool IsRoot { get; init; }
    public CriterionType CriterionType { get; init; }
    public bool Passed { get; init; }

    // Condition criteria only.
    public string? PropertyName { get; init; }

    /// <summary>Effective aggregation mode (null input resolved to the property type's default).</summary>
    public AggregationMode? AggregationMode { get; init; }

    /// <summary>
    /// Condition: aggregated value this cycle per the criterion's aggregation mode (semantics by
    /// property type, see spec). Composite: count of operand criteria currently passing.
    /// </summary>
    public double? CurrentValue { get; init; }

    /// <summary>Condition: its threshold (pass = value &gt;= threshold). Composite: operands that must pass (AND: count; OR/NOT: 1).</summary>
    public double? Threshold { get; init; }

    // Root criterion only.
    public SuccessType? SuccessType { get; init; }

    /// <summary>Root, daily mode: successful days required. Cumulative roots: null — the target is derived (condition: threshold; composite: required passing operands).</summary>
    public double? CycleTarget { get; init; }

    /// <summary>Root (daily mode) only. Today's aggregated value (condition: day aggregate vs its own threshold; composite: 1.0/0.0 pass bit); null when today is outside the active window.</summary>
    public double? CurrentDayValue { get; init; }

    /// <summary>Root (daily mode) only. Successful days so far this cycle.</summary>
    public int? SuccessfulDays { get; init; }

    // Composite criteria only.
    public CompositeOperator? Operator { get; init; }
    public List<int>? OperandIds { get; init; }
}

/// <summary>Current-cycle progress for a habit.</summary>
public sealed record ProgressOutDto
{
    public required DateOnly CycleFrom { get; init; }

    /// <summary>Null for an open-ended whole cycle (spec FR-4.1 — clients render "ongoing"; never a sentinel).</summary>
    public DateOnly? CycleTo { get; init; }

    /// <summary>The root criterion — not duplicated in <see cref="Criteria"/>.</summary>
    public required CriterionProgressOutDto RootCriterion { get; init; }

    /// <summary>All non-root criteria in the habit.</summary>
    public List<CriterionProgressOutDto> Criteria { get; init; } = new();
}

public sealed record HabitOutDto
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public HabitCycle Cycle { get; init; }
    public required DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public HabitState State { get; init; }

    /// <summary>
    /// The <c>yyyy-MM-dd</c> date recorded when the habit was deactivated (spec FR-2.4);
    /// null while the habit is active (and for inactive rows predating the field).
    /// </summary>
    public DateOnly? DeactivatedDate { get; init; }

    /// <summary>True if any punch exists for this habit across all cycles (computed at query time).</summary>
    public bool HasPunches { get; init; }

    public DateTime CreatedAt { get; init; }

    public required ProgressOutDto Progress { get; init; }
}

/// <summary>One invitation of a habit (owner-facing management view).</summary>
public sealed record ShareGrantOutDto
{
    public required int Id { get; init; }
    public required string GranteeUserId { get; init; }
    public required string GranteeUserName { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// "Shared with me" gallery entry: a habit the caller was invited to, plus the owner's
/// display-name snapshot. The owner's identity claim (OwnerId) is NEVER serialized —
/// viewers see the name only.
/// </summary>
public sealed record SharedHabitOutDto
{
    public required HabitOutDto Habit { get; init; }
    public required string OwnerName { get; init; }
}

/// <summary>
/// Read-only detail of an invited habit: definition, items/properties and criteria with
/// values computed for the VIEWER's today (same aggregation the owner's own endpoints
/// run), plus the owner-name snapshot.
/// </summary>
public sealed record SharedHabitDetailOutDto
{
    public required HabitOutDto Habit { get; init; }
    public required string OwnerName { get; init; }
    public List<ItemOutDto> Items { get; init; } = new();
    public List<CriterionOutDto> Criteria { get; init; } = new();
}

public sealed record PropertyValueOutDto
{
    public required int PropertyId { get; init; }
    public required string PropertyName { get; init; }
    public PropertyType PropertyType { get; init; }
    public bool? BoolValue { get; init; }
    public double? NumValue { get; init; }
    public List<string>? ListEntries { get; init; }
}

public sealed record PunchOutDto
{
    public required int Id { get; init; }
    public required int HabitId { get; init; }
    public required int ItemId { get; init; }
    public DateTime PunchedAt { get; init; }
    public required DateOnly PunchDate { get; init; }
    public DateTime CreatedAt { get; init; }

    public List<PropertyValueOutDto> Values { get; init; } = new();
}

/// <summary>Per-criterion pass/fail and aggregated value for a single day.</summary>
public sealed record CriterionDayResultOutDto
{
    public required int CriterionId { get; init; }
    public required string Name { get; init; }
    public bool Passed { get; init; }

    /// <summary>Condition: aggregated value that day. Composite: count of operands passing that day.</summary>
    public double? CurrentValue { get; init; }
}

/// <summary>One day of punch history with criterion pass/fail state.</summary>
public sealed record DayHistoryOutDto
{
    public required DateOnly Date { get; init; }

    /// <summary>Cycle window this day belongs to (spec DayHistoryOut.cycle_from/cycle_to).</summary>
    public required DateOnly CycleFrom { get; init; }

    /// <summary>Cycle window end; null for an open-ended whole cycle.</summary>
    public DateOnly? CycleTo { get; init; }

    /// <summary>
    /// Daily mode: the root passed on this specific day.
    /// Cumulative mode: true on and after the day the root first passed within the cycle
    /// (the pass is latched). Derived from CURRENT punches and criteria for every cycle.
    /// </summary>
    public bool IsSuccessful { get; init; }

    public List<CriterionDayResultOutDto> Criteria { get; init; } = new();

    /// <summary>All punch sessions recorded this day, across all items (callers group by item).</summary>
    public List<PunchOutDto> Punches { get; init; } = new();
}
