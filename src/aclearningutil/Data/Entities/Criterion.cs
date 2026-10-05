namespace aclearningutil.Data.Entities;

/// <summary>
/// A named success rule for a habit. Exactly one criterion per habit is the root.
/// SuccessType and CycleTarget live only on the root; they are null on non-root criteria.
/// A criterion is either a condition (aggregates a property) or a composite (AND/OR/NOT over
/// other criteria); the two shapes are stored in the 1:1 detail tables. The criterion
/// graph must remain a DAG — cycles are rejected at write time.
/// </summary>
public class Criterion
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>Redundant tenant key for fast filtering.</summary>
    public string OwnerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public CriterionType CriterionType { get; set; }
    public bool IsRoot { get; set; }

    /// <summary>Root only. The habit's success mode (spec FR: root carries success_type).</summary>
    public SuccessType? SuccessType { get; set; }

    /// <summary>
    /// Root, Daily mode only: the number of successful days the cycle requires (&gt; 0).
    /// Cumulative roots keep this NULL — their target is derived from the tree.
    /// </summary>
    public double? CycleTarget { get; set; }

    public DateTime CreatedAt { get; set; }

    public Habit? Habit { get; set; }
    public CriterionCondition? Condition { get; set; }
    public CriterionComposite? Composite { get; set; }
}

/// <summary>Aggregation definition for a condition criterion.</summary>
public class CriterionCondition
{
    public int Id { get; set; }

    /// <summary>1:1 with <see cref="Criterion"/>.</summary>
    public int CriterionId { get; set; }

    /// <summary>
    /// The NAME of the property this criterion targets (spec: conditions bind names, never
    /// property-row ids). Aggregation matches same-named properties on every item within
    /// <see cref="ItemScope"/>; the habit-wide rule that same-named properties share one
    /// type keeps the aggregation well-defined. Deleting property rows never breaks this
    /// reference — if the name disappears from the whole scope the criterion simply
    /// never passes (spec FR-2.3).
    /// </summary>
    public string PropertyName { get; set; } = string.Empty;

    public ItemScope ItemScope { get; set; }

    /// <summary>
    /// How values combine across the cycle. Null = the property type's default
    /// (EverTrue / Sum / UnionDistinct).
    /// </summary>
    public AggregationMode? AggregationMode { get; set; }

    /// <summary>Aggregated value must be &gt;= this threshold for the criterion to pass. &gt; 0.</summary>
    public double Threshold { get; set; }

    public Criterion? Criterion { get; set; }
    public List<CriterionConditionScopeItem> ScopeItems { get; set; } = new();
}

/// <summary>Item membership rows for a condition criterion with ItemScope.Subset.</summary>
public class CriterionConditionScopeItem
{
    /// <summary>CLR-side key for the scope row's owner; the physical column keeps its
    /// frozen legacy name "CriterionLeafId" (pinned in <c>AppDbContext</c>) so existing
    /// SQLite files and the schema-bootstrap raw SQL stay valid.</summary>
    public int CriterionConditionId { get; set; }
    public int ItemId { get; set; }

    public CriterionCondition? CriterionCondition { get; set; }
    public HabitItem? Item { get; set; }
}

/// <summary>Composition definition for a composite criterion.</summary>
public class CriterionComposite
{
    public int Id { get; set; }

    /// <summary>1:1 with <see cref="Criterion"/>.</summary>
    public int CriterionId { get; set; }

    public CompositeOperator Operator { get; set; }

    public Criterion? Criterion { get; set; }
    public List<CriterionCompositeOperand> Operands { get; set; } = new();
}

/// <summary>Operand edges of the criterion DAG. NOT: exactly one; AND/OR: at least two.</summary>
public class CriterionCompositeOperand
{
    public int CompositeId { get; set; }

    /// <summary>Delete-restricted: an in-use operand cannot be deleted (criterionInUse).</summary>
    public int OperandCriterionId { get; set; }

    public int Order { get; set; }

    public CriterionComposite? Composite { get; set; }
    public Criterion? OperandCriterion { get; set; }
}
