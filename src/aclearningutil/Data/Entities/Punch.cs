namespace aclearningutil.Data.Entities;

/// <summary>
/// One punch session: records values for one or more properties of a single item on one day.
/// PunchDate is derived from the server's local timezone at creation time (NFR-5) and is
/// immutable; the session can be edited/deleted later even in a past cycle or an inactive habit.
/// </summary>
public class Punch
{
    public int Id { get; set; }
    public int HabitId { get; set; }
    public int ItemId { get; set; }

    /// <summary>Redundant tenant key for fast filtering.</summary>
    public string OwnerId { get; set; } = string.Empty;

    /// <summary>UTC instant of the (last) write; refreshed when a same-day boolean is updated.</summary>
    public DateTime PunchedAt { get; set; }

    /// <summary>Server-local calendar day the punch belongs to (ISO yyyy-MM-dd at storage level).</summary>
    public DateOnly PunchDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public Habit? Habit { get; set; }
    public HabitItem? Item { get; set; }
    public List<PunchValue> Values { get; set; } = new();
}

/// <summary>
/// Per-property value inside a punch session. Exactly one of the three value columns is
/// non-null, matching the property's type. Boolean properties maintain at most one value
/// row per (property, item, day) across all sessions — a second same-day punch upserts it.
/// </summary>
public class PunchValue
{
    public int Id { get; set; }
    public int PunchId { get; set; }

    /// <summary>Delete-restricted while any punch value references the property.</summary>
    public int PropertyId { get; set; }

    public bool? BoolValue { get; set; }
    public double? NumValue { get; set; }

    /// <summary>JSON array of strings for list properties.</summary>
    public string? ListEntries { get; set; }

    public Punch? Punch { get; set; }
    public ItemProperty? Property { get; set; }
}
