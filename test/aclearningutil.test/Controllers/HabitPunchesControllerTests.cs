using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Controllers;

public class HabitPunchesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly HabitsController _habits;
    private readonly HabitPunchesController _controller;
    private const string User = HabitTestData.TestUser;
    private const string Other = HabitTestData.OtherUser;

    public HabitPunchesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _habits = new HabitsController(_context, evaluation, new Mock<ILogger<HabitsController>>().Object);
        _controller = new HabitPunchesController(_context, new Mock<ILogger<HabitPunchesController>>().Object);
        HabitTestData.SetupUserClaims(_habits, User);
        HabitTestData.SetupUserClaims(_controller, User);
    }

    public void Dispose() => _context.Dispose();

    private static async Task<string> ExpectCodeAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<HabitException>(act);
        return ex.Code;
    }

    /// <summary>CreatedAtAction sets Result (not Value) — unwrap either shape.</summary>
    private static T Unwrap<T>(Microsoft.AspNetCore.Mvc.ActionResult<T> result)
    {
        if (result.Value is { } direct)
        {
            return direct;
        }
        var obj = result.Result.Should().BeAssignableTo<Microsoft.AspNetCore.Mvc.ObjectResult>().Subject;
        return (T)obj.Value!;
    }

    private static HabitOutDto ValueOf(ActionResult<HabitOutDto> result)
    {
        if (result.Value != null)
        {
            return result.Value;
        }
        var obj = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return obj.Value.Should().BeAssignableTo<HabitOutDto>().Subject;
    }

    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Creates a habit via the API and returns its (habitId, itemId, propertyId-by-name map).</summary>
    private async Task<(int habitId, int itemId, Dictionary<string, int> propertyIds)> CreateAndLoadAsync(HabitCreateDto dto)
    {
        var habit = ValueOf(await _habits.Create(dto, CancellationToken.None));
        var item = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id);
        var ids = item.Properties.ToDictionary(p => p.Name, p => p.Id);
        return (habit.Id, item.Id, ids);
    }

    // ── POST — numeric accumulation ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_NumericPunches_AccumulateAcrossSessions_And_Progress()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());

        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 3 } },
        }, CancellationToken.None);
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 2 } },
        }, CancellationToken.None);

        var result = await _controller.GetItemPunches(habitId, itemId, null, null, CancellationToken.None);
        result.Value.Should().HaveCount(2);

        var habitResult = await _habits.GetById(habitId, CancellationToken.None);
        habitResult.Value!.Progress.RootCriterion.CurrentValue.Should().Be(5);
        habitResult.Value.Progress.RootCriterion.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Create_NumericReachingThreshold_MakesRootPass()
    {
        // Cumulative condition root: the threshold IS the cycle target (derived, never stored).
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning(threshold: 5));
        var action = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 6 } },
        }, CancellationToken.None);
        ((ObjectResult)action.Result!).StatusCode.Should().Be(StatusCodes.Status201Created);

        var habitResult = await _habits.GetById(habitId, CancellationToken.None);
        habitResult.Value!.Progress.RootCriterion.Passed.Should().BeTrue();
    }

    // ── POST — boolean same-day upsert (last write wins) ─────────────────────────────

    [Fact]
    public async Task Create_BooleanPunch_Upserts_SingleRowPerPropertyPerDay()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyExerciseDayCount());

        async Task PunchAsync(bool value) =>
            await _controller.Create(habitId, itemId, new PunchCreateDto
            {
                Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["done"], BoolValue = value } },
            }, CancellationToken.None);

        await PunchAsync(true);
        await PunchAsync(false);
        await PunchAsync(true);

        var rows = await _context.PunchValues.Where(v => v.PropertyId == ids["done"]).ToListAsync();
        rows.Should().ContainSingle("boolean properties keep exactly one row per item per day");
        rows[0].BoolValue.Should().BeTrue();
    }

    [Fact]
    public async Task Create_BooleanFalse_With_NoExistingRecord_CreatesRowContributingZero()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyExerciseDayCount());
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["done"], BoolValue = false } },
        }, CancellationToken.None);

        var row = await _context.PunchValues.SingleAsync(v => v.PropertyId == ids["done"]);
        row.BoolValue.Should().BeFalse();

        // RC-6 semantics: the false flag shows as todayValue = 0.0, not null.
        var items = new HabitItemsController(_context, new HabitEvaluationService(_context));
        HabitTestData.SetupUserClaims(items, User);
        var list = await items.GetAll(habitId, CancellationToken.None);
        list.Value!.Single(i => i.Id == itemId).Properties.Single(p => p.Name == "done").TodayValue
            .Should().Be(0.0);
    }

    // ── POST — list uniqueness enforcement (FR-3.2) ─────────────────────────────────

    [Fact]
    public async Task Create_ListPerDay_RejectsEntryAlreadyRecordedToday()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.DailyVocabulary(ItemUniqueness.PerDay));
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { "ephemeral", "lucid" } },
            },
        }, CancellationToken.None);

        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { "terse", "ephemeral" } },
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateEntry);

        // The rejection left no partial record.
        (await _context.Punches.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_ListPerDay_RejectsDuplicates_WithinOneSubmission()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.DailyVocabulary(ItemUniqueness.PerDay));
        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { "x", "x" } },
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateEntry);
    }

    [Fact]
    public async Task Create_ListPerCycle_RejectsEntryAlreadyRecordedThisCycle()
    {
        // Weekly per_cycle list: an entry recorded earlier this week is rejected today.
        var dto = HabitTestData.DailyVocabulary(ItemUniqueness.PerCycle) with
        {
            Cycle = HabitCycle.Weekly,
            StartDate = HabitTestData.Today.AddDays(-21),
        };
        var (habitId, itemId, ids) = await CreateAndLoadAsync(dto);

        var monday = MondayOf(HabitTestData.Today);
        var habit = await _context.Habits.SingleAsync(h => h.Id == habitId);
        var item = await _context.HabitItems.Include(i => i.Properties).SingleAsync(i => i.Id == itemId);
        await HabitTestData.SeedPunchAsync(_context, habit, item,
            item.Properties.Single(p => p.Name == "word"), monday, listEntries: new[] { "opaque" });

        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { "opaque", "nuance" } },
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateEntry);
    }

    [Fact]
    public async Task Create_PerDay_AllowsSameEntryOnDifferentDays()
    {
        var dto = HabitTestData.DailyVocabulary(ItemUniqueness.PerDay) with
        {
            Cycle = HabitCycle.Weekly,
            StartDate = HabitTestData.Today.AddDays(-21),
        };
        var (habitId, itemId, ids) = await CreateAndLoadAsync(dto);
        var habit = await _context.Habits.SingleAsync(h => h.Id == habitId);
        var item = await _context.HabitItems.Include(i => i.Properties).SingleAsync(i => i.Id == itemId);
        var word = item.Properties.Single(p => p.Name == "word");

        var lastMonday = MondayOf(HabitTestData.Today).AddDays(-7);
        await HabitTestData.SeedPunchAsync(_context, habit, item, word, lastMonday, listEntries: new[] { "ephemeral" });

        // Same text, different week/day — accepted (per_day dedupe is within the day only).
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = new List<string> { "ephemeral" } } },
        }, CancellationToken.None);
        (await _context.Punches.CountAsync(p => p.HabitId == habitId)).Should().Be(2);
    }

    // ── POST — state and window rules (FR-3.3) ───────────────────────────────────────

    [Fact]
    public async Task Create_Rejects_InactiveHabit()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        await _habits.Deactivate(habitId, CancellationToken.None);

        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.HabitInactive);
    }

    [Fact]
    public async Task Create_Rejects_TodayOutsideActiveWindow()
    {
        // Starts tomorrow.
        var (notStartedId, ni, nIds) = await CreateAndLoadAsync(
            HabitTestData.WeeklyRunning(start: HabitTestData.Today.AddDays(1)));
        (await ExpectCodeAsync(() => _controller.Create(notStartedId, ni, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = nIds["distance"], NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.OutOfWindow);

        // Ended yesterday.
        var (endedId, ei, eIds) = await CreateAndLoadAsync(
            HabitTestData.WeeklyRunning(end: HabitTestData.Today.AddDays(-1)));
        (await ExpectCodeAsync(() => _controller.Create(endedId, ei, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = eIds["distance"], NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.OutOfWindow);
    }

    // ── POST — client-supplied PunchDate (back-fill) ─────────────────────────────────

    [Fact]
    public async Task Create_PunchDate_BackfillsInWindowDay_StampsThatDate_And_CountsInCurrentCycle()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());

        // Latest of the week's Monday and the habit start (-3d): guaranteed inside BOTH
        // the active window and the CURRENT cycle, so the progress assertion is
        // day-of-week independent (today == Monday → same-day explicit punch).
        var day = MondayOf(HabitTestData.Today);
        if (day < HabitTestData.Today.AddDays(-3))
        {
            day = HabitTestData.Today.AddDays(-3);
        }

        var created = Unwrap<PunchOutDto>(await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 4 } },
            PunchDate = day,
        }, CancellationToken.None));
        created.PunchDate.Should().Be(day);

        var dayPunches = await _controller.GetItemPunches(habitId, itemId, day, day, CancellationToken.None);
        dayPunches.Value!.Single().Values.Single().NumValue.Should().Be(4);

        var habitResult = await _habits.GetById(habitId, CancellationToken.None);
        habitResult.Value!.Progress.RootCriterion.CurrentValue.Should().Be(4);
    }

    [Fact]
    public async Task Create_PunchDate_FutureDay_RejectedOutOfWindow()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
            PunchDate = HabitTestData.Today.AddDays(1),
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.OutOfWindow);
    }

    [Fact]
    public async Task Create_PunchDate_BeforeStartDate_RejectedOutOfWindow()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
            PunchDate = HabitTestData.Today.AddDays(-30),
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.OutOfWindow);
    }

    [Fact]
    public async Task Create_PunchDate_AfterPeriodEnded_StillAcceptedForInWindowDay()
    {
        // Spec FR-3.3 (revised): the window check follows the PUNCH DATE, not "today".
        // The habit is still active and the punched day lies inside the window — the
        // back-fill of a missed day is accepted even though the period already ended.
        var (habitId, itemId, ids) = await CreateAndLoadAsync(
            HabitTestData.WeeklyRunning(end: HabitTestData.Today.AddDays(-1)));

        var created = Unwrap<PunchOutDto>(await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
            PunchDate = HabitTestData.Today.AddDays(-2),
        }, CancellationToken.None));
        created.PunchDate.Should().Be(HabitTestData.Today.AddDays(-2));

        // ... while a date beyond the end (or "today", outside the window) is rejected.
        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.OutOfWindow);
    }

    [Fact]
    public async Task Create_PunchDate_BooleanUpsertsBackfillDay_NotToday()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyExerciseDayCount());
        var backDay = HabitTestData.Today.AddDays(-1);

        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["done"], BoolValue = true } },
        }, CancellationToken.None); // today
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["done"], BoolValue = true } },
            PunchDate = backDay,
        }, CancellationToken.None); // back-fill
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["done"], BoolValue = false } },
            PunchDate = backDay,
        }, CancellationToken.None); // back-fill again → same-day upsert

        // One session per day, one boolean row each — the back-fill never touches today's row.
        var backPunches = (await _controller.GetItemPunches(habitId, itemId, backDay, backDay, CancellationToken.None)).Value!;
        backPunches.Should().HaveCount(1);
        backPunches.Single().Values.Should().HaveCount(1);
        backPunches.Single().Values.Single().BoolValue.Should().BeFalse();

        var todayPunches = (await _controller.GetItemPunches(habitId, itemId,
            HabitTestData.Today, HabitTestData.Today, CancellationToken.None)).Value!;
        todayPunches.Single().Values.Single().BoolValue.Should().BeTrue();
    }

    [Fact]
    public async Task Create_PunchDate_PerDayUniquenessScopedToBackfillDay()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.DailyVocabulary());
        var backDay = HabitTestData.Today.AddDays(-1);

        async Task PunchDay(List<string> entries, DateOnly day) =>
            await _controller.Create(habitId, itemId, new PunchCreateDto
            {
                Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = entries } },
                PunchDate = day,
            }, CancellationToken.None);

        await PunchDay(new List<string> { "alpha", "beta" }, backDay);

        // Same entry, same (back-filled) day → duplicate.
        (await ExpectCodeAsync(() => PunchDay(new List<string> { "alpha" }, backDay)))
            .Should().Be(HabitErrorCodes.DuplicateEntry);

        // Same entry, different day → fine (per_day scope follows the punch day).
        await PunchDay(new List<string> { "alpha" }, HabitTestData.Today.AddDays(-2));
        (await _context.Punches.CountAsync(p => p.HabitId == habitId)).Should().Be(2);
    }

    [Fact]
    public async Task Create_PunchDate_PerCycleBackfillIntoEarlierCycle_UsesThatCyclesWindow()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyVocabularyList());
        var earlierWeek = HabitTestData.Today.AddDays(-21); // exactly 3 weeks back → always a distinct week

        async Task PunchCycleDay(List<string> entries, DateOnly day) =>
            await _controller.Create(habitId, itemId, new PunchCreateDto
            {
                Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = entries } },
                PunchDate = day,
            }, CancellationToken.None);

        await PunchCycleDay(new List<string> { "alpha" }, HabitTestData.Today);

        // per_cycle duplicate check for the back-fill runs against the EARLIER cycle's
        // window — today's "alpha" is invisible there, so this is accepted …
        await PunchCycleDay(new List<string> { "alpha" }, earlierWeek);
        // … but repeating it inside the same earlier cycle is rejected.
        (await ExpectCodeAsync(() => PunchCycleDay(new List<string> { "alpha" }, earlierWeek)))
            .Should().Be(HabitErrorCodes.DuplicateEntry);

        var dayPunches = (await _controller.GetItemPunches(habitId, itemId, earlierWeek, earlierWeek, CancellationToken.None)).Value!;
        dayPunches.Should().HaveCount(1);
        dayPunches.Single().PunchDate.Should().Be(earlierWeek);
    }

    // ── POST — value/type validation ─────────────────────────────────────────────────

    [Fact]
    public async Task Create_Rejects_TypeMismatches()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());

        // numeric value on a boolean property (checklist) and vice versa
        var (clId, clItem, clIds) = await CreateAndLoadAsync(HabitTestData.DailyChecklist());

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], BoolValue = true } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        (await ExpectCodeAsync(() => _controller.Create(clId, clItem, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = clIds["done"], NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        // two values at once / no value at all
        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1, BoolValue = true } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"] } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        // non-positive number, blank list entry, empty values list
        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 0 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        (await ExpectCodeAsync(() => _controller.Create(clId, clItem, new PunchCreateDto { Values = new List<PropertyValueCreateDto>() },
            CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_PropertyOfAnotherItem_AsNotFound()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.CompositeHabit());
        var sibling = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habitId && i.Id != itemId);
        var foreignPropId = sibling.Properties.Single(p => p.Name == "pages").Id;

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = foreignPropId, NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = 4242, NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);
    }

    // ── A-L2 / A-L5: numeric finiteness + server-side payload limits ─────────────────

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Create_Rejects_NonFinite_NumValue(double num)
    {
        // Infinity (e.g. a "1e999" wire literal) must not become a silently accepted punch.
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = num } },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
        (await _context.Punches.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_Rejects_TooMany_Values_In_One_Session()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var values = Enumerable.Range(1, HabitRuleValidator.MaxValuesPerPunch + 1)
            .Select(i => new PropertyValueCreateDto { PropertyId = ids["distance"], NumValue = 1 })
            .ToList();
        var act = () => _controller.Create(habitId, itemId, new PunchCreateDto { Values = values }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.TooManyEntries);
    }

    [Fact]
    public async Task Create_Rejects_TooMany_List_Entries_And_Accepts_At_Limit()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.DailyVocabulary());

        var over = Enumerable.Range(1, HabitRuleValidator.MaxEntriesPerValue + 1)
            .Select(i => $"w{i}").ToList();
        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = over } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.TooManyEntries);

        var atLimit = Enumerable.Range(1, HabitRuleValidator.MaxEntriesPerValue).Select(i => $"w{i}").ToList();
        var created = Unwrap(await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = atLimit } },
        }, CancellationToken.None));
        created.Values.Single().ListEntries.Should().HaveCount(HabitRuleValidator.MaxEntriesPerValue);
    }

    [Fact]
    public async Task Create_Rejects_Overlong_List_Entry_And_Accepts_At_Limit()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.DailyVocabulary());

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { new string('x', HabitRuleValidator.MaxEntryLength + 1) } },
            },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.ValidationTooLong);

        var created = Unwrap(await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["word"], ListEntries = new List<string> { new string('y', HabitRuleValidator.MaxEntryLength) } },
            },
        }, CancellationToken.None));
        created.Values.Single().ListEntries!.Single().Should().HaveLength(HabitRuleValidator.MaxEntryLength);
    }

    // ── GET lists ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetItemPunches_FiltersByDate_And_OrdersNewestFirst()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var habit = await _context.Habits.SingleAsync(h => h.Id == habitId);
        var item = await _context.HabitItems.Include(i => i.Properties).SingleAsync(i => i.Id == itemId);
        var distance = item.Properties.Single(p => p.Name == "distance");
        var monday = MondayOf(HabitTestData.Today);

        await HabitTestData.SeedPunchAsync(_context, habit, item, distance, monday, numValue: 3);
        await HabitTestData.SeedPunchAsync(_context, habit, item, distance, monday.AddDays(1), numValue: 4);

        var all = await _controller.GetItemPunches(habitId, itemId, null, null, CancellationToken.None);
        all.Value.Should().HaveCount(2);
        all.Value![0].PunchDate.Should().Be(monday.AddDays(1));

        var onlyMonday = await _controller.GetItemPunches(habitId, itemId, monday, monday, CancellationToken.None);
        onlyMonday.Value.Should().ContainSingle();
        onlyMonday.Value![0].Values[0].NumValue.Should().Be(3);
    }

    [Fact]
    public async Task GetHabitPunches_Returns_AggregateAcrossItems_NewestFirst()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.DailyChecklist(), CancellationToken.None));
        var h = await _context.Habits.SingleAsync(x => x.Id == habit.Id);
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == h.Id).ToListAsync();
        await _controller.Create(h.Id, items[0].Id, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = items[0].Properties[0].Id, BoolValue = true } },
        }, CancellationToken.None);
        await _controller.Create(h.Id, items[1].Id, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = items[1].Properties[0].Id, BoolValue = true } },
        }, CancellationToken.None);

        var result = await _controller.GetHabitPunches(h.Id, null, null, CancellationToken.None);
        result.Value.Should().HaveCount(2);
        result.Value!.Select(p => p.ItemId).Should().BeEquivalentTo(new[] { items[0].Id, items[1].Id });
    }

    [Fact]
    public async Task GetById_Returns_Punch_With_PropertyMetadata()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var created = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 2.5 } },
        }, CancellationToken.None);
        var punchId = ((ObjectResult)created.Result!).Value.Should().BeAssignableTo<PunchOutDto>().Subject.Id;

        var result = await _controller.GetById(habitId, itemId, punchId, CancellationToken.None);
        result.Value!.Values[0].PropertyName.Should().Be("distance");
        result.Value.Values[0].PropertyType.Should().Be(PropertyType.Numeric);
    }

    [Fact]
    public async Task Punches_Of_Other_Users_Are_Invisible()
    {
        var (habitId, itemId, _) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        HabitTestData.SetupUserClaims(_controller, Other);

        (await ExpectCodeAsync(() => _controller.GetHabitPunches(habitId, null, null, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
        (await ExpectCodeAsync(() => _controller.GetItemPunches(habitId, itemId, null, null, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── PUT ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Numeric_ReplacesSessionValue_RunningTotalMovesByDelta()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var first = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 3 } },
        }, CancellationToken.None);
        await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 5 } },
        }, CancellationToken.None);

        var punchId = ((PunchOutDto)((ObjectResult)first.Result!).Value!).Id;
        await _controller.Update(habitId, itemId, punchId, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 4 } },
        }, CancellationToken.None);

        var habitResult = await _habits.GetById(habitId, CancellationToken.None);
        habitResult.Value!.Progress.RootCriterion.CurrentValue.Should().Be(9);  // 4 + 5, not 3+5+4
        (await _context.Punches.CountAsync(p => p.HabitId == habitId)).Should().Be(2);
    }

    [Fact]
    public async Task Update_PerCycleList_ExcludesTheEditedPunchFromUniquenessCheck()
    {
        var dto = HabitTestData.DailyVocabulary(ItemUniqueness.PerCycle) with
        {
            Cycle = HabitCycle.Weekly,
            StartDate = HabitTestData.Today.AddDays(-21),
        };
        var (habitId, itemId, ids) = await CreateAndLoadAsync(dto);
        var p1 = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = new List<string> { "a" } } },
        }, CancellationToken.None);
        var p2 = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = new List<string> { "b" } } },
        }, CancellationToken.None);
        var p2obj = Unwrap(p2);

        // Editing p2 to keep its own "b" plus add "c": own entries don't count against it.
        var ok = await _controller.Update(habitId, itemId, p2obj.Id, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = new List<string> { "b", "c" } } },
        }, CancellationToken.None);
        ok.Value!.Values.Single().ListEntries.Should().BeEquivalentTo(new[] { "b", "c" });

        // But p1's "a" (another session this cycle) is rejected.
        (await ExpectCodeAsync(() => _controller.Update(habitId, itemId, p2obj.Id, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["word"], ListEntries = new List<string> { "a" } } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.DuplicateEntry);
        _ = p1;
    }

    [Fact]
    public async Task Update_OmittedPropertyRows_AreRemoved()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.CompositeHabit());
        var created = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto>
            {
                new() { PropertyId = ids["pages"], NumValue = 10 },
                new() { PropertyId = ids["finished"], BoolValue = true },
            },
        }, CancellationToken.None);
        var punchId = ((PunchOutDto)((ObjectResult)created.Result!).Value!).Id;

        var updated = await _controller.Update(habitId, itemId, punchId, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["pages"], NumValue = 12 } },
        }, CancellationToken.None);

        updated.Value!.Values.Should().ContainSingle();
        updated.Value.Values[0].NumValue.Should().Be(12);
        (await _context.PunchValues.CountAsync(v => v.PunchId == punchId)).Should().Be(1);
    }

    [Fact]
    public async Task Update_BooleanValue_Upserts_SameDayRow_From_AnotherSession()
    {
        // H3: the one-boolean-row-per-(property,item,day) invariant must survive an EDIT.
        // Session A records the day's boolean; session B is numeric. PUT-ing B with a
        // boolean value used to insert a second live boolean row for the same day — the
        // design promises Create's same-day upsert applies on edit instead.
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.CompositeHabit());

        var a = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["finished"], BoolValue = true } },
        }, CancellationToken.None);
        var b = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["pages"], NumValue = 10 } },
        }, CancellationToken.None);
        var aId = ((PunchOutDto)((ObjectResult)a.Result!).Value!).Id;
        var bId = ((PunchOutDto)((ObjectResult)b.Result!).Value!).Id;

        await _controller.Update(habitId, itemId, bId, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["finished"], BoolValue = false } },
        }, CancellationToken.None);

        var boolRows = await _context.PunchValues
            .Where(v => v.PropertyId == ids["finished"] && v.BoolValue != null)
            .ToListAsync();
        boolRows.Should().ContainSingle("exactly one live boolean row per (property, item, day) after an edit");
        boolRows[0].PunchId.Should().Be(aId, "the upsert updates the existing session's row in place (Create semantics)");
        boolRows[0].BoolValue.Should().BeFalse("last write wins");

        // And the value is visible through the aggregate list, not duplicated: exactly one
        // boolean value row for this item/day in total.
        var punches = await _controller.GetItemPunches(habitId, itemId, null, null, CancellationToken.None);
        punches.Value!.SelectMany(p => p.Values).Count(v => v.BoolValue != null).Should().Be(1);
    }

    [Fact]
    public async Task Update_BooleanValue_With_NoExistingDayRow_KeepsRow_In_Edited_Session()
    {
        // The other branch: no other session owns the day's boolean row → the edited punch
        // receives it (no phantom update of an unrelated session).
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.CompositeHabit());
        var b = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["pages"], NumValue = 10 } },
        }, CancellationToken.None);
        var bId = ((PunchOutDto)((ObjectResult)b.Result!).Value!).Id;

        await _controller.Update(habitId, itemId, bId, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["finished"], BoolValue = true } },
        }, CancellationToken.None);

        var row = await _context.PunchValues.SingleAsync(v => v.PropertyId == ids["finished"]);
        row.PunchId.Should().Be(bId);
        row.BoolValue.Should().BeTrue();
    }

    [Fact]
    public async Task PunchListEndpoints_Reject_InvertedDateRange()
    {
        // L7c: History rejects from>to with invalidDateRange — the punch lists now match.
        var (habitId, itemId, _) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var today = HabitTestData.Today;

        (await ExpectCodeAsync(() => _controller.GetItemPunches(
            habitId, itemId, today, today.AddDays(-1), CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidDateRange);
        (await ExpectCodeAsync(() => _controller.GetHabitPunches(
            habitId, today, today.AddDays(-1), CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidDateRange);

        // Half-open ranges remain valid (previously returned empty; must stay 200).
        (await _controller.GetHabitPunches(habitId, null, today, CancellationToken.None)).Value
            .Should().NotBeNull();
        (await _controller.GetItemPunches(habitId, itemId, today, null, CancellationToken.None)).Value
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Update_Allowed_Even_When_Habit_Is_Inactive()
    {
        // FR-3.4: history records stay editable after deactivation.
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var created = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 2 } },
        }, CancellationToken.None);
        await _habits.Deactivate(habitId, CancellationToken.None);

        var punchId = ((PunchOutDto)((ObjectResult)created.Result!).Value!).Id;
        var result = await _controller.Update(habitId, itemId, punchId, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 7 } },
        }, CancellationToken.None);
        result.Value!.Values[0].NumValue.Should().Be(7);
    }

    [Fact]
    public async Task Update_UnknownOrForeignPunch_NotFound()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        (await ExpectCodeAsync(() => _controller.Update(habitId, itemId, 987654, new PunchUpdateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 1 } },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesPunch_And_ItsValues_And_RefreshesProgress()
    {
        var (habitId, itemId, ids) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        var created = await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 9 } },
        }, CancellationToken.None);
        var punchId = ((PunchOutDto)((ObjectResult)created.Result!).Value!).Id;

        var result = await _controller.Delete(habitId, itemId, punchId, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        (await _context.Punches.CountAsync()).Should().Be(0);
        (await _context.PunchValues.CountAsync()).Should().Be(0);

        var habitResult = await _habits.GetById(habitId, CancellationToken.None);
        habitResult.Value!.Progress.RootCriterion.CurrentValue.Should().Be(0);
    }

    [Fact]
    public async Task Delete_Allowed_After_Period_Ended_And_When_Inactive()
    {
        // FR-3.4: corrections are exempt from the state/window gates that govern creation.
        var (habitId, itemId, ids) = await CreateAndLoadAsync(
            HabitTestData.WeeklyRunning(end: HabitTestData.Today.AddDays(-1)));
        var backDay = HabitTestData.Today.AddDays(-2);
        var created = Unwrap<PunchOutDto>(await _controller.Create(habitId, itemId, new PunchCreateDto
        {
            Values = new List<PropertyValueCreateDto> { new() { PropertyId = ids["distance"], NumValue = 2 } },
            PunchDate = backDay,
        }, CancellationToken.None));
        await _habits.Deactivate(habitId, CancellationToken.None);

        var result = await _controller.Delete(habitId, itemId, created.Id, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
        (await _context.Punches.CountAsync(p => p.HabitId == habitId)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_Other_Users_Punch_NotFound()
    {
        var (habitId, itemId, _) = await CreateAndLoadAsync(HabitTestData.WeeklyRunning());
        HabitTestData.SetupUserClaims(_controller, Other);
        (await ExpectCodeAsync(() => _controller.Delete(habitId, itemId, 1, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task Endpoints_Without_User_Claim_Return_Unauthorized()
    {
        // A-L1: every habit endpoint's missing-claim path is the unauthenticated
        // ProblemDetails envelope (HabitException 401), never a bare body.
        HabitTestData.SetupUserClaims(_controller, string.Empty);
        var ex = await Assert.ThrowsAsync<HabitException>(
            () => _controller.GetHabitPunches(1, null, null, CancellationToken.None));
        ex.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        ex.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        var createEx = await Assert.ThrowsAsync<HabitException>(
            () => _controller.Create(1, 1, new PunchCreateDto(), CancellationToken.None));
        createEx.Code.Should().Be(HabitErrorCodes.Unauthenticated);

        foreach (var act in new Func<Task>[]
        {
            () => _controller.GetItemPunches(1, 1, null, null, CancellationToken.None),
            () => _controller.GetById(1, 1, 1, CancellationToken.None),
            () => _controller.Update(1, 1, 1, new PunchUpdateDto(), CancellationToken.None),
            () => _controller.Delete(1, 1, 1, CancellationToken.None),
        })
        {
            var e = await Assert.ThrowsAsync<HabitException>(act);
            e.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        }
    }
}
