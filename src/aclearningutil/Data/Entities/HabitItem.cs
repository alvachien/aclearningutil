namespace aclearningutil.Data.Entities;

/// <summary>A named item within a habit. Item names are unique within the habit.</summary>
public class HabitItem
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>Redundant tenant key for fast filtering.</summary>
    public string OwnerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }

    public Habit? Habit { get; set; }
    public List<ItemProperty> Properties { get; set; } = new();
}

/// <summary>
/// A property definition on an item. Each item independently defines its own properties;
/// PropertyType and ItemUniqueness are immutable after creation.
/// </summary>
public class ItemProperty
{
    public int Id { get; set; }
    public int ItemId { get; set; }

    /// <summary>Redundant copies for fast filtering.</summary>
    public int HabitId { get; set; }
    public string OwnerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public PropertyType PropertyType { get; set; }

    /// <summary>Numeric only, &gt; 0 when set. Score multiplier applied during aggregation.</summary>
    public double? BaseRate { get; set; }

    /// <summary>List only (required); forbidden on other types.</summary>
    public ItemUniqueness? ItemUniqueness { get; set; }

    public int Order { get; set; }
    public DateTime CreatedAt { get; set; }

    public HabitItem? Item { get; set; }
    public Habit? Habit { get; set; }
}
