using aclearningutil.Data.Entities;
using aclearningutil.Models;

namespace aclearningutil.Services;

/// <summary>
/// Shared business-rule validation for the habit-tracking API (docs/design-habit-api.md
/// § Validation &amp; Business Rules). Throws <see cref="HabitException"/> with the spec's
/// error codes; controllers translate those into ProblemDetails responses.
/// </summary>
public static class HabitRuleValidator
{
    // ── Server-side payload limits ──────────────────────────────────────────────────
    // SQLite does not enforce the max lengths declared in the model (docs/design-habit-api.md
    // § Data Model), so the write paths reject over-long / over-count payloads with 422.
    // The limits below mirror the design DDL where it states one, and are otherwise sane
    // caps for a single-request habit definition.

    /// <summary>Max length of habit / item / property / criterion / bound-property names.</summary>
    public const int MaxNameLength = 500;

    /// <summary>Max length of the habit description.</summary>
    public const int MaxDescriptionLength = 2000;

    /// <summary>Max items (or criteria) in one habit-create payload.</summary>
    public const int MaxItemsPerHabit = 100;

    /// <summary>Max properties on one item (create payload or nested habit create).</summary>
    public const int MaxPropertiesPerItem = 100;

    /// <summary>Max criteria in one habit-create payload.</summary>
    public const int MaxCriteriaPerHabit = 100;

    /// <summary>Max scope-item / operand references in one criterion payload.</summary>
    public const int MaxReferencesPerCriterion = 100;

    /// <summary>Max property values in one punch session.</summary>
    public const int MaxValuesPerPunch = 100;

    /// <summary>Max list entries in one punch value.</summary>
    public const int MaxEntriesPerValue = 100;

    /// <summary>Max characters of one list entry string.</summary>
    public const int MaxEntryLength = 1000;

    // ── Basic field rules ───────────────────────────────────────────────────────────

    public static void RequireName(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidName, $"{what} name is required.");
        }
        if (name.Length > MaxNameLength)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                $"{what} name must be at most {MaxNameLength} characters (got {name.Length}).");
        }
    }

    public static void RequireDescriptionLength(string? description)
    {
        if (description is not null && description.Length > MaxDescriptionLength)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                $"Description must be at most {MaxDescriptionLength} characters (got {description.Length}).");
        }
    }

    public static void ValidateDateRange(DateOnly startDate, DateOnly? endDate)
    {
        if (endDate.HasValue && endDate.Value < startDate)
        {
            throw HabitException.Unprocessable(
                HabitErrorCodes.InvalidDateRange, "endDate must be on or after startDate.");
        }
    }

    /// <summary>
    /// Resolves a nullable wire enum: an omitted <c>propertyType</c> is 422
    /// <c>missingPropertyType</c> — the DTO field is nullable precisely so the value can
    /// never silently bind to <c>boolean</c> = 0 (same pattern as <c>missingCycle</c>).
    /// </summary>
    public static PropertyType RequirePropertyType(PropertyType? propertyType, string? name = null)
    {
        if (propertyType is null)
        {
            var who = string.IsNullOrWhiteSpace(name) ? "A property" : $"Property '{name}'";
            throw HabitException.Unprocessable(HabitErrorCodes.MissingPropertyType,
                $"{who} requires a propertyType ('boolean', 'numeric' or 'list').");
        }

        return propertyType.Value;
    }

    /// <summary>
    /// Resolves a nullable wire enum: an omitted <c>criterionType</c> is 422
    /// <c>missingCriterionType</c> — never a silent <c>condition</c> = 0.
    /// </summary>
    public static CriterionType RequireCriterionType(CriterionType? criterionType, string? name = null)
    {
        if (criterionType is null)
        {
            var who = string.IsNullOrWhiteSpace(name) ? "A criterion" : $"Criterion '{name}'";
            throw HabitException.Unprocessable(HabitErrorCodes.MissingCriterionType,
                $"{who} requires a criterionType ('condition' or 'composite').");
        }

        return criterionType.Value;
    }

    /// <summary>Rejects duplicate entries inside one id reference list (scope items / operands).
    /// A duplicated id would hit the join-table PK and surface as a 500 instead of a 422.</summary>
    public static void RejectDuplicateReferences(IReadOnlyList<int>? ids, string what, string criterionName)
    {
        if (ids is null)
        {
            return;
        }

        var duplicate = ids.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateReference,
                $"Criterion '{criterionName}': {what} contains duplicate reference {duplicate.Key}.");
        }
    }

    /// <summary>Rejects duplicate entries inside one name reference list (payload phase).</summary>
    private static void RejectDuplicateReferences(IReadOnlyList<string>? names, string what, string criterionName)
    {
        if (names is null)
        {
            return;
        }

        var duplicate = names.GroupBy(x => x, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.DuplicateReference,
                $"Criterion '{criterionName}': {what} contains duplicate reference '{duplicate.Key}'.");
        }
    }

    /// <summary>Count cap for an id reference list.</summary>
    public static void ValidateReferenceCount(IReadOnlyList<int>? ids, string what, string criterionName)
    {
        if (ids is { Count: > MaxReferencesPerCriterion })
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"Criterion '{criterionName}': {what} must not exceed {MaxReferencesPerCriterion} references (got {ids.Count}).");
        }
    }

    /// <summary>Count cap for a name reference list.</summary>
    private static void ValidateReferenceCount(IReadOnlyList<string>? names, string what, string criterionName)
    {
        if (names is { Count: > MaxReferencesPerCriterion })
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"Criterion '{criterionName}': {what} must not exceed {MaxReferencesPerCriterion} references (got {names.Count}).");
        }
    }

    public static void ValidatePropertyDefinition(string name, PropertyType type, double? baseRate, ItemUniqueness? uniqueness)
    {
        RequireName(name, "Property");

        // Defense-in-depth for enum members that arrive outside the JSON pipeline (the wire
        // format already rejects unknown strings / integers — see the converter in Program.cs).
        if (!Enum.IsDefined(type))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Property '{name}': unknown property type '{(int)type}'.");
        }
        if (uniqueness.HasValue && !Enum.IsDefined(uniqueness.Value))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidItemUniqueness,
                $"Property '{name}': unknown itemUniqueness value '{(int)uniqueness.Value}'.");
        }

        switch (type)
        {
            case PropertyType.Numeric:
                if (baseRate.HasValue && (!double.IsFinite(baseRate.Value) || baseRate.Value <= 0))
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidBaseRate,
                        $"baseRate on property '{name}' must be a positive finite number.");
                }
                if (uniqueness.HasValue)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidItemUniqueness,
                        $"itemUniqueness must not be set on numeric property '{name}'.");
                }
                break;

            case PropertyType.List:
                if (!uniqueness.HasValue)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidItemUniqueness,
                        $"itemUniqueness is required for list property '{name}'.");
                }
                if (baseRate.HasValue)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidBaseRate,
                        $"baseRate only applies to numeric properties ('{name}').");
                }
                break;

            default: // Boolean
                if (uniqueness.HasValue)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidItemUniqueness,
                        $"itemUniqueness must not be set on boolean property '{name}'.");
                }
                if (baseRate.HasValue)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidBaseRate,
                        $"baseRate only applies to numeric properties ('{name}').");
                }
                break;
        }
    }

    public static void ValidateItemShape(ItemCreateDto item)
    {
        RequireName(item.Name, "Item");
        if (item.Properties.Count == 0)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.ItemWithoutProperties,
                $"Item '{item.Name}' must define at least one property.");
        }
        if (item.Properties.Count > MaxPropertiesPerItem)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"Item '{item.Name}' must not define more than {MaxPropertiesPerItem} properties (got {item.Properties.Count}).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in item.Properties)
        {
            var type = RequirePropertyType(p.PropertyType, p.Name);
            ValidatePropertyDefinition(p.Name, type, p.BaseRate, p.ItemUniqueness);
            if (!seen.Add(p.Name))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                    $"Duplicate property name '{p.Name}' within item '{item.Name}'.");
            }
        }
    }

    // ── Criterion rules ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Success-type / target placement rules shared by every criterion write path (spec
    /// "Success types"). The root carries an explicit <c>successType</c>; composite roots
    /// are valid in BOTH modes (in daily mode a day passes exactly when the root group
    /// passes — the evaluator uses the pass bit, never the operand count).
    /// </summary>
    public static void ValidateTargets(CriterionCreateDto c, HabitCycle cycle)
    {
        if (!c.IsRoot)
        {
            if (c.SuccessType.HasValue || c.CycleTarget.HasValue)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                    $"successType/cycleTarget are only valid on the root criterion ('{c.Name}').");
            }
            return;
        }

        if (c.SuccessType is null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                $"Root criterion '{c.Name}' requires successType ('daily' or 'cumulative').");
        }

        if (c.SuccessType == Data.Entities.SuccessType.Cumulative)
        {
            // The cumulative cycle target is a pure derived value (condition root: its threshold;
            // composite root: the required passing-operand count) — never input, never stored.
            if (c.CycleTarget.HasValue)
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                    $"A cumulative root criterion ('{c.Name}') must not carry cycleTarget — the target is derived from the tree.");
            }
            return;
        }

        // Daily (day-count) mode.
        if (c.CycleTarget is null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.MissingCycleTarget,
                $"Daily-mode root criterion '{c.Name}' requires cycleTarget (successful days needed).");
        }
        // !IsFinite first: NaN/Infinity never satisfy the integral comparison below.
        if (!double.IsFinite(c.CycleTarget.Value) || c.CycleTarget.Value <= 0
            || Math.Floor(c.CycleTarget.Value) != c.CycleTarget.Value)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                "cycleTarget must be a positive whole number of days in daily mode.");
        }

        // Whole cycles are unbounded: the span is user-defined, so "successful days" is not
        // capped by a calendar week/month.
        switch (cycle)
        {
            case HabitCycle.Daily when c.CycleTarget.Value != 1:
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                    "A daily habit in daily mode must have cycleTarget = 1.");
            case HabitCycle.Weekly when c.CycleTarget.Value > 7:
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                    "cycleTarget must be <= 7 for a weekly habit in daily mode.");
            case HabitCycle.Monthly when c.CycleTarget.Value > 31:
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                    "cycleTarget must be <= 31 for a monthly habit in daily mode.");
        }
    }

    /// <summary>
    /// Aggregation modes are typed: boolean → EverTrue only (spec); numeric → Sum/Avg/Max/
    /// Min/Latest; list → UnionDistinct/Latest. Null means the type default and is valid.
    /// </summary>
    public static void ValidateAggregationMode(string propertyName, PropertyType type, AggregationMode? mode)
    {
        if (mode is null)
        {
            return;
        }

        var allowed = type switch
        {
            PropertyType.Boolean => new[] { AggregationMode.EverTrue },
            PropertyType.Numeric => new[] { AggregationMode.Sum, AggregationMode.Avg, AggregationMode.Max, AggregationMode.Min, AggregationMode.Latest },
            _ => new[] { AggregationMode.UnionDistinct, AggregationMode.Latest },
        };
        if (!allowed.Contains(mode.Value))
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Aggregation mode '{mode.Value}' is not valid for property '{propertyName}' of type '{type}'.");
        }
    }

    /// <summary>
    /// Habit-wide name/type consistency (spec): every property row sharing a name must
    /// carry the same type. Check a candidate (name, type) against the habit's existing
    /// rows; used on property create, rename, and item create.
    /// </summary>
    public static void ValidatePropertyTypeConsistency(IEnumerable<(string Name, PropertyType Type)> existing, string name, PropertyType type)
    {
        var clash = existing.FirstOrDefault(e => e.Name == name);
        if (clash.Name is not null && clash.Type != type)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Property name '{name}' is already used in this habit with type '{clash.Type}'; same-named properties must share a type.");
        }
    }

    /// <summary>
    /// Validate the criteria list of a HabitCreate payload: exactly one root, name
    /// uniqueness, per-criterion shape (name-based references only), resolvable names
    /// against the same payload, operand counts, and acyclicity.
    /// </summary>
    public static void ValidateCriterionCreatePayload(
        IReadOnlyList<CriterionCreateDto> criteria, HabitCycle cycle,
        IReadOnlyList<ItemCreateDto> items)
    {
        if (items.Count > MaxItemsPerHabit)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"A habit create payload must not carry more than {MaxItemsPerHabit} items (got {items.Count}).");
        }
        if (criteria.Count > MaxCriteriaPerHabit)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.TooManyEntries,
                $"A habit create payload must not carry more than {MaxCriteriaPerHabit} criteria (got {criteria.Count}).");
        }

        var rootCount = criteria.Count(c => c.IsRoot);
        if (rootCount != 1)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.NoRootCriterion,
                $"Exactly one criterion must be the root (found {rootCount}).");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in criteria)
        {
            RequireName(c.Name, "Criterion");
            if (!names.Add(c.Name))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.DuplicateName,
                    $"Duplicate criterion name '{c.Name}' within the habit.");
            }
            RejectNameIdCollision(c);

            var criterionType = RequireCriterionType(c.CriterionType, c.Name);
            if (!Enum.IsDefined(criterionType))
            {
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                    $"Criterion '{c.Name}': unknown criterionType '{(int)criterionType}'.");
            }

            if (criterionType == CriterionType.Condition)
            {
                if (c.Operator is not null || c.OperandCriterionNames is not null || c.OperandCriterionIds is not null)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                        $"Condition criterion '{c.Name}' must not set composite fields.");
                }

                if (string.IsNullOrWhiteSpace(c.PropertyName))
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                        $"Condition criterion '{c.Name}' requires propertyName.");
                }
                if (c.PropertyName.Length > MaxNameLength)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.ValidationTooLong,
                        $"Criterion '{c.Name}': propertyName must be at most {MaxNameLength} characters (got {c.PropertyName.Length}).");
                }
                var targetProps = items.SelectMany(i => i.Properties)
                    .Where(p => p.Name == c.PropertyName)
                    .ToList();
                if (targetProps.Count == 0)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                        $"Property name '{c.PropertyName}' referenced by criterion '{c.Name}' is not defined in this payload.");
                }

                // A property name is one type across the habit (ValidateItemShape + the
                // per-item uniqueness already checked name/type consistency).
                ValidateAggregationMode(c.PropertyName, RequirePropertyType(targetProps[0].PropertyType, c.PropertyName), c.AggregationMode);
                // !IsFinite first: NaN/Infinity fail neither `<= 0` nor the integral check.
                if (c.Threshold is null || !double.IsFinite(c.Threshold.Value) || c.Threshold.Value <= 0)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidThreshold,
                        $"Condition criterion '{c.Name}' requires a positive finite threshold.");
                }
                if (targetProps[0].PropertyType == PropertyType.Boolean && Math.Floor(c.Threshold.Value) != c.Threshold.Value)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidThreshold,
                        $"Boolean criterion '{c.Name}' requires an integer item-count threshold.");
                }
                if (c.ScopeItemIds is not null)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                        $"HabitCreate condition criterion '{c.Name}' must scope items by name, not id.");
                }
                RejectDuplicateReferences(c.ScopeItemNames, "scopeItemNames", c.Name);
                ValidateReferenceCount(c.ScopeItemNames, "scopeItemNames", c.Name);
                if (c.ItemScope == ItemScope.Subset)
                {
                    if (c.ScopeItemNames is null || c.ScopeItemNames.Count == 0)
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                            $"Subset-scoped criterion '{c.Name}' requires scopeItemNames.");
                    }
                    foreach (var itemName in c.ScopeItemNames)
                    {
                        if (items.All(i => i.Name != itemName))
                        {
                            throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                                $"Scope item name '{itemName}' referenced by criterion '{c.Name}' is not defined in this payload.");
                        }
                    }

                    // Spec: the property must exist on at least one item in the scope.
                    if (!items.Where(i => c.ScopeItemNames.Contains(i.Name))
                            .Any(i => i.Properties.Any(p => p.Name == c.PropertyName)))
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                            $"Property '{c.PropertyName}' is not defined on any item in the subset scope of criterion '{c.Name}'.");
                    }
                }
            }
            else
            {
                if (c.PropertyName is not null || c.AggregationMode is not null || c.Threshold is not null
                    || c.ItemScope is not null || c.ScopeItemNames is not null)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                        $"Composite criterion '{c.Name}' must not set condition fields.");
                }
                if (c.Operator is null)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidTarget,
                        $"Composite criterion '{c.Name}' requires an operator.");
                }
                var operandNames = c.OperandCriterionNames;
                if (operandNames is null || operandNames.Count == 0)
                {
                    throw HabitException.Unprocessable(HabitErrorCodes.InvalidOperandCount,
                        $"Composite criterion '{c.Name}' requires operands.");
                }
                RejectDuplicateReferences(operandNames, "operandCriterionNames", c.Name);
                ValidateReferenceCount(operandNames, "operandCriterionNames", c.Name);
                ValidateOperandCount(c.Operator.Value, operandNames.Count, c.Name);
                foreach (var operandName in operandNames)
                {
                    if (criteria.All(x => x.Name != operandName))
                    {
                        throw HabitException.Unprocessable(HabitErrorCodes.UnknownOperandName,
                            $"Operand criterion name '{operandName}' referenced by criterion '{c.Name}' is not defined in this payload.");
                    }
                }
            }

            ValidateTargets(c, cycle);
        }

        // DAG check over the payload (indices as temporary node keys).
        var indexByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < criteria.Count; i++)
        {
            indexByName[criteria[i].Name] = i;
        }
        var edges = criteria
            .Where(c => c.CriterionType == CriterionType.Composite)
            .ToDictionary(
                c => indexByName[c.Name],
                c => (IReadOnlyList<int>)(c.OperandCriterionNames ?? new List<string>()).Select(n => indexByName[n]).ToList());
        AssertAcyclic(edges);
    }

    /// <summary>Validating name-based/id-based field exclusivity on the create DTO.
    /// (Leaves bind only names — there is no propertyId field to collide with.)</summary>
    private static void RejectNameIdCollision(CriterionCreateDto c)
    {
        if (c.ScopeItemIds is not null && c.ScopeItemNames is not null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Criterion '{c.Name}' must not set both scopeItemIds and scopeItemNames.");
        }
        if (c.OperandCriterionIds is not null && c.OperandCriterionNames is not null)
        {
            throw HabitException.Unprocessable(HabitErrorCodes.InvalidPropertyType,
                $"Criterion '{c.Name}' must not set both operandCriterionIds and operandCriterionNames.");
        }
    }

    public static void ValidateOperandCount(CompositeOperator op, int count, string criterionName)
    {
        switch (op)
        {
            case CompositeOperator.Not when count != 1:
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidOperandCount,
                    $"Composite criterion '{criterionName}': NOT requires exactly one operand.");
            case CompositeOperator.And or CompositeOperator.Or when count < 2:
                throw HabitException.Unprocessable(HabitErrorCodes.InvalidOperandCount,
                    $"Composite criterion '{criterionName}': AND/OR require at least two operands.");
        }
    }

    /// <summary>
    /// DFS cycle detection over the criterion graph (edges: composite criterion id → operand ids).
    /// </summary>
    public static void AssertAcyclic(IReadOnlyDictionary<int, IReadOnlyList<int>> edges)
    {
        var state = new Dictionary<int, byte>(); // 0 unvisited, 1 in-stack, 2 done

        bool Visit(int node)
        {
            if (state.TryGetValue(node, out var s))
            {
                return s == 1; // back edge → cycle
            }

            state[node] = 1;
            foreach (var child in edges.TryGetValue(node, out var list) ? list : Array.Empty<int>())
            {
                if (Visit(child))
                {
                    return true;
                }
            }

            state[node] = 2;
            return false;
        }

        foreach (var node in edges.Keys.ToList())
        {
            if (Visit(node))
            {
                throw HabitException.Unprocessable(
                    HabitErrorCodes.CircularCriterion, "Criterion composition would create a circular reference.");
            }
        }
    }
}
