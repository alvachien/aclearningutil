namespace aclearningutil.Data.Entities;

/// <summary>How often the habit goal resets.</summary>
public enum HabitCycle
{
    Daily,
    Weekly,
    Monthly,

    /// <summary>
    /// Whole-period cycle: the habit forms a single cycle from StartDate to
    /// EndDate (open-ended to today when no end date) — the goal never resets
    /// mid-habit. Wire value "whole".
    /// </summary>
    Whole,
}

/// <summary>Habit lifecycle state. Deactivation is irreversible — there is no reactivation path.</summary>
public enum HabitState
{
    Active,
    Inactive,
}

/// <summary>Punchable property kind (see functional spec "Property types").</summary>
public enum PropertyType
{
    Boolean,
    Numeric,
    List,
}

/// <summary>Deduplication scope for list properties.</summary>
public enum ItemUniqueness
{
    PerDay,
    PerCycle,
}

/// <summary>A criterion either aggregates a property (condition) or combines other criteria (composite).</summary>
public enum CriterionType
{
    Condition,
    Composite,
}

/// <summary>Which items contribute to a condition criterion's aggregation.</summary>
public enum ItemScope
{
    All,
    Subset,
}

/// <summary>Composition rule for composite criteria.</summary>
public enum CompositeOperator
{
    And,
    Or,
    Not,
}

/// <summary>
/// How the root criterion determines cycle success (spec "Success types").
/// Carried by the root criterion only: Daily re-evaluates the tree every
/// calendar day and counts successful days against CycleTarget; Cumulative
/// evaluates aggregated values once over the whole cycle (its target is
/// derived from the tree, never stored).
/// </summary>
public enum SuccessType
{
    Daily,
    Cumulative,
}

/// <summary>
/// How a condition's punch values combine across the cycle (cumulative evaluation;
/// day-count evaluation uses the same aggregation restricted to one day).
/// Defaults per property type: Boolean → EverTrue; Numeric → Sum; List → UnionDistinct.
/// </summary>
public enum AggregationMode
{
    // Boolean (ever_true)
    EverTrue,

    // Numeric
    Sum,
    Avg,
    Max,
    Min,
    Latest,

    // List
    UnionDistinct,
}
