using aclearningutil.Data.Entities;

namespace aclearningutil.Models;

// ── Habit tracking request DTOs (see docs/design-habit-api.md) ─────────────────────
// Enums serialize on the wire as the spec's lowercase/snake values via the global
// JsonStringEnumConverter(SnakeCaseLower) registered in Program.cs.

/// <summary>Create a property definition. PropertyType and ItemUniqueness are immutable after creation.</summary>
public sealed record PropertyCreateDto
{
    public required string Name { get; init; }

    /// <summary>Required. A null here means the body omitted <c>propertyType</c> → 422
    /// <c>missingPropertyType</c> (a non-nullable enum would silently bind an omission to
    /// <c>boolean</c> = 0 — same pattern as the habit <c>cycle</c> field).</summary>
    public PropertyType? PropertyType { get; init; }

    /// <summary>Numeric only; must be &gt; 0 when set.</summary>
    public double? BaseRate { get; init; }

    /// <summary>Required for list properties; forbidden on others.</summary>
    public ItemUniqueness? ItemUniqueness { get; init; }

    public int Order { get; init; }
}

public sealed record ItemCreateDto
{
    public required string Name { get; init; }
    public int Order { get; init; }

    /// <summary>At least one property is required.</summary>
    public List<PropertyCreateDto> Properties { get; init; } = new();
}

public sealed record ItemUpdateDto
{
    public required string Name { get; init; }
    public int Order { get; init; }
}

public sealed record PropertyUpdateDto
{
    public required string Name { get; init; }

    /// <summary>Numeric only; must be &gt; 0 when set.</summary>
    public double? BaseRate { get; init; }

    public int Order { get; init; }
}

/// <summary>
/// Create a criterion. Leaves ALWAYS reference the targeted property by
/// <see cref="PropertyName"/> (a condition binds the name, never a property-row id —
/// deleting property rows must not break criteria). During habit creation (name-based
/// phase) scope items are referenced by <see cref="ScopeItemNames"/> and composite
/// operands by <see cref="OperandCriterionNames"/>; after creation (standalone POST /
/// individual PUT) those use id-based fields instead. Providing both forms for the same
/// purpose is rejected (invalidPropertyType); an unresolvable name rolls the whole
/// creation transaction back (unknownOperandName).
/// </summary>
public sealed record CriterionCreateDto
{
    public required string Name { get; init; }
    public bool IsRoot { get; init; }

    /// <summary>Required. A null here means the body omitted <c>criterionType</c> → 422
    /// <c>missingCriterionType</c> (a non-nullable enum would silently bind an omission to
    /// <c>condition</c> = 0 — same pattern as the habit <c>cycle</c> field).</summary>
    public CriterionType? CriterionType { get; init; }

    // Condition — always name-based.
    public string? PropertyName { get; init; }

    /// <summary>Optional; null = the property type's default (EverTrue/Sum/UnionDistinct).</summary>
    public AggregationMode? AggregationMode { get; init; }

    public ItemScope? ItemScope { get; init; }
    public List<string>? ScopeItemNames { get; init; }
    public List<int>? ScopeItemIds { get; init; }
    public double? Threshold { get; init; }

    // Composite — id-based / name-based.
    public CompositeOperator? Operator { get; init; }
    public List<string>? OperandCriterionNames { get; init; }
    public List<int>? OperandCriterionIds { get; init; }

    // Root criterion only.
    public SuccessType? SuccessType { get; init; }

    /// <summary>Daily mode only (successful days required); must be null for a cumulative root.</summary>
    public double? CycleTarget { get; init; }
}

/// <summary>
/// Update a criterion. CriterionType is immutable BY CONSTRUCTION — the payload carries
/// no type field; converting a condition into a composite (or back) requires delete + recreate.
/// Operands/scope use ids — the entities already exist — while the condition keeps binding its
/// property by name. Setting IsRoot=true atomically clears the previous root; clearing
/// the current root without designating another is rejected (rootRequired).
/// </summary>
public sealed record CriterionUpdateDto
{
    public required string Name { get; init; }
    public bool IsRoot { get; init; }

    public string? PropertyName { get; init; }
    public AggregationMode? AggregationMode { get; init; }
    public ItemScope? ItemScope { get; init; }
    public List<int>? ScopeItemIds { get; init; }
    public double? Threshold { get; init; }

    public CompositeOperator? Operator { get; init; }
    public List<int>? OperandCriterionIds { get; init; }

    public SuccessType? SuccessType { get; init; }

    /// <summary>Daily mode only (successful days required); must be null for a cumulative root.</summary>
    public double? CycleTarget { get; init; }
}

public sealed record HabitCreateDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Required. A null here means the body omitted <c>cycle</c> → 422 missingCycle
    /// (a non-nullable enum would silently bind an omission to Daily=0).</summary>
    public HabitCycle? Cycle { get; init; }
    public DateOnly StartDate { get; init; }

    /// <summary>Last active day, inclusive; must be &gt;= StartDate when set.</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Min 1 — created atomically with the habit.</summary>
    public List<ItemCreateDto> Items { get; init; } = new();

    /// <summary>Min 1, exactly one with IsRoot=true — created atomically with the habit.</summary>
    public List<CriterionCreateDto> Criteria { get; init; } = new();
}

/// <summary>Update basic habit fields. State is not editable — use POST .../Deactivate.</summary>
public sealed record HabitUpdateDto
{
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>Rejected (structuralChangeBlocked) when punch records exist.
    /// Required — an omitted <c>cycle</c> is 422 missingCycle, never a silent Daily.</summary>
    public HabitCycle? Cycle { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
}

/// <summary>Exactly one of the three values must be set, matching the property's type.</summary>
public sealed record PropertyValueCreateDto
{
    public int PropertyId { get; init; }
    public bool? BoolValue { get; init; }

    /// <summary>Numeric properties; must be &gt; 0.</summary>
    public double? NumValue { get; init; }

    /// <summary>List properties; min 1 non-empty entry.</summary>
    public List<string>? ListEntries { get; init; }
}

public sealed record PunchCreateDto
{
    /// <summary>One entry per property being recorded in this session; min 1.</summary>
    public List<PropertyValueCreateDto> Values { get; init; } = new();

    /// <summary>
    /// Day the session is recorded for; null = server's today. Must be on or before the
    /// server's today and inside the habit's active window, otherwise 422
    /// <c>outOfWindow</c>. FR-3.3 (revised spec) gates on the PUNCH DATE, not "today": an
    /// active habit accepts back-fills for past in-window days even after today has
    /// passed endDate; deactivation or a future/in-window-less date rejects (spec FR-3.1/FR-3.3).
    /// </summary>
    public DateOnly? PunchDate { get; init; }
}

/// <summary>
/// Replace this session's recorded values. Boolean values upsert the day's single row;
/// numeric/list values replace this punch's rows. per_cycle uniqueness re-checks against
/// other sessions, excluding the punch being edited.
/// </summary>
public sealed record PunchUpdateDto
{
    public List<PropertyValueCreateDto> Values { get; init; } = new();
}

/// <summary>
/// Invite one user to a habit (owner-only). The invitee gains READ-ONLY visibility of
/// the habit definition AND its full punch history through api/SharedHabits — no other
/// user sees anything. Both fields come from the ID provider's user search
/// (acidserver /api/users/search); the owner's display name is NOT client-sent — the
/// server snapshots it from the caller's own "name" claim.
/// </summary>
public sealed record ShareGrantCreateDto
{
    public required string GranteeUserId { get; init; }
    public required string GranteeUserName { get; init; }
}
