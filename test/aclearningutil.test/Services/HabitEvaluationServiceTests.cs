using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Services;

/// <summary>
/// Cycle-window and criterion-evaluation semantics with a FIXED clock (today = a real
/// calendar date injected into the service) — no dependency on the test run date.
/// Reference: functional spec real cases RC-1 … RC-16 (scenario regions for RC-1/2/7/8/9/10)
/// and API design § Cycle Window / § Criterion Evaluation.
/// The window end is nullable (open-ended whole cycles report NULL); roots carry an
/// explicit success_type; cumulative targets are derived, never stored; history lists
/// every in-window day; a cumulative pass latches within its cycle.
/// </summary>
public class HabitEvaluationServiceTests : IDisposable
{
    // A Wednesday in 2026 — ISO week Sep 14 (Mon) .. Sep 20 (Sun).
    private static readonly DateOnly Today = new(2026, 9, 16);
    private static readonly DateOnly WeekMonday = new(2026, 9, 14);

    private readonly AppDbContext _context;
    private readonly HabitEvaluationService _service;

    public HabitEvaluationServiceTests()
    {
        // SQLite in-memory (shared-cache) database — see TestDbContextFactory:
        // the service's aggregation/window logic runs against real SQL
        // semantics, FK enforcement and transactions, exactly as in production.
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _service = new HabitEvaluationService(_context);
    }

    public void Dispose() => _context.Dispose();

    private static string User() => HabitTestData.TestUser;

    private sealed record Graph(Habit Habit, List<HabitItem> Items, Dictionary<string, ItemProperty> PropsByName, Criterion Root);

    /// <summary>
    /// Builds a habit whose ROOT is a condition on one of the item properties (bound by NAME;
    /// aggregation spans every item defining that name). Composites are wired manually in
    /// the tests that need them (AddCompositeRootAsync below).
    /// </summary>
    private async Task<Graph> BuildAsync(
        HabitCycle cycle,
        DateOnly start,
        DateOnly? end,
        (string Item, (string Name, PropertyType Type, double? Rate)[] Props)[] items,
        (string Name, string TargetProp, double Threshold, ItemScope Scope, string[] ScopeItems) conditionRoot,
        (SuccessType Success, double? CycleTarget)? targets = null,
        AggregationMode? conditionMode = null,
        string habitName = "Test")
    {
        var habit = new Habit
        {
            OwnerId = HabitTestData.TestUser,
            Name = habitName,
            Cycle = cycle,
            StartDate = start,
            EndDate = end,
            State = HabitState.Active,
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var (itemName, props) in items)
        {
            var item = new HabitItem { Habit = habit, OwnerId = habit.OwnerId, Name = itemName, Order = habit.Items.Count, CreatedAt = DateTime.UtcNow };
            var order = 0;
            foreach (var (name, type, rate) in props)
            {
                item.Properties.Add(new ItemProperty
                {
                    Item = item,
                    Habit = habit,
                    OwnerId = habit.OwnerId,
                    Name = name,
                    PropertyType = type,
                    BaseRate = rate,
                    ItemUniqueness = type == PropertyType.List ? ItemUniqueness.PerDay : null,
                    Order = order++,
                    CreatedAt = DateTime.UtcNow,
                });
            }
            habit.Items.Add(item);
        }

        // Same-named properties on several items are the norm (e.g. "done" per item) — first wins.
        var byName = habit.Items.SelectMany(i => i.Properties)
            .GroupBy(p => p.Name)
            .ToDictionary(g => g.Key, g => g.First());

        var condition = conditionRoot;
        var t = targets ?? (SuccessType.Cumulative, null);
        var rootCrit = new Criterion
        {
            Habit = habit,
            OwnerId = habit.OwnerId,
            Name = condition.Name,
            CriterionType = CriterionType.Condition,
            IsRoot = true,
            SuccessType = t.Success,
            // Cumulative roots store NO cycle_target (the target is the condition threshold).
            CycleTarget = t.Success == SuccessType.Daily ? t.CycleTarget ?? 1 : null,
            CreatedAt = DateTime.UtcNow,
            Condition = new CriterionCondition
            {
                PropertyName = condition.TargetProp,
                AggregationMode = conditionMode,
                Threshold = condition.Threshold,
                ItemScope = condition.Scope,
            },
        };
        foreach (var scopeItemName in condition.ScopeItems)
        {
            rootCrit.Condition!.ScopeItems.Add(new CriterionConditionScopeItem
            {
                CriterionCondition = rootCrit.Condition,
                Item = habit.Items.First(i => i.Name == scopeItemName),
            });
        }
        habit.Criteria.Add(rootCrit);
        _context.Habits.Add(habit);
        await _context.SaveChangesAsync();
        return new Graph(habit, habit.Items, byName, rootCrit);
    }

    /// <summary>
    /// Demotes the current condition root to a normal criterion and makes the given criterion
    /// names operands of a new composite root. Returns the graph with the new root.
    /// </summary>
    private async Task<Graph> AddCompositeRootAsync(
        Graph g, string rootName, CompositeOperator op, string[] operandNames,
        SuccessType success, double? cycleTarget = null)
    {
        var savedHabit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var all = await _context.Criteria.Where(c => c.HabitId == g.Habit.Id).ToListAsync();
        foreach (var c in all.Where(c => operandNames.Contains(c.Name)))
        {
            c.IsRoot = false;
            c.SuccessType = null;
            c.CycleTarget = null;
        }

        var root = new Criterion
        {
            Habit = savedHabit,
            OwnerId = User(),
            Name = rootName,
            CriterionType = CriterionType.Composite,
            IsRoot = true,
            SuccessType = success,
            CycleTarget = success == SuccessType.Daily ? cycleTarget ?? 1 : null,
            CreatedAt = DateTime.UtcNow,
        };
        var composite = new CriterionComposite { Criterion = root, Operator = op };
        root.Composite = composite;
        var order = 0;
        foreach (var name in operandNames)
        {
            composite.Operands.Add(new CriterionCompositeOperand
            {
                Composite = composite,
                OperandCriterion = all.First(c => c.Name == name),
                Order = order++,
            });
        }
        _context.Criteria.Add(root);
        await _context.SaveChangesAsync();
        return g with { Root = root };
    }

    /// <summary>Adds a non-root condition criterion on the property with the given name.</summary>
    private async Task AddExtraConditionAsync(
        Graph g, string name, string propName, double threshold,
        string[]? scopeItems = null, AggregationMode? mode = null)
    {
        var savedHabit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var prop = g.Items.SelectMany(i => i.Properties).First(p => p.Name == propName);
        var crit = new Criterion
        {
            Habit = savedHabit,
            OwnerId = User(),
            Name = name,
            CriterionType = CriterionType.Condition,
            CreatedAt = DateTime.UtcNow,
            Condition = new CriterionCondition
            {
                PropertyName = propName,
                AggregationMode = mode,
                Threshold = threshold,
                ItemScope = scopeItems is null ? ItemScope.All : ItemScope.Subset,
            },
        };
        foreach (var scopeItemName in scopeItems ?? Array.Empty<string>())
        {
            crit.Condition.ScopeItems.Add(new CriterionConditionScopeItem
            {
                CriterionCondition = crit.Condition,
                Item = g.Items.First(i => i.Name == scopeItemName),
            });
        }
        _context.Criteria.Add(crit);
        await _context.SaveChangesAsync();
    }

    private async Task SeedAsync(Graph g, string itemName, string propName, DateOnly day,
        bool? b = null, double? n = null, string[]? l = null)
    {
        var item = g.Items.First(i => i.Name == itemName);
        var prop = item.Properties.Single(p => p.Name == propName);
        await HabitTestData.SeedPunchAsync(_context, g.Habit, item, prop, day, b, n, l);
    }

    private async Task<CriterionProgressOutDto> RootProgressAsync(Graph g)
    {
        // Refresh the habit with its graph for the service to evaluate.
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, Today);
        return progress.RootCriterion;
    }

    // ── ComputeCycleWindow ───────────────────────────────────────────────────────────

    [Fact]
    public void Window_Weekly_FirstCycle_ClampsToStartMidweek()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Weekly, start: new DateOnly(2026, 9, 16));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 9, 17));
        from.Should().Be(new DateOnly(2026, 9, 16));   // Wed, not Mon Sep 14
        to.Should().Be(new DateOnly(2026, 9, 20));     // Sun
    }

    [Fact]
    public void Window_Weekly_MidHabit_FullIsoWeek()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Weekly, start: new DateOnly(2026, 9, 1));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, Today);
        from.Should().Be(WeekMonday);
        to.Should().Be(new DateOnly(2026, 9, 20));
    }

    [Fact]
    public void Window_Monthly_FirstCycle_ClampsToStartMidMonth()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Monthly, start: new DateOnly(2026, 9, 15));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 9, 20));
        from.Should().Be(new DateOnly(2026, 9, 15));
        to.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public void Window_Monthly_LaterCycle_FullCalendarMonth()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Monthly, start: new DateOnly(2026, 8, 20));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, Today);
        from.Should().Be(new DateOnly(2026, 9, 1));
        to.Should().Be(new DateOnly(2026, 9, 30));
    }

    [Fact]
    public void Window_LastCycle_ClampsToEndMidweek_RC12()
    {
        // RC-12: weekly, Sep 1 .. Sep 17 — on Sep 16 the window is Sep 14..Sep 17.
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Weekly, start: new DateOnly(2026, 9, 1), end: new DateOnly(2026, 9, 17));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 9, 16));
        from.Should().Be(WeekMonday);
        to.Should().Be(new DateOnly(2026, 9, 17));

        // After the end date the final cycle window persists for display.
        var (f2, t2) = HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 10, 5));
        f2.Should().Be(WeekMonday);
        t2.Should().Be(new DateOnly(2026, 9, 17));
    }

    [Fact]
    public void Window_BeforeStart_ShowsFirstCycleRange()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Weekly, start: new DateOnly(2026, 9, 30));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, Today);
        from.Should().Be(new DateOnly(2026, 9, 30));   // Wednesday next week
        to.Should().Be(new DateOnly(2026, 10, 4));     // that week's Sunday
    }

    [Fact]
    public void Window_Daily_IsToday()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Daily, start: new DateOnly(2026, 9, 1));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, Today);
        from.Should().Be(Today);
        to.Should().Be(Today);
    }

    [Fact]
    public void Window_Whole_IsTheHabitSpan_Everywhere()
    {
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Whole, start: new DateOnly(2026, 9, 21), end: new DateOnly(2026, 10, 5));
        // One cycle for the whole life of the habit: the window never moves —
        // before the span, mid-span, after the end.
        var span = (From: new DateOnly(2026, 9, 21), To: (DateOnly?)new DateOnly(2026, 10, 5));
        HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 9, 1)).Should().Be(span);
        HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 9, 25)).Should().Be(span);
        HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 11, 1)).Should().Be(span);
    }

    [Fact]
    public void Window_Whole_OpenEnded_CycleToIsNull()
    {
        // Spec FR-4.1: an open-ended whole cycle reports cycle_to = null — never a
        // sentinel date and never "today" (the UI renders it as "ongoing").
        var habit = HabitTestData.NewHabitRow(User(), HabitCycle.Whole, start: new DateOnly(2026, 9, 1));
        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, Today);
        from.Should().Be(new DateOnly(2026, 9, 1));
        to.Should().BeNull();

        // The window is stable no matter when it is evaluated (aggregation caps at today).
        HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 11, 1)).To.Should().BeNull();
    }

    [Fact]
    public void Window_Inactive_AnchorsLastActualCycle()
    {
        // Spec FR-2.2/FR-2.4: an inactive habit reports the cycle containing its last
        // active day — DeactivatedDate − 1 — not the calendar window of "today".
        var habit = HabitTestData.NewHabitRow(
            User(), HabitCycle.Weekly, start: new DateOnly(2026, 8, 1), state: HabitState.Inactive);
        habit.DeactivatedDate = new DateOnly(2026, 9, 18); // deactivated Friday

        var (from, to) = HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 10, 20));
        from.Should().Be(WeekMonday);          // last active day Thu Sep 17 → week Sep 14..20
        to.Should().Be(new DateOnly(2026, 9, 20));

        // Deactivating on a Monday rolls back to the previous week.
        habit.DeactivatedDate = new DateOnly(2026, 9, 21);
        HabitEvaluationService.ComputeCycleWindow(habit, new DateOnly(2026, 10, 20)).From.Should().Be(new DateOnly(2026, 9, 14));
    }

    // ── Daily mode with base_rate (RC-4 / RC-16) ─────────────────────────────────────

    [Fact]
    public async Task DayCount_Numeric_Applies_PerItem_BaseRate_And_RawValueFallback()
    {
        // RC-16: Push-ups ×1.5, Squats raw (no base_rate), Plank ×0.5; the day passes
        // when the day's weighted sum >= 60 (the condition's own threshold); cycle 1 day.
        var g = await BuildAsync(
            HabitCycle.Daily, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Push-ups", new[] { ("reps", PropertyType.Numeric, (double?)1.5) }),
                ("Squats", new[] { ("reps", PropertyType.Numeric, (double?)null) }),
                ("Plank", new[] { ("reps", PropertyType.Numeric, (double?)0.5) }),
            },
            conditionRoot: ("C1", "reps", 60, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Daily, 1));

        // Short day (RC-4 shape): 15×1.5 + 10 raw = 32.5 < 60 → fail.
        await SeedAsync(g, "Push-ups", "reps", Today, n: 15);
        await SeedAsync(g, "Squats", "reps", Today, n: 10);

        var root = await RootProgressAsync(g);
        root.CurrentDayValue.Should().Be(32.5);
        root.SuccessfulDays.Should().Be(0);
        root.Passed.Should().BeFalse();

        // Complete the day (RC-16 table): push-ups 20 → 30, squats 25 raw, plank 10 → 5.
        await SeedAsync(g, "Push-ups", "reps", Today, n: 5);    // +7.5 → 30
        await SeedAsync(g, "Squats", "reps", Today, n: 15);     // 25 (raw)
        await SeedAsync(g, "Plank", "reps", Today, n: 10);      // 5

        root = await RootProgressAsync(g);
        root.CurrentDayValue.Should().Be(60);
        root.SuccessfulDays.Should().Be(1);
        root.Passed.Should().BeTrue();       // 1 >= cycleTarget 1
    }

    [Fact]
    public async Task Cumulative_Numeric_Sum_AggregatesThroughTodayOnly()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 3);
        await SeedAsync(g, "Run", "distance", Today, n: 7);
        // A punch later in the week (after "today") must not count.
        await SeedAsync(g, "Run", "distance", new DateOnly(2026, 9, 19), n: 50);

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(10);
        root.Passed.Should().BeTrue();
    }

    // ── Aggregation modes & cumulative latching (spec § Aggregation modes) ──────────

    [Fact]
    public async Task Cumulative_Numeric_Avg_Mode_LatchesPass_AfterAggregateFalls()
    {
        // avg per punch: 20+40 → 30 ≥ 30 passes Tue; a 10 on Thu drags the mean to
        // 23.3̄ — the aggregate falls back but the cycle PASS stays latched (spec).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30),
            new[] { ("Session", new[] { ("minutes", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "minutes", 30, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null),
            conditionMode: AggregationMode.Avg);

        await SeedAsync(g, "Session", "minutes", WeekMonday, n: 20);
        await SeedAsync(g, "Session", "minutes", new DateOnly(2026, 9, 15), n: 40);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var atTue = await _service.BuildProgressAsync(habit, new DateOnly(2026, 9, 15));
        atTue.RootCriterion.CurrentValue.Should().Be(30);
        atTue.RootCriterion.Passed.Should().BeTrue();

        await SeedAsync(g, "Session", "minutes", new DateOnly(2026, 9, 17), n: 10);
        var atThu = await _service.BuildProgressAsync(habit, new DateOnly(2026, 9, 17));
        atThu.RootCriterion.CurrentValue.Should().BeApproximately(70.0 / 3.0, 1e-9);
        atThu.RootCriterion.Passed.Should().BeTrue();   // latched — cannot be revoked
    }

    [Fact]
    public async Task Cumulative_Numeric_Min_Mode_LatchesPass()
    {
        // min: Mon 12 passes (≥10); Tue 8 drops the min below the threshold — latched.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30),
            new[] { ("Run", new[] { ("km", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "km", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null),
            conditionMode: AggregationMode.Min);

        await SeedAsync(g, "Run", "km", WeekMonday, n: 12);
        await SeedAsync(g, "Run", "km", new DateOnly(2026, 9, 15), n: 8);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, new DateOnly(2026, 9, 15));
        progress.RootCriterion.CurrentValue.Should().Be(8);
        progress.RootCriterion.Passed.Should().BeTrue();   // latched from Monday's min 12
    }

    [Fact]
    public async Task Cumulative_Numeric_Latest_Mode_HistoryMarksBothDaysSuccessful_Latched()
    {
        // latest punch: Mon 12 passes; Tue 3 falls below but the pass is latched —
        // history rows for BOTH days read is_successful (cumulative mode: true on and
        // after the first pass day).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30),
            new[] { ("Run", new[] { ("km", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "km", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null),
            conditionMode: AggregationMode.Latest);

        await SeedAsync(g, "Run", "km", WeekMonday, n: 12);
        await SeedAsync(g, "Run", "km", new DateOnly(2026, 9, 15), n: 3);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var history = await _service.BuildHistoryAsync(habit, WeekMonday, new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 15));

        history.Should().HaveCount(2);
        history[0].Date.Should().Be(new DateOnly(2026, 9, 15));   // newest first
        history[0].IsSuccessful.Should().BeTrue();                // latched despite latest = 3
        history[0].Criteria[0].CurrentValue.Should().Be(3);
        history[1].IsSuccessful.Should().BeTrue();                // Mon: latest 12 ≥ 10
    }

    // ── Daily (day-count) weekly mode (RC-11) ───────────────────────────────────────

    [Fact]
    public async Task DayCount_Weekly_CountsPassingDaysAgainstCycleTarget()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), null,
            new[]
            {
                ("Pushups", new[] { ("done", PropertyType.Boolean, (double?)null) }),
                ("Squats", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "done", 1, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Daily, 3));

        // Today (Sep 16, Wed) is WeekMonday + 2 — that's the same day, not an extra one.
        await SeedAsync(g, "Pushups", "done", WeekMonday, b: true);              // Mon ✓
        await SeedAsync(g, "Pushups", "done", WeekMonday.AddDays(1), b: false);  // Tue ✗
        await SeedAsync(g, "Squats", "done", Today, b: true);                    // Wed: 1 item
        await SeedAsync(g, "Pushups", "done", Today, b: true);                   // Wed: 2 items (same day)

        var root = await RootProgressAsync(g);
        root.SuccessfulDays.Should().Be(2);   // Mon + Wed; Tue's only punch was false
        root.Passed.Should().BeFalse();       // 2 < cycleTarget 3
        root.CurrentDayValue.Should().Be(2);  // today: two items done (condition shows the day aggregate)

        // PASS direction: a third passing day (Thu) flips the cycle at target 3.
        await SeedAsync(g, "Pushups", "done", new DateOnly(2026, 9, 17), b: true);
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var asOfThu = await _service.BuildProgressAsync(habit, new DateOnly(2026, 9, 17));
        asOfThu.RootCriterion.SuccessfulDays.Should().Be(3);
        asOfThu.RootCriterion.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task DayCount_CurrentDayValue_Null_WhenTodayOutsideActiveWindow()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 10),
            new[] { ("Pushups", new[] { ("done", PropertyType.Boolean, (double?)null) }) },
            conditionRoot: ("C1", "done", 1, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Daily, 3));

        var root = await RootProgressAsync(g);
        root.CurrentDayValue.Should().BeNull();       // habit ended Sep 10 < Today
    }

    [Fact]
    public async Task DayCount_CompositeRoot_UsesPassBit_NotOperandCount()
    {
        // RC-11 shape (morning_exercise template): AND over two per-item subsets in DAILY
        // mode. A day passes only when the WHOLE group passes — the operand count must
        // not be used as the day signal (one operand passing would wrongly count a day).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), null,
            new[]
            {
                ("Pushups", new[] { ("done", PropertyType.Boolean, (double?)null) }),
                ("Squats", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "done", 1, ItemScope.Subset, new[] { "Pushups" }),
            targets: (SuccessType.Cumulative, null));
        await AddExtraConditionAsync(g, "C2", "done", 1, scopeItems: new[] { "Squats" });
        g = await AddCompositeRootAsync(g, "C3", CompositeOperator.And, new[] { "C1", "C2" }, SuccessType.Daily, cycleTarget: 3);

        await SeedAsync(g, "Pushups", "done", WeekMonday, b: true);               // Mon: 1/2 operands → no day
        await SeedAsync(g, "Squats", "done", new DateOnly(2026, 9, 15), b: true);  // Tue: 1/2 (other one) → no day

        var root = await RootProgressAsync(g);   // Wednesday, nothing punched yet today
        root.SuccessfulDays.Should().Be(0);      // pass bit, NOT operand count
        root.CurrentDayValue.Should().Be(0.0);   // composite root: 0/1 signal for today
        root.Passed.Should().BeFalse();

        // Both items today → the group passes today.
        await SeedAsync(g, "Pushups", "done", Today, b: true);
        await SeedAsync(g, "Squats", "done", Today, b: true);
        root = await RootProgressAsync(g);
        root.CurrentDayValue.Should().Be(1.0);
        root.SuccessfulDays.Should().Be(1);
    }

    // ── Boolean aggregation across items (RC-6) ──────────────────────────────────────

    [Fact]
    public async Task Boolean_Cumulative_CountsEachItemOnceEvenWhenTrueManyDays()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Make bed", new[] { ("done", PropertyType.Boolean, (double?)null) }),
                ("Journal", new[] { ("done", PropertyType.Boolean, (double?)null) }),
                ("Exercise", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "done", 3, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Make bed", "done", WeekMonday, b: true);
        await SeedAsync(g, "Make bed", "done", Today, b: true);   // second day — still counts once
        await SeedAsync(g, "Journal", "done", Today, b: true);

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(2);
        root.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Boolean_SameNamedPropertyAcrossItems_RespectsSubsetScope()
    {
        // RC-13: criterion scoped to the three chapters; "Appendix A" is punchable
        // but never advances C1 — through both the fail and the pass verdict.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Chapter 1", new[] { ("completed", PropertyType.Boolean, (double?)null) }),
                ("Chapter 2", new[] { ("completed", PropertyType.Boolean, (double?)null) }),
                ("Chapter 3", new[] { ("completed", PropertyType.Boolean, (double?)null) }),
                ("Appendix A", new[] { ("completed", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "completed", 3, ItemScope.Subset, new[] { "Chapter 1", "Chapter 2", "Chapter 3" }),
            targets: (SuccessType.Cumulative, null));

        // FAIL — only 1 of the 3 scoped chapters; the appendix punch adds 0.
        await SeedAsync(g, "Chapter 1", "completed", Today, b: true);
        await SeedAsync(g, "Appendix A", "completed", Today, b: true);   // out of scope

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(1);   // appendix contributes 0
        root.Passed.Should().BeFalse();

        // PASS — the remaining scoped chapters close C1 at 3.
        await SeedAsync(g, "Chapter 2", "completed", Today, b: true);
        await SeedAsync(g, "Chapter 3", "completed", Today, b: true);

        root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(3);
        root.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Criterion_PropertyNameMissingFromScope_AlwaysFails()
    {
        // The bound name does not exist on any item at all: permanently-0 aggregate,
        // never passes (spec "entire scope lacks the property" rule).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "vanished", 1, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));
        await SeedAsync(g, "Run", "distance", Today, n: 100);

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(0);
        root.Passed.Should().BeFalse();
    }

    // ── List counting: per-item distinct, summed across items (RC-5 / RC-9) ─────────

    [Fact]
    public async Task List_PerDay_UnionDistinct_DoesNotDoubleCountAcrossDays()
    {
        // Spec RC-5 (revised): union_distinct counts each item's DISTINCT strings in the
        // window, regardless of how many days the string was accepted. Mon 3, Tue 2,
        // Wed re-records "ephemeral" → +2 only (7), Thu's three fresh words → 10 ✓.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Words", new[] { ("word", PropertyType.List, (double?)null) }) },
            conditionRoot: ("C1", "word", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Words", "word", WeekMonday, l: new[] { "ephemeral", "lucid", "terse" });
        await SeedAsync(g, "Words", "word", WeekMonday.AddDays(1), l: new[] { "verbose", "concise" });
        await SeedAsync(g, "Words", "word", WeekMonday.AddDays(2), l: new[] { "ephemeral", "opaque", "nuance" });

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(7);   // the Wednesday repeat adds nothing
        root.Passed.Should().BeFalse();

        await SeedAsync(g, "Words", "word", new DateOnly(2026, 9, 17), l: new[] { "tangible", "abstract", "candid" });
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var asOfThu = await _service.BuildProgressAsync(habit, new DateOnly(2026, 9, 17));
        asOfThu.RootCriterion.CurrentValue.Should().Be(10);
        asOfThu.RootCriterion.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task List_PerItemDistinct_CountsSameTextTwiceAcrossDifferentItems()
    {
        // Dedup is per ITEM, not per condition (spec RC-9): "Ex 1" logged under two
        // chapters contributes 2 to the aggregate.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Chapter 1", new[] { ("exercise", PropertyType.List, (double?)null) }),
                ("Chapter 2", new[] { ("exercise", PropertyType.List, (double?)null) }),
            },
            conditionRoot: ("C1", "exercise", 2, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Chapter 1", "exercise", WeekMonday, l: new[] { "Ex 1" });
        await SeedAsync(g, "Chapter 2", "exercise", new DateOnly(2026, 9, 15), l: new[] { "Ex 1" });

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(2);
        root.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task List_PerCycle_EvaluationStillUnionDistinct_IgnoringUniqueness()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Words", new[] { ("word", PropertyType.List, (double?)null) }) },
            conditionRoot: ("C1", "word", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        var wordProp = g.Items[0].Properties[0];
        wordProp.ItemUniqueness = ItemUniqueness.PerCycle;
        await _context.SaveChangesAsync();

        await SeedAsync(g, "Words", "word", WeekMonday, l: new[] { "ephemeral", "lucid", "terse" });
        await SeedAsync(g, "Words", "word", WeekMonday.AddDays(2), l: new[] { "opaque", "nuance" });

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(5);
        root.Passed.Should().BeFalse();

        await SeedAsync(g, "Words", "word", Today, l: new[] { "candid", "diligent", "amiable", "prudent", "luminous" });
        root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(10);
        root.Passed.Should().BeTrue();
    }

    // ── Composites (RC-10, RC-14, RC-23) ────────────────────────────────────────────

    [Fact]
    public async Task Composite_Not_PassesWhenOperandFails_RC14()
    {
        // C1 = had_caffeine ≥ 1 (caffeine day); root C2 = NOT C1 (caffeine-free day).
        // On a DAILY cycle the cumulative window IS the day, so the caffeine-free
        // semantics evaluate identically (a punch-less day passes the NOT root).
        var g = await BuildAsync(
            HabitCycle.Daily, new DateOnly(2026, 9, 1), null,
            new[] { ("Caffeine log", new[] { ("had_caffeine", PropertyType.Boolean, (double?)null) }) },
            conditionRoot: ("C1", "had_caffeine", 1, ItemScope.All, Array.Empty<string>()));
        g = await AddCompositeRootAsync(g, "C2", CompositeOperator.Not, new[] { "C1" }, SuccessType.Cumulative);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);

        // No punch today → C1 fails → NOT passes.
        var progress = await _service.BuildProgressAsync(habit, Today);
        progress.RootCriterion.Operator.Should().Be(CompositeOperator.Not);
        progress.RootCriterion.CurrentValue.Should().Be(1);
        progress.RootCriterion.Threshold.Should().Be(1);
        progress.RootCriterion.SuccessfulDays.Should().BeNull();      // cumulative: day-count fields null
        progress.RootCriterion.CurrentDayValue.Should().BeNull();
        progress.RootCriterion.Passed.Should().BeTrue();

        // Caffeine recorded → C1 passes → NOT fails.
        await SeedAsync(g, "Caffeine log", "had_caffeine", Today, b: true);
        progress = await _service.BuildProgressAsync(habit, Today);
        progress.RootCriterion.CurrentValue.Should().Be(0);
        progress.RootCriterion.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task DayCount_NotRoot_PunchlessDaysPass_RC14()
    {
        // Spec RC-14 exactly: weekly NOT(had_caffeine >= 1) in DAILY mode — a day with
        // no punch passes (the operand fails), a caffeine day does not.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Caffeine log", new[] { ("had_caffeine", PropertyType.Boolean, (double?)null) }) },
            conditionRoot: ("C1", "had_caffeine", 1, ItemScope.All, Array.Empty<string>()));
        g = await AddCompositeRootAsync(g, "C2", CompositeOperator.Not, new[] { "C1" }, SuccessType.Daily, cycleTarget: 5);

        await SeedAsync(g, "Caffeine log", "had_caffeine", WeekMonday, b: true);  // Mon fails
        await SeedAsync(g, "Caffeine log", "had_caffeine", new DateOnly(2026, 9, 15), b: false); // Tue false → operand fails → NOT passes

        var root = await RootProgressAsync(g);   // Wed: nothing punched → passes
        root.SuccessfulDays.Should().Be(2);      // Tue + Wed (Mon failed)
        root.CurrentDayValue.Should().Be(1.0);
        root.Passed.Should().BeFalse();          // 2 < 5
    }

    [Fact]
    public async Task Composite_Or_Root_PassesWhenAnyOperandPasses_RC23()
    {
        // C1 pages ≥ 30, C2 finished ≥ 2, root = OR (derived target: 1 operand).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Chapter 1", new[] { ("pages", PropertyType.Numeric, (double?)null), ("finished", PropertyType.Boolean, (double?)null) }),
                ("Chapter 2", new[] { ("pages", PropertyType.Numeric, (double?)null), ("finished", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "pages", 30, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));
        await AddExtraConditionAsync(g, "C2", "finished", 2);
        g = await AddCompositeRootAsync(g, "C3", CompositeOperator.Or, new[] { "C1", "C2" }, SuccessType.Cumulative);

        await SeedAsync(g, "Chapter 1", "pages", Today, n: 31);   // C1 passes, C2 (0 ≥ 2) does not

        var root = await RootProgressAsync(g);
        root.Operator.Should().Be(CompositeOperator.Or);
        root.CurrentValue.Should().Be(1);      // one operand passing
        root.Threshold.Should().Be(1);         // OR needs any one — the derived target
        root.CycleTarget.Should().BeNull();    // cumulative: never stored, never sent
        root.Passed.Should().BeTrue();
    }

    // ── History (FR-3.4 — fully derived) ───────────────────────────────────────────

    [Fact]
    public async Task History_Cumulative_IsSuccessfulOnlyFromTransitionDay_RC12Style()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 3);
        await SeedAsync(g, "Run", "distance", WeekMonday.AddDays(1), n: 4);
        await SeedAsync(g, "Run", "distance", Today, n: 3);   // running 10 → transition

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var history = await _service.BuildHistoryAsync(habit, WeekMonday, Today, Today);

        history.Should().HaveCount(3);
        history[0].Date.Should().Be(Today);                       // newest first
        history[0].IsSuccessful.Should().BeTrue();
        history[0].CycleFrom.Should().Be(WeekMonday);
        history[0].CycleTo.Should().Be(new DateOnly(2026, 9, 20));
        history[0].Criteria[0].CurrentValue.Should().Be(10);      // running total through that day
        history[1].IsSuccessful.Should().BeFalse();               // 7 at day 2
        history[2].IsSuccessful.Should().BeFalse();               // 3 at day 1
        history[2].Criteria[0].CurrentValue.Should().Be(3);
    }

    [Fact]
    public async Task History_EditingPastPunch_ChangesDerivedOutcome()
    {
        // Spec FR-3.4 (revised): history is a fully derived view — deleting a punch that
        // made the cycle pass removes the success from that day (no frozen snapshot).
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 6);
        var punch = await SeedAsync2(g, "Run", "distance", new DateOnly(2026, 9, 15), 4);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var before = await _service.BuildHistoryAsync(habit, WeekMonday, new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 15));
        before[0].IsSuccessful.Should().BeTrue();    // running 10 on Tue

        await _context.PunchValues.Where(v => v.PunchId == punch.Id).ExecuteDeleteAsync();
        await _context.Punches.Where(p => p.Id == punch.Id).ExecuteDeleteAsync();
        _context.ChangeTracker.Clear();

        var after = await _service.BuildHistoryAsync(habit, WeekMonday, new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 15));
        after[0].IsSuccessful.Should().BeFalse();    // derived again: only 6 remain
    }

    private async Task<Punch> SeedAsync2(Graph g, string itemName, string propName, DateOnly day, double value) =>
        await HabitTestData.SeedPunchAsync(
            _context, g.Habit, g.Items.First(i => i.Name == itemName),
            g.Items.First(i => i.Name == itemName).Properties.Single(p => p.Name == propName),
            day, null, value, null);

    [Fact]
    public async Task History_DayCount_PerDayResult_ReflectsThatDayOnly()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 5, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Daily, 2));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 6);   // day passes
        await SeedAsync(g, "Run", "distance", WeekMonday.AddDays(1), n: 2);  // fails

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var history = await _service.BuildHistoryAsync(habit, WeekMonday, WeekMonday.AddDays(1), Today);

        history.Should().HaveCount(2);
        history[0].IsSuccessful.Should().BeFalse();
        history[0].Criteria[0].CurrentValue.Should().Be(2);   // per-day value, not cumulative
        history[1].IsSuccessful.Should().BeTrue();
        history[1].Criteria[0].CurrentValue.Should().Be(6);
    }

    [Fact]
    public async Task History_ListsPunchlessInWindowDays_WithEmptySessionList()
    {
        // Spec FR-3.4: every day of the window is listed — punch-less days included.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 4);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        // The requested `to` (Sep 17) is beyond "today" (Sep 16) — clamped: Mon..Wed.
        var history = await _service.BuildHistoryAsync(habit, WeekMonday, new DateOnly(2026, 9, 17), Today);

        history.Should().HaveCount(3);   // Mon, Tue, Wed — Tue/Wed have empty session lists
        history.Should().OnlyContain(d => d.Punches.Count <= 1);
        history.Single(d => d.Date == new DateOnly(2026, 9, 15)).Punches.Should().BeEmpty();
        history.Single(d => d.Date == new DateOnly(2026, 9, 15)).IsSuccessful.Should().BeFalse();
    }

    [Fact]
    public async Task History_PunchDaysBeforeStart_AreExcludedByDefaultRange()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 16), null,   // starts Wed
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 99);   // pre-start row
        await SeedAsync(g, "Run", "distance", Today, n: 4);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var history = await _service.BuildHistoryAsync(habit, null, null, Today);
        history.Should().ContainSingle();      // only today (range starts at StartDate)
        history[0].Date.Should().Be(Today);
    }

    [Fact]
    public async Task History_MidCycleWindow_StartsCumulativeAtTheCycleStart_NotTheWindow()
    {
        // A1 (fixed clock): weekly sum ≥ 10; punch 8 Mon + 3 Wed; from = Wed → the Wed row
        // still evaluates [cycleFrom=Mon .. Wed] and reads 11 / passed.
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 8, 1), null,
            new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
            conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 8);
        await SeedAsync(g, "Run", "distance", Today, n: 3);   // Today = Wed 2026-09-16

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var history = await _service.BuildHistoryAsync(habit, Today, Today, Today);

        history.Should().ContainSingle();
        history[0].CycleFrom.Should().Be(WeekMonday);
        history[0].Criteria[0].CurrentValue.Should().Be(11);
        history[0].IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public async Task History_ClampsEndAtDeactivation_NotRootNeverAdvisesLaterDays()
    {
        // A-L4: a daily NOT-root passes on PUNCH-LESS days — the deactivation clamp must
        // remove every day at/after DeactivatedDate so no "achieved" row can appear for
        // time when the habit was already dead.
        var g = await BuildAsync(
            HabitCycle.Daily, new DateOnly(2026, 9, 1), null,
            new[] { ("Caffeine log", new[] { ("had_caffeine", PropertyType.Boolean, (double?)null) }) },
            conditionRoot: ("C1", "had_caffeine", 1, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));
        g = await AddCompositeRootAsync(g, "C2", CompositeOperator.Not, new[] { "C1" }, SuccessType.Cumulative);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        habit.State = HabitState.Inactive;
        habit.DeactivatedDate = new DateOnly(2026, 9, 15);
        await _context.SaveChangesAsync();

        var history = await _service.BuildHistoryAsync(habit, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 20), Today);

        history.Should().NotBeEmpty();
        history.Max(d => d.Date).Should().Be(new DateOnly(2026, 9, 14), "the last active day is DeactivatedDate − 1");
        history.Should().OnlyContain(d => d.Date < new DateOnly(2026, 9, 15));
        // Control: the final allowed day IS punch-less and DOES report the NOT pass —
        // the removal is purely the date clamp, not a data gap.
        history.Single(d => d.Date == new DateOnly(2026, 9, 14)).Punches.Should().BeEmpty();
        history.Single(d => d.Date == new DateOnly(2026, 9, 14)).IsSuccessful.Should().BeTrue();
    }

    // ── Property-level aggregates ────────────────────────────────────────────────────

    [Fact]
    public async Task PropertyAggregates_BooleanTodayStates_And_NumericSums()
    {
        var g = await BuildAsync(
            HabitCycle.Weekly, new DateOnly(2026, 9, 1), null,
            new[]
            {
                ("Run", new[] { ("distance", PropertyType.Numeric, (double?)2.0) }),
                ("Yoga", new[] { ("done", PropertyType.Boolean, (double?)null) }),
                ("Stretch", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            },
            conditionRoot: ("C1", "distance", 100, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null));

        await SeedAsync(g, "Run", "distance", WeekMonday, n: 3);
        await SeedAsync(g, "Run", "distance", Today, n: 2);
        await SeedAsync(g, "Yoga", "done", Today, b: false);
        await SeedAsync(g, "Stretch", "done", WeekMonday, b: true);

        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var agg = await _service.ComputePropertyAggregatesAsync(habit, Today);

        var distance = g.Items[0].Properties[0];
        agg[distance.Id].CurrentCycleValue.Should().Be((3 + 2) * 2.0);   // base_rate applied
        agg[distance.Id].TodayValue.Should().Be(4.0);                     // today 2 × 2

        var yogaDone = g.Items[1].Properties[0];
        agg[yogaDone.Id].TodayValue.Should().Be(0.0);                     // false today → 0.0, not null
        agg[yogaDone.Id].CurrentCycleValue.Should().Be(0);

        var stretchDone = g.Items[2].Properties[0];
        agg[stretchDone.Id].TodayValue.Should().BeNull();                 // untouched today
        agg[stretchDone.Id].CurrentCycleValue.Should().Be(1);
    }

    // ── RC-1 — Running, 10 km/week over two consecutive weekly cycles ──────────────
    // Spec RC-1 "Weekly running target (10 km)": weekly cycle, Run/distance numeric,
    // C1 = sum of distance >= 10 (root, cumulative — the target IS the threshold; no
    // cycle_target is stored). Habit spans Mon 2026-09-21 .. Sun 2026-10-04 = two full
    // ISO weeks; the four scenarios walk this habit through every two-week fail/success
    // combination. Each week passes when the distances punched within that week's
    // window sum to >= 10 km; the verdict is evaluated as of the week's last day, so
    // each window closes with its own total.

    private Task<Graph> BuildRunningAsync() => BuildAsync(
        HabitCycle.Weekly, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 4),
        new[] { ("Run", new[] { ("distance", PropertyType.Numeric, (double?)null) }) },
        conditionRoot: ("C1", "distance", 10, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Running");

    private async Task SeedRunAsync(Graph g, int month, int day, double km) =>
        await SeedAsync(g, "Run", "distance", new DateOnly(2026, month, day), n: km);

    /// <summary>Asserts the weekly verdict as of <paramref name="at"/>: cycle window,
    /// that window's summed distance, and pass/fail against the 10 km target.</summary>
    private async Task AssertRunningWeekAsync(Graph g, DateOnly at, DateOnly from, DateOnly to, double km, bool passed)
    {
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, at);
        progress.CycleFrom.Should().Be(from);
        progress.CycleTo.Should().Be(to);
        progress.RootCriterion.CurrentValue.Should().Be(km);
        progress.RootCriterion.Passed.Should().Be(passed);
    }

    [Fact]
    public async Task Running_TwoWeeks_FirstWeekFail_SecondWeekSuccess_RC1()
    {
        var g = await BuildRunningAsync();
        await SeedRunAsync(g, 9, 21, 2);   // week 1: 2 + 3 = 5
        await SeedRunAsync(g, 9, 23, 3);
        await SeedRunAsync(g, 9, 28, 2);   // week 2: 2 + 3 + 5 = 10
        await SeedRunAsync(g, 9, 30, 3);
        await SeedRunAsync(g, 10, 3, 5);

        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), km: 5, passed: false);
        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), km: 10, passed: true);
    }

    [Fact]
    public async Task Running_TwoWeeks_FirstWeekSuccess_SecondWeekFail_RC1()
    {
        var g = await BuildRunningAsync();
        await SeedRunAsync(g, 9, 21, 2);   // week 1: 2 + 3 + 5 = 10
        await SeedRunAsync(g, 9, 23, 3);
        await SeedRunAsync(g, 9, 26, 5);
        await SeedRunAsync(g, 9, 30, 1);   // week 2: 1 + 5 = 6
        await SeedRunAsync(g, 10, 3, 5);

        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), km: 10, passed: true);
        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), km: 6, passed: false);
    }

    [Fact]
    public async Task Running_TwoWeeks_BothWeeksFail_RC1()
    {
        var g = await BuildRunningAsync();
        await SeedRunAsync(g, 9, 21, 2);   // week 1: 2 + 3 = 5
        await SeedRunAsync(g, 9, 23, 3);
        await SeedRunAsync(g, 9, 28, 1);   // week 2: 1 + 2 + 2 = 5
        await SeedRunAsync(g, 9, 30, 2);
        await SeedRunAsync(g, 10, 3, 2);

        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), km: 5, passed: false);
        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), km: 5, passed: false);
    }

    [Fact]
    public async Task Running_TwoWeeks_BothWeeksSuccess_RC1()
    {
        var g = await BuildRunningAsync();
        await SeedRunAsync(g, 9, 21, 2);   // week 1: 2 + 3 + 5 = 10
        await SeedRunAsync(g, 9, 23, 3);
        await SeedRunAsync(g, 9, 26, 5);
        await SeedRunAsync(g, 9, 28, 3);   // week 2: 3 + 2 + 5 = 10
        await SeedRunAsync(g, 9, 30, 2);
        await SeedRunAsync(g, 10, 3, 5);

        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), km: 10, passed: true);
        await AssertRunningWeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), km: 10, passed: true);
    }

    // ── RC-7 — Book1: four chapters, completion-tracked reading habit ──────────────
    // Spec RC-7 "Book reading: tick chapters + track hours" (book_chapters template)
    // in its FAILING shapes. Habit spans Mon 2026-09-21 .. Mon 2026-10-05 — the end
    // date truncates the third ISO week to a single day, so the windows are
    // [09-21..09-27], [09-28..10-04] and [10-05..10-05]. Like the `book_chapters`
    // template each chapter item carries boolean `completed` + numeric `hours`
    // (hours is tracking-only — it is NOT aggregated by any criterion). Root C1 =
    // count of `completed` ever_true >= 4, cumulative. Case 1 never marks chapter 4;
    // case 2 marks it on 10-05. Each case runs TWICE — once weekly (every window
    // counts only its own marks, 2 / 1 / 1, so neither habit passes) and once on
    // the WHOLE cycle (single window [09-21..10-05]: case 1 sees 3/4 and fails,
    // case 2 sees all four marks and PASSES).

    private Task<Graph> BuildBook1Async(HabitCycle cycle = HabitCycle.Weekly) => BuildAsync(
        cycle, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 5),
        new[]
        {
            ("Chapter 1", new[] { ("completed", PropertyType.Boolean, (double?)null), ("hours", PropertyType.Numeric, (double?)null) }),
            ("Chapter 2", new[] { ("completed", PropertyType.Boolean, (double?)null), ("hours", PropertyType.Numeric, (double?)null) }),
            ("Chapter 3", new[] { ("completed", PropertyType.Boolean, (double?)null), ("hours", PropertyType.Numeric, (double?)null) }),
            ("Chapter 4", new[] { ("completed", PropertyType.Boolean, (double?)null), ("hours", PropertyType.Numeric, (double?)null) }),
        },
        conditionRoot: ("C1", "completed", 4, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Book1");

    /// <summary>One reading session: hours punch, optionally marking the chapter completed the same day.</summary>
    private async Task SeedReadingAsync(Graph g, string chapter, int month, int day, double hours, bool completed = false)
    {
        await SeedAsync(g, chapter, "hours", new DateOnly(2026, month, day), n: hours);
        if (completed)
        {
            await SeedAsync(g, chapter, "completed", new DateOnly(2026, month, day), b: true);
        }
    }

    /// <summary>Asserts Book1's weekly verdict as of <paramref name="at"/>: window plus the
    /// number of chapters completed within it and the pass/fail against the 4-chapter goal.</summary>
    private async Task AssertBook1WeekAsync(Graph g, DateOnly at, DateOnly from, DateOnly to, double completed, bool passed)
    {
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, at);
        progress.CycleFrom.Should().Be(from);
        progress.CycleTo.Should().Be(to);
        progress.RootCriterion.CurrentValue.Should().Be(completed);
        progress.RootCriterion.Passed.Should().Be(passed);
    }

    /// <summary>Book1's shared 17-session reading history (ch1 ✓09-23, ch2 ✓09-27,
    /// ch3 ✓10-01, ch4 read but unmarked through 10-04); <paramref name="markChapter4"/>
    /// adds the 18th session completing chapter 4 on 10-05.</summary>
    private async Task SeedBook1HistoryAsync(Graph g, bool markChapter4)
    {
        // Week 1 (09-21..09-27): ch1 ✓09-23, ch2 ✓09-27.
        await SeedReadingAsync(g, "Chapter 1", 9, 21, 2);
        await SeedReadingAsync(g, "Chapter 1", 9, 22, 3);
        await SeedReadingAsync(g, "Chapter 1", 9, 23, 2, completed: true);
        await SeedReadingAsync(g, "Chapter 2", 9, 23, 1);
        await SeedReadingAsync(g, "Chapter 2", 9, 24, 1);
        await SeedReadingAsync(g, "Chapter 2", 9, 25, 2);
        await SeedReadingAsync(g, "Chapter 2", 9, 26, 2);
        await SeedReadingAsync(g, "Chapter 2", 9, 27, 1, completed: true);
        await SeedReadingAsync(g, "Chapter 3", 9, 27, 1);   // ch3 reading starts Sun (still week 1)

        // Week 2 (09-28..10-04): ch3 ✓10-01; ch4 read but not yet marked.
        await SeedReadingAsync(g, "Chapter 3", 9, 28, 2);
        await SeedReadingAsync(g, "Chapter 3", 9, 29, 2);
        await SeedReadingAsync(g, "Chapter 3", 9, 30, 1);
        await SeedReadingAsync(g, "Chapter 3", 10, 1, 1, completed: true);
        await SeedReadingAsync(g, "Chapter 4", 10, 1, 1);
        await SeedReadingAsync(g, "Chapter 4", 10, 2, 1);
        await SeedReadingAsync(g, "Chapter 4", 10, 3, 2);
        await SeedReadingAsync(g, "Chapter 4", 10, 4, 1);

        if (markChapter4)
        {
            // Week 3 (10-05..): ch4 ✓10-05 — the book is now fully read.
            await SeedReadingAsync(g, "Chapter 4", 10, 5, 2, completed: true);
        }
    }

    [Fact]
    public async Task Book1_NotAllChaptersCompleted_EveryWeeklyCycleFails_RC7()
    {
        var g = await BuildBook1Async();
        await SeedBook1HistoryAsync(g, markChapter4: false);

        // No weekly window reaches 4 completed chapters ⇒ every cycle fails. The
        // boolean count is window-scoped: a chapter completed in week 1 does not
        // carry into week 2, and the tracking-only `hours` punches never advance it.
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), completed: 2, passed: false);
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), completed: 1, passed: false);
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 5),
            from: new DateOnly(2026, 10, 5), to: new DateOnly(2026, 10, 5), completed: 0, passed: false);
    }

    [Fact]
    public async Task Book1_AllChaptersEventuallyCompleted_NoSingleWeeklyCyclePasses_RC7()
    {
        // Same history with ch4 marked on 10-05: "all four completed" is true of
        // the span, yet each weekly window only sees its own marks (2 / 1 / 1),
        // so every weekly cycle still fails. The Whole-cycle twins below give
        // the finished book its success verdict.
        var g = await BuildBook1Async();
        await SeedBook1HistoryAsync(g, markChapter4: true);

        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), completed: 2, passed: false);
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), completed: 1, passed: false);
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 5),
            from: new DateOnly(2026, 10, 5), to: new DateOnly(2026, 10, 5), completed: 1, passed: false);

        // Span-level truth the per-window verdicts can't see: all four chapters
        // WERE completed (one true boolean punch each), spread over three weeks.
        var completionMarks = await _context.PunchValues.CountAsync(v => v.BoolValue == true);
        completionMarks.Should().Be(4);
    }

    [Fact]
    public async Task Book1_WholeCycle_Chapter4Missing_WholeSpanFails_RC7()
    {
        // Case 1 on the WHOLE cycle: one window [09-21..10-05] accumulating every
        // mark ever punched — 3 of 4 chapters ⇒ fail (the goal no longer resets).
        var g = await BuildBook1Async(HabitCycle.Whole);
        await SeedBook1HistoryAsync(g, markChapter4: false);

        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 5),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 5), completed: 3, passed: false);
    }

    [Fact]
    public async Task Book1_WholeCycle_AllChaptersCompleted_WholeSpanPasses_RC7()
    {
        // Case 2 on the WHOLE cycle — the expected outcome: the book is finished
        // by the end date, so the single window counts 4/4 and the habit PASSES.
        var g = await BuildBook1Async(HabitCycle.Whole);
        await SeedBook1HistoryAsync(g, markChapter4: true);

        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 5), completed: 3, passed: false);
        await AssertBook1WeekAsync(g, at: new DateOnly(2026, 10, 5),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 5), completed: 4, passed: true);
    }

    // ── "All items completed" checklist — the simple shape ─────────────────────────
    // Three items, each with a single boolean `done`; root C1 = count of items with
    // `done` true >= 3 (cumulative). Marks accumulate within one cycle window and
    // reset at each window boundary — what doesn't exist is accumulation ACROSS
    // windows (the Book1 problem), solved by the Whole cycle below.

    private Task<Graph> BuildChecklistAsync(HabitCycle cycle, DateOnly start) => BuildAsync(
        cycle, start, null,
        new[]
        {
            ("Task A", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            ("Task B", new[] { ("done", PropertyType.Boolean, (double?)null) }),
            ("Task C", new[] { ("done", PropertyType.Boolean, (double?)null) }),
        },
        conditionRoot: ("C1", "done", 3, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Checklist");

    private async Task<CriterionProgressOutDto> VerdictAsync(Graph g, DateOnly at)
    {
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, at);
        return progress.RootCriterion;
    }

    [Fact]
    public async Task Checklist_AllItemsDone_SameDay_DailyCyclePasses()
    {
        var g = await BuildChecklistAsync(HabitCycle.Daily, new DateOnly(2026, 9, 1));

        // Two of three today: not yet.
        await SeedAsync(g, "Task A", "done", Today, b: true);
        await SeedAsync(g, "Task B", "done", Today, b: true);
        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(2);
        root.Passed.Should().BeFalse();

        // Third item completed → "all items completed" is satisfied for the day.
        await SeedAsync(g, "Task C", "done", Today, b: true);
        root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(3);
        root.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Checklist_AllItemsDone_SameWeek_WeeklyCyclePasses()
    {
        // Marks on DIFFERENT days of one week still accumulate: the window is the
        // week, so 3/3 inside Mon..Wed passes as of Wednesday (Today).
        var g = await BuildChecklistAsync(HabitCycle.Weekly, WeekMonday);
        await SeedAsync(g, "Task A", "done", WeekMonday, b: true);
        await SeedAsync(g, "Task B", "done", WeekMonday.AddDays(1), b: true);
        await SeedAsync(g, "Task C", "done", WeekMonday.AddDays(2), b: true);

        var root = await RootProgressAsync(g);
        root.CurrentValue.Should().Be(3);
        root.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task Checklist_MarksSpreadAcrossWeeks_NeverPasses_TheBook1Problem()
    {
        // Same criterion, same three marks, but one per ISO week: every window
        // counts exactly 1 of 3 and fails.
        var g = await BuildChecklistAsync(HabitCycle.Weekly, WeekMonday);   // Mon 09-14 start
        await SeedAsync(g, "Task A", "done", new DateOnly(2026, 9, 16), b: true);   // week of 09-14
        await SeedAsync(g, "Task B", "done", new DateOnly(2026, 9, 23), b: true);   // week of 09-21
        await SeedAsync(g, "Task C", "done", new DateOnly(2026, 9, 30), b: true);   // week of 09-28

        foreach (var weekEnd in new[] { new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 27), new DateOnly(2026, 10, 4) })
        {
            // Each weekly window closes seeing only the one mark punched inside it.
            var week = await VerdictAsync(g, weekEnd);
            week.CurrentValue.Should().Be(1);
            week.Passed.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Checklist_MarksSpreadAcrossWeeks_WholeCycleAccumulatesAndPasses()
    {
        // The exact "the book was finished eventually" gap: same one-mark-per-week
        // history, but on a Whole cycle the single window accumulates across weeks
        // — after 09-30 all three items are done, so the cycle passes.
        var g = await BuildChecklistAsync(HabitCycle.Whole, WeekMonday);   // start Mon 09-14, no end
        await SeedAsync(g, "Task A", "done", new DateOnly(2026, 9, 16), b: true);
        await SeedAsync(g, "Task B", "done", new DateOnly(2026, 9, 23), b: true);
        await SeedAsync(g, "Task C", "done", new DateOnly(2026, 9, 30), b: true);

        // Mid-span the open window runs to the evaluated day: 2/3 on 09-27, fail.
        var mid = await VerdictAsync(g, new DateOnly(2026, 9, 27));
        mid.CurrentValue.Should().Be(2);
        mid.Passed.Should().BeFalse();

        // Once the last item lands (09-30) the goal is met and STAYS met.
        var done = await VerdictAsync(g, new DateOnly(2026, 10, 4));
        done.CurrentValue.Should().Be(3);
        done.Passed.Should().BeTrue();
    }

    // ── Extended spec scenarios (RC-2 / RC-8 / RC-9 / RC-10) ─────────────────────
    // Multi-week seeded histories with explicit per-window verdicts, mirroring
    // the spec's Illustrative Use Cases. Every scenario asserts BOTH directions
    // (fail and pass) on real calendar dates — no machine-clock dependence.
    // Generic verdict helper for all scenarios below: window bounds + running
    // value + pass/fail as of `at`.
    private async Task AssertVerdictAsync(Graph g, DateOnly at, DateOnly from, DateOnly to, double value, bool passed)
    {
        var habit = await _context.Habits.SingleAsync(h => h.Id == g.Habit.Id);
        var progress = await _service.BuildProgressAsync(habit, at);
        progress.CycleFrom.Should().Be(from);
        progress.CycleTo.Should().Be(to);
        progress.RootCriterion.CurrentValue.Should().Be(value);
        progress.RootCriterion.Passed.Should().Be(passed);
    }

    // ── S1 / RC-2 — Monthly reading, 300 minutes ─────────────────────────────────

    private Task<Graph> BuildMonthlyReadingAsync() => BuildAsync(
        HabitCycle.Monthly, new DateOnly(2026, 9, 1), null,
        new[] { ("Reading", new[] { ("duration", PropertyType.Numeric, (double?)null) }) },
        conditionRoot: ("C1", "duration", 300, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Reading minutes");

    [Fact]
    public async Task Reading_Monthly300_PassesMidMonth_ResetsFailNextMonth_RC2()
    {
        var g = await BuildMonthlyReadingAsync();
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 1), n: 45);
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 2), n: 30);   // two sessions
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 2), n: 20);   // same day …
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 10), n: 105);
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 18), n: 60);

        // Failing mid-month: 45+50+105 = 200, then 260.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 15),
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), value: 200, passed: false);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 18),
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), value: 260, passed: false);

        // 45 more on Sep 25 → 305 ≥ 300: PASS, and the month-end still reads passed.
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 9, 25), n: 45);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 25),
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), value: 305, passed: true);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 30),
            from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30), value: 305, passed: true);

        // October resets: 60 fresh minutes is 60/300 FAIL — September never carries.
        await SeedAsync(g, "Reading", "duration", new DateOnly(2026, 10, 5), n: 60);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 15),
            from: new DateOnly(2026, 10, 1), to: new DateOnly(2026, 10, 31), value: 60, passed: false);
    }

    // ── S2 / RC-8 — Book pages on the whole cycle ────────────────────────────────

    private Task<Graph> BuildBookPagesAsync() => BuildAsync(
        HabitCycle.Whole, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 4),
        new[]
        {
            ("Chapter 1", new[] { ("pages", PropertyType.Numeric, (double?)null) }),
            ("Chapter 2", new[] { ("pages", PropertyType.Numeric, (double?)null) }),
            ("Chapter 3", new[] { ("pages", PropertyType.Numeric, (double?)null) }),
        },
        conditionRoot: ("C1", "pages", 60, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Book — Pages");

    [Fact]
    public async Task BookPages_Whole_PagesAcrossWeeks_Reaches60AndStaysPassed_RC8()
    {
        var g = await BuildBookPagesAsync();
        await SeedAsync(g, "Chapter 1", "pages", new DateOnly(2026, 9, 21), n: 15);
        await SeedAsync(g, "Chapter 1", "pages", new DateOnly(2026, 9, 25), n: 10);
        await SeedAsync(g, "Chapter 2", "pages", new DateOnly(2026, 9, 27), n: 12);

        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 37, passed: false);
        await SeedAsync(g, "Chapter 2", "pages", new DateOnly(2026, 10, 1), n: 8);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 1),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 45, passed: false);

        // Crossing the boundary in week 2 (a weekly cycle would have missed this):
        // 61 ≥ 60 → PASS, and it stays passed through the end of the habit.
        await SeedAsync(g, "Chapter 3", "pages", new DateOnly(2026, 10, 2), n: 16);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 2),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 61, passed: true);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 61, passed: true);
    }

    [Fact]
    public async Task BookPages_Whole_BookLeftAt45Pages_Fails_RC8()
    {
        var g = await BuildBookPagesAsync();
        await SeedAsync(g, "Chapter 1", "pages", new DateOnly(2026, 9, 21), n: 15);
        await SeedAsync(g, "Chapter 1", "pages", new DateOnly(2026, 9, 25), n: 10);
        await SeedAsync(g, "Chapter 2", "pages", new DateOnly(2026, 9, 27), n: 12);
        await SeedAsync(g, "Chapter 2", "pages", new DateOnly(2026, 10, 1), n: 8);

        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 45, passed: false);
    }

    // ── S3 / RC-9 — Book exercises on the whole cycle (per_day list) ─────────────
    // union_distinct counts each CHAPTER's distinct strings over the whole window and
    // sums across chapters: a same-day or later-day repeat adds nothing (it's the
    // PUNCH-TIME uniqueness that guards recording), while the same "Ex 1" under a new
    // chapter counts again (per-item scoping). ≥ 9 across the whole habit.

    private Task<Graph> BuildBookExercisesAsync() => BuildAsync(
        HabitCycle.Whole, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 11),
        new[]
        {
            ("Chapter 1", new[] { ("exercise", PropertyType.List, (double?)null) }),
            ("Chapter 2", new[] { ("exercise", PropertyType.List, (double?)null) }),
            ("Chapter 3", new[] { ("exercise", PropertyType.List, (double?)null) }),
        },
        conditionRoot: ("C1", "exercise", 9, ItemScope.All, Array.Empty<string>()),
        targets: (SuccessType.Cumulative, null),
        habitName: "Book — Exercises");

    [Fact]
    public async Task BookExercises_Whole_UnionDistinct_Reaches9AcrossWeeksPasses_RC9()
    {
        var g = await BuildBookExercisesAsync();
        // Ch1: {Ex 1..4} distinct = 4 (the same-day "Ex 3" repeat adds nothing).
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 21), l: new[] { "Ex 1", "Ex 2", "Ex 3" });
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 21), l: new[] { "Ex 3", "Ex 4" });
        await SeedAsync(g, "Chapter 2", "exercise", new DateOnly(2026, 9, 22), l: new[] { "Ex 5", "Ex 6" });
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 11), value: 6, passed: false);

        // 09-29: Ch1 "Ex 1" again on a new day adds NOTHING (per-item union distinct);
        // Ch3's fresh "Ex 7" brings 7. Then Ch3 {Ex 8, Ex 9} on 10-05 → 9 ≥ 9 PASS,
        // and it holds at the end (union_distinct never regresses).
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 29), l: new[] { "Ex 1" });
        await SeedAsync(g, "Chapter 3", "exercise", new DateOnly(2026, 9, 29), l: new[] { "Ex 7" });
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 30),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 11), value: 7, passed: false);
        await SeedAsync(g, "Chapter 3", "exercise", new DateOnly(2026, 10, 5), l: new[] { "Ex 8", "Ex 9" });
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 5),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 11), value: 9, passed: true);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 11),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 11), value: 9, passed: true);
    }

    [Fact]
    public async Task BookExercises_Whole_StoppedAt8Exercises_Fails_RC9()
    {
        var g = await BuildBookExercisesAsync();
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 21), l: new[] { "Ex 1", "Ex 2", "Ex 3" });
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 21), l: new[] { "Ex 3", "Ex 4" });
        await SeedAsync(g, "Chapter 2", "exercise", new DateOnly(2026, 9, 22), l: new[] { "Ex 5", "Ex 6" });
        await SeedAsync(g, "Chapter 1", "exercise", new DateOnly(2026, 9, 29), l: new[] { "Ex 1" });  // repeat: adds 0
        await SeedAsync(g, "Chapter 3", "exercise", new DateOnly(2026, 9, 29), l: new[] { "Ex 7" });
        await SeedAsync(g, "Chapter 3", "exercise", new DateOnly(2026, 10, 5), l: new[] { "Ex 8" });
        // 8 distinct (4 + 2 + 2) < 9.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 11),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 11), value: 8, passed: false);
    }

    // ── S4 + S5 / RC-10 — Composite root: C1 chapters AND C2 exercises ───────────
    // C1 ≥ 3 chapters, C2 ≥ 9 distinct exercises, root C3 = AND (value = count of
    // passing operands; cumulative target derived = 2). S4 keeps everything inside
    // ONE weekly window like the spec's example; S5 splits the marks over two weeks
    // (weekly never passes, whole does) — the composite twin of the Book1 lesson.

    private async Task<Graph> BuildChapterCompositeAsync(HabitCycle cycle, DateOnly? end)
    {
        var g = await BuildAsync(
            cycle, new DateOnly(2026, 9, 21), end,
            new[]
            {
                ("Chapter 1", new[] { ("completed", PropertyType.Boolean, (double?)null), ("exercise", PropertyType.List, (double?)null) }),
                ("Chapter 2", new[] { ("completed", PropertyType.Boolean, (double?)null), ("exercise", PropertyType.List, (double?)null) }),
                ("Chapter 3", new[] { ("completed", PropertyType.Boolean, (double?)null), ("exercise", PropertyType.List, (double?)null) }),
            },
            conditionRoot: ("C1", "completed", 3, ItemScope.All, Array.Empty<string>()),
            targets: (SuccessType.Cumulative, null),
            habitName: "Book — Chapters + Exercises");
        await AddExtraConditionAsync(g, "C2", "exercise", 9);
        return await AddCompositeRootAsync(g, "C3", CompositeOperator.And, new[] { "C1", "C2" }, SuccessType.Cumulative);
    }

    private Task SeedExercisesAsync(Graph g, string chapter, DateOnly day, params string[] exercises) =>
        SeedAsync(g, chapter, "exercise", day, l: exercises);

    [Fact]
    public async Task Composite_ChaptersAndExercisesInOneWeek_Passes_RC10()
    {
        var g = await BuildChapterCompositeAsync(HabitCycle.Weekly, new DateOnly(2026, 9, 27));
        await SeedAsync(g, "Chapter 1", "completed", new DateOnly(2026, 9, 21), b: true);
        await SeedAsync(g, "Chapter 2", "completed", new DateOnly(2026, 9, 24), b: true);
        await SeedAsync(g, "Chapter 3", "completed", new DateOnly(2026, 9, 25), b: true);
        await SeedExercisesAsync(g, "Chapter 1", new DateOnly(2026, 9, 21), "Ex 1", "Ex 2", "Ex 3");
        await SeedExercisesAsync(g, "Chapter 2", new DateOnly(2026, 9, 22), "Ex 4", "Ex 5", "Ex 6");
        await SeedExercisesAsync(g, "Chapter 3", new DateOnly(2026, 9, 23), "Ex 7", "Ex 8", "Ex 9");

        // Mid-week: exercises are done (9) but only 2 chapters ticked → 1/2 fail.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 23),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), value: 1, passed: false);
        // Week end: C1 3/3 AND C2 9/9 → 2/2 PASS.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), value: 2, passed: true);
    }

    [Fact]
    public async Task Composite_ChaptersDone_ExercisesShort_Fails_RC10()
    {
        var g = await BuildChapterCompositeAsync(HabitCycle.Weekly, new DateOnly(2026, 9, 27));
        await SeedAsync(g, "Chapter 1", "completed", new DateOnly(2026, 9, 21), b: true);
        await SeedAsync(g, "Chapter 2", "completed", new DateOnly(2026, 9, 24), b: true);
        await SeedAsync(g, "Chapter 3", "completed", new DateOnly(2026, 9, 25), b: true);
        await SeedExercisesAsync(g, "Chapter 1", new DateOnly(2026, 9, 21), "Ex 1", "Ex 2", "Ex 3");
        await SeedExercisesAsync(g, "Chapter 2", new DateOnly(2026, 9, 22), "Ex 4", "Ex 5", "Ex 6");
        // Only 6 exercises: C1 passes, C2 not → the week fails on 1 of 2 operands.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), value: 1, passed: false);
    }

    [Fact]
    public async Task Composite_MarksSplitAcrossWeeks_WeeklyNeverPasses_RC10()
    {
        // Chapters in week 1, exercises in week 2 — under a WEEKLY cycle each
        // window only ever sees one passing operand.
        var g = await BuildChapterCompositeAsync(HabitCycle.Weekly, new DateOnly(2026, 10, 4));
        await SeedAsync(g, "Chapter 1", "completed", new DateOnly(2026, 9, 21), b: true);
        await SeedAsync(g, "Chapter 2", "completed", new DateOnly(2026, 9, 23), b: true);
        await SeedAsync(g, "Chapter 3", "completed", new DateOnly(2026, 9, 25), b: true);
        await SeedExercisesAsync(g, "Chapter 1", new DateOnly(2026, 9, 28), "Ex 1", "Ex 2", "Ex 3");
        await SeedExercisesAsync(g, "Chapter 2", new DateOnly(2026, 9, 29), "Ex 4", "Ex 5", "Ex 6");
        await SeedExercisesAsync(g, "Chapter 3", new DateOnly(2026, 9, 30), "Ex 7", "Ex 8", "Ex 9");

        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 9, 27), value: 1, passed: false);   // C1 only
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 28), to: new DateOnly(2026, 10, 4), value: 1, passed: false);   // C2 only
    }

    [Fact]
    public async Task Composite_MarksSplitAcrossWeeks_WholeCyclePasses_RC10()
    {
        // Identical history on a WHOLE cycle: the single window accumulates both
        // operands → C1 3/3 AND C2 9/9 → pass.
        var g = await BuildChapterCompositeAsync(HabitCycle.Whole, new DateOnly(2026, 10, 4));
        await SeedAsync(g, "Chapter 1", "completed", new DateOnly(2026, 9, 21), b: true);
        await SeedAsync(g, "Chapter 2", "completed", new DateOnly(2026, 9, 23), b: true);
        await SeedAsync(g, "Chapter 3", "completed", new DateOnly(2026, 9, 25), b: true);
        await SeedExercisesAsync(g, "Chapter 1", new DateOnly(2026, 9, 28), "Ex 1", "Ex 2", "Ex 3");
        await SeedExercisesAsync(g, "Chapter 2", new DateOnly(2026, 9, 29), "Ex 4", "Ex 5", "Ex 6");
        await SeedExercisesAsync(g, "Chapter 3", new DateOnly(2026, 9, 30), "Ex 7", "Ex 8", "Ex 9");

        // Before week 2's exercises land, only C1 passes → still short.
        await AssertVerdictAsync(g, at: new DateOnly(2026, 9, 27),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 1, passed: false);
        await AssertVerdictAsync(g, at: new DateOnly(2026, 10, 4),
            from: new DateOnly(2026, 9, 21), to: new DateOnly(2026, 10, 4), value: 2, passed: true);
    }

    // RC-3 was retired as a scenario (superseded by RC-5's weekly per_day list +
    // RC-9's whole-cycle book completion). Its mid-habit item-add rule is
    // exercised end to end by the API in
    // HabitItemsControllerTests.Create_MidHabitItemAdd_LeavesExistingPunches_And_NewPunchesAccumulate.
}
