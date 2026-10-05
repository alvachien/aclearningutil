using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;

namespace aclearningutil.test.Helpers;

/// <summary>
/// Shared fixtures for habit-tracking tests: user claims, canonical HabitCreate payloads,
/// and direct entity seeding for scenarios the API itself cannot produce (past-dated
/// punches, cross-tenant rows).
/// </summary>
public static class HabitTestData
{
    public const string TestUser = "habit-user-1";
    public const string OtherUser = "habit-user-2";

    /// <summary>A user who is neither owner nor invitee in the sharing scenarios.</summary>
    public const string ThirdUser = "habit-user-3";

    /// <summary>Matches the controllers' server-local clock.</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Monday of the ISO week containing the date (mirrors the window rule).</summary>
    public static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Monday of the FULLY PAST ISO week (never the clock's own week) — safe
    /// anchors for mid-cycle punch/history tests regardless of the run's weekday.</summary>
    public static DateOnly LastWeekMonday => MondayOf(Today.AddDays(-7));

    /// <summary>
    /// The "name" claim (acidserver ProfileService: AspNetUsers.UserName) backs the
    /// owner-name snapshot on share grants — default it to the id so share tests can
    /// exercise the happy path without extra plumbing.
    /// </summary>
    public static void SetupUserClaims(ControllerBase controller, string userId, string? userName = null)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("name", userName ?? userId),
        };
        var identity = new ClaimsIdentity(claims, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    // ── Payload builders ────────────────────────────────────────────────────────────

    /// <summary>
    /// Weekly "Run" habit: numeric distance property, cumulative condition root
    /// ("sum of distance >= cycleTarget"). Start defaults to a Monday-agnostic -3 days so
    /// today always falls inside the active window.
    /// </summary>
    public static HabitCreateDto WeeklyRunning(
        DateOnly? start = null,
        DateOnly? end = null,
        double threshold = 10) => new()
        {
            Name = "Running",
            Description = "Weekly running target",
            Cycle = HabitCycle.Weekly,
            StartDate = start ?? Today.AddDays(-3),
            EndDate = end,
            Items = new List<ItemCreateDto>
            {
                new()
                {
                    Name = "Run",
                    Order = 0,
                    Properties = new List<PropertyCreateDto>
                    {
                        new() { Name = "distance", PropertyType = PropertyType.Numeric, Order = 0 },
                    },
                },
            },
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = true,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "distance",
                    ItemScope = ItemScope.All,
                    Threshold = threshold,
                    SuccessType = SuccessType.Cumulative,
                    // Cumulative roots carry no cycleTarget — the target is the threshold.
                },
            },
        };

    /// <summary>
    /// Daily checklist habit (RC-6 shape): four items with a shared "done" boolean
    /// property; root condition "count of done &gt;= minDone" in daily mode (the day passes
    /// against the condition's own threshold; cycleTarget = 1 successful day).
    /// </summary>
    public static HabitCreateDto DailyChecklist(
        string name = "Morning routine",
        int itemCount = 4,
        double minDone = 4) => new()
        {
            Name = name,
            Cycle = HabitCycle.Daily,
            StartDate = Today.AddDays(-3),
            Items = Enumerable.Range(1, itemCount).Select(i => new ItemCreateDto
            {
                Name = $"Item{i}",
                Order = i,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 },
                },
            }).ToList(),
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = true,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "done",
                    ItemScope = ItemScope.All,
                    Threshold = minDone,
                    SuccessType = SuccessType.Daily,
                    CycleTarget = 1,
                },
            },
        };

    /// <summary>
    /// Weekly boolean day-count habit (morning_exercise shape): N items with "done";
    /// root condition count &gt;= 1 in daily mode, cycleTarget = successful days per week.
    /// </summary>
    public static HabitCreateDto WeeklyExerciseDayCount(int cycleTarget = 5) => new()
    {
        Name = "Morning Exercise",
        Cycle = HabitCycle.Weekly,
        StartDate = Today.AddDays(-21),
        Items = new[] { "Pushups", "Squats", "Stretching", "Plank" }.Select((n, i) => new ItemCreateDto
        {
            Name = n,
            Order = i,
            Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 },
                },
        }).ToList(),
        Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = true,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "done",
                    ItemScope = ItemScope.All,
                    Threshold = 1,
                    SuccessType = SuccessType.Daily,
                    CycleTarget = cycleTarget,
                },
            },
    };

    /// <summary>Daily vocabulary habit: single item, "word" list property (per_day).</summary>
    public static HabitCreateDto DailyVocabulary(ItemUniqueness uniqueness = ItemUniqueness.PerDay) => new()
    {
        Name = "Vocabulary",
        Cycle = HabitCycle.Daily,
        StartDate = Today.AddDays(-3),
        Items = new List<ItemCreateDto>
            {
                new()
                {
                    Name = "New words",
                    Order = 0,
                    Properties = new List<PropertyCreateDto>
                    {
                        new() { Name = "word", PropertyType = PropertyType.List, ItemUniqueness = uniqueness, Order = 0 },
                    },
                },
            },
        Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = true,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "word",
                    ItemScope = ItemScope.All,
                    Threshold = 10,
                    SuccessType = SuccessType.Cumulative,
                },
            },
    };

    /// <summary>
    /// Weekly vocabulary habit: single item, "word" list property (per_cycle). Starts
    /// exactly three weeks ago so today and the start day always sit in DIFFERENT cycle
    /// windows — exercises cycle-scoped uniqueness across week boundaries.
    /// </summary>
    public static HabitCreateDto WeeklyVocabularyList() => new()
    {
        Name = "Weekly Vocabulary",
        Cycle = HabitCycle.Weekly,
        StartDate = Today.AddDays(-21),
        Items = new List<ItemCreateDto>
        {
            new()
            {
                Name = "New words",
                Order = 0,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "word", PropertyType = PropertyType.List, ItemUniqueness = ItemUniqueness.PerCycle, Order = 0 },
                },
            },
        },
        Criteria = new List<CriterionCreateDto>
        {
            new()
            {
                Name = "C1",
                IsRoot = true,
                CriterionType = CriterionType.Condition,
                PropertyName = "word",
                ItemScope = ItemScope.All,
                Threshold = 10,
                SuccessType = SuccessType.Cumulative,
            },
        },
    };

    /// <summary>
    /// Composite habit (RC-10 shape): weekly, three "Chapter" items with numeric "pages"
    /// and boolean "finished"; C1 pages>=30, C2 finished>=2, root C3 = C1 AND C2.
    /// </summary>
    public static HabitCreateDto CompositeHabit() => new()
    {
        Name = "Book with exercises",
        Cycle = HabitCycle.Weekly,
        StartDate = Today.AddDays(-14),
        Items = new[] { "Chapter 1", "Chapter 2", "Chapter 3" }.Select((n, i) => new ItemCreateDto
        {
            Name = n,
            Order = i,
            Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "pages", PropertyType = PropertyType.Numeric, Order = 0 },
                    new() { Name = "finished", PropertyType = PropertyType.Boolean, Order = 1 },
                },
        }).ToList(),
        Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = false,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "pages",
                    ItemScope = ItemScope.All,
                    Threshold = 30,
                },
                new()
                {
                    Name = "C2",
                    IsRoot = false,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "finished",
                    ItemScope = ItemScope.All,
                    Threshold = 2,
                },
                new()
                {
                    Name = "C3",
                    IsRoot = true,
                    CriterionType = CriterionType.Composite,
                    Operator = CompositeOperator.And,
                    OperandCriterionNames = new List<string> { "C1", "C2" },
                    SuccessType = SuccessType.Cumulative,
                    // AND composite cumulative root: target derived = 2 passing operands.
                },
            },
    };

    /// <summary>
    /// Weekly NESTED composite habit: C1 "distance" ≥ 5, C2 "done" ≥ 1, C3 = OR(C1, C2),
    /// root C4 = AND(C3, C1) — cumulative (the same condition referenced by two composites;
    /// the criterion graph is a DAG, not a tree). Exercises composite-of-composite
    /// evaluation over a window that starts mid-cycle.
    /// </summary>
    public static HabitCreateDto WeeklyNestedComposite(double distanceThreshold = 5) => new()
    {
        Name = "Nested composite",
        Cycle = HabitCycle.Weekly,
        StartDate = Today.AddDays(-21),
        Items = new List<ItemCreateDto>
        {
            new()
            {
                Name = "Session",
                Order = 0,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "distance", PropertyType = PropertyType.Numeric, Order = 0 },
                    new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 1 },
                },
            },
        },
        Criteria = new List<CriterionCreateDto>
        {
            new()
            {
                Name = "C1", IsRoot = false, CriterionType = CriterionType.Condition,
                PropertyName = "distance", ItemScope = ItemScope.All, Threshold = distanceThreshold,
            },
            new()
            {
                Name = "C2", IsRoot = false, CriterionType = CriterionType.Condition,
                PropertyName = "done", ItemScope = ItemScope.All, Threshold = 1,
            },
            new()
            {
                Name = "C3", IsRoot = false, CriterionType = CriterionType.Composite,
                Operator = CompositeOperator.Or, OperandCriterionNames = new List<string> { "C1", "C2" },
            },
            new()
            {
                Name = "C4", IsRoot = true, CriterionType = CriterionType.Composite,
                Operator = CompositeOperator.And, OperandCriterionNames = new List<string> { "C3", "C1" },
                SuccessType = SuccessType.Cumulative,
            },
        },
    };

    // ── Direct entity seeding ───────────────────────────────────────────────────────

    /// <summary>Seeds a habit row (no items/criteria) for the given owner.</summary>
    public static Habit NewHabitRow(string ownerId, HabitCycle cycle = HabitCycle.Weekly, DateOnly? start = null,
        DateOnly? end = null, HabitState state = HabitState.Active, string name = "Seeded habit") => new()
        {
            OwnerId = ownerId,
            Name = name,
            Cycle = cycle,
            StartDate = start ?? Today.AddDays(-10),
            EndDate = end,
            State = state,
            CreatedAt = DateTime.UtcNow,
        };

    /// <summary>
    /// Seeds the canonical weekly "Run" habit directly (numeric distance; cumulative condition
    /// root "distance &gt;= threshold"). Pass <paramref name="successType"/> Daily (with
    /// cycleTarget = successful days) for day-count scenarios.
    /// </summary>
    public static async Task<(Habit habit, HabitItem item, ItemProperty property, Criterion root)> SeedMinimalRunningAsync(
        AppDbContext db, string ownerId = TestUser, double threshold = 10,
        SuccessType successType = SuccessType.Cumulative, double cycleTarget = 1,
        HabitCycle cycle = HabitCycle.Weekly, HabitState state = HabitState.Active,
        DateOnly? start = null, DateOnly? end = null, DateOnly? deactivatedDate = null)
    {
        var habit = NewHabitRow(ownerId, cycle, start, end, state);
        habit.DeactivatedDate = deactivatedDate;
        var item = new HabitItem { Habit = habit, OwnerId = ownerId, Name = "Run", Order = 0, CreatedAt = DateTime.UtcNow };
        var property = new ItemProperty
        {
            Item = item,
            Habit = habit,
            OwnerId = ownerId,
            Name = "distance",
            PropertyType = PropertyType.Numeric,
            Order = 0,
            CreatedAt = DateTime.UtcNow,
        };
        item.Properties.Add(property);
        habit.Items.Add(item);

        var root = new Criterion
        {
            Habit = habit,
            OwnerId = ownerId,
            Name = "C1",
            CriterionType = CriterionType.Condition,
            IsRoot = true,
            SuccessType = successType,
            CycleTarget = successType == SuccessType.Daily ? cycleTarget : null,
            CreatedAt = DateTime.UtcNow,
            Condition = new CriterionCondition { PropertyName = "distance", Threshold = threshold, ItemScope = ItemScope.All },
        };
        habit.Criteria.Add(root);

        db.Habits.Add(habit);
        await db.SaveChangesAsync();
        return (habit, item, property, root);
    }

    /// <summary>Seeds a share grant directly (endpoint-independent invitee setup).</summary>
    public static async Task<HabitShareGrant> SeedGrantAsync(
        AppDbContext db, Habit habit, string granteeUserId,
        string granteeUserName = "Grantee", string ownerName = "Owner")
    {
        var grant = new HabitShareGrant
        {
            HabitId = habit.Id,
            OwnerId = habit.OwnerId,
            GranteeUserId = granteeUserId,
            GranteeUserName = granteeUserName,
            OwnerName = ownerName,
            CreatedAt = DateTime.UtcNow,
        };
        db.HabitShareGrants.Add(grant);
        await db.SaveChangesAsync();
        return grant;
    }

    /// <summary>
    /// Seeds a punch row directly with the given day — used for past-cycle punches that
    /// the punch endpoint (server-dated) cannot create.
    /// </summary>
    public static async Task<Punch> SeedPunchAsync(
        AppDbContext db, Habit habit, HabitItem item, ItemProperty property, DateOnly day,
        bool? boolValue = null, double? numValue = null, IReadOnlyList<string>? listEntries = null)
    {
        var punch = new Punch
        {
            HabitId = habit.Id,
            ItemId = item.Id,
            OwnerId = habit.OwnerId,
            PunchedAt = day.ToDateTime(new TimeOnly(12, 0)).ToUniversalTime(),
            PunchDate = day,
            CreatedAt = DateTime.UtcNow,
        };

        var value = new PunchValue
        {
            Punch = punch,
            PropertyId = property.Id,
            BoolValue = boolValue,
            NumValue = numValue,
            ListEntries = listEntries == null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(listEntries),
        };
        punch.Values.Add(value);

        db.Punches.Add(punch);
        await db.SaveChangesAsync();
        return punch;
    }
}
