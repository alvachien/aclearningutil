namespace aclearningutil.Data.Entities;

/// <summary>
/// A tracked habit. Scoped to its owner via <see cref="OwnerId"/> (tenant isolation key —
/// never exposed in API responses). See docs/design-habit-api.md.
/// </summary>
public class Habit
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public HabitCycle Cycle { get; set; }
    public DateOnly StartDate { get; set; }

    /// <summary>Last active day, inclusive. Null = runs indefinitely. Must be &gt;= StartDate when set.</summary>
    public DateOnly? EndDate { get; set; }

    public HabitState State { get; set; } = HabitState.Active;

    /// <summary>
    /// Server-local date recorded when the habit was deactivated (spec FR-2.4).
    /// The last actual cycle shown for an inactive habit is the one containing the
    /// day before this date (FR-2.2).
    /// </summary>
    public DateOnly? DeactivatedDate { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<HabitItem> Items { get; set; } = new();
    public List<Criterion> Criteria { get; set; } = new();
}
