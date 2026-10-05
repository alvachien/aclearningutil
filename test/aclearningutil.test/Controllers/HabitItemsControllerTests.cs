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

public class HabitItemsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly HabitsController _habits;
    private readonly HabitItemsController _controller;
    private const string User = HabitTestData.TestUser;
    private const string Other = HabitTestData.OtherUser;

    public HabitItemsControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _habits = new HabitsController(_context, evaluation, new Mock<ILogger<HabitsController>>().Object);
        _controller = new HabitItemsController(_context, evaluation);
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

    private async Task<HabitOutDto> CreateChecklistAsync() => ValueOf(await _habits.Create(HabitTestData.DailyChecklist(), CancellationToken.None));

    private static HabitOutDto ValueOf(ActionResult<HabitOutDto> result)
    {
        if (result.Value != null)
        {
            return result.Value;
        }
        var obj = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return obj.Value.Should().BeAssignableTo<HabitOutDto>().Subject;
    }

    // ── GET list ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns_Items_With_Properties_And_Aggregates()
    {
        var habit = await CreateChecklistAsync();
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == habit.Id).ToListAsync();
        var done1 = items[0].Properties.Single(p => p.Name == "done");
        await HabitTestData.SeedPunchAsync(_context, await _context.Habits.SingleAsync(h => h.Id == habit.Id),
            items[0], done1, HabitTestData.Today, boolValue: true);

        var result = await _controller.GetAll(habit.Id, CancellationToken.None);

        result.Value.Should().HaveCount(4);
        var item1 = result.Value!.Single(i => i.Name == "Item1");
        item1.HasPunches.Should().BeTrue();
        item1.Properties.Should().ContainSingle();
        item1.Properties[0].Name.Should().Be("done");
        item1.Properties[0].TodayValue.Should().Be(1.0);
        item1.Properties[0].CurrentCycleValue.Should().Be(1);   // daily cycle: one day true

        result.Value!.Single(i => i.Name == "Item4").HasPunches.Should().BeFalse();
    }

    [Fact]
    public async Task GetAll_Other_Users_Habit_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        (await ExpectCodeAsync(() => _controller.GetAll(habit.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetAll_Without_User_Claim_Unauthorized()
    {
        // A-L1: unauthenticated → HabitException(401, unauthenticated) via the filter, not a bare body.
        HabitTestData.SetupUserClaims(_controller, string.Empty);
        var ex = await Assert.ThrowsAsync<HabitException>(() => _controller.GetAll(1, CancellationToken.None));
        ex.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        ex.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task GetById_Single_Item()
    {
        var habit = await CreateChecklistAsync();
        var created = await _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Extra",
            Order = 9,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "reps", PropertyType = PropertyType.Numeric, BaseRate = 1.5, Order = 0 },
            },
        }, CancellationToken.None);

        var result = await _controller.GetById(habit.Id, Unwrap(created).Id, CancellationToken.None);
        result.Value!.Name.Should().Be("Extra");
        result.Value.Properties[0].BaseRate.Should().Be(1.5);
    }

    [Fact]
    public async Task GetById_Wrong_Habit_NotFound()
    {
        var habitA = await CreateChecklistAsync();
        var habitB = ValueOf(await _habits.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        var item = await _context.HabitItems.FirstAsync(i => i.HabitId == habitA.Id);

        (await ExpectCodeAsync(() => _controller.GetById(habitB.Id, item.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── POST create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AddsItemWithProperties_Even_When_Habit_Has_Punches()
    {
        // FR-2.3: adding an item is always permitted.
        var habit = await CreateChecklistAsync();
        var h = await _context.Habits.SingleAsync(x => x.Id == habit.Id);
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == h.Id).ToListAsync();
        await HabitTestData.SeedPunchAsync(_context, h, items[0], items[0].Properties[0], HabitTestData.Today, boolValue: true);

        var result = await _controller.Create(h.Id, new ItemCreateDto
        {
            Name = "New item",
            Order = 7,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "minutes", PropertyType = PropertyType.Numeric, Order = 0 },
            },
        }, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status201Created);
        (await _context.HabitItems.CountAsync(i => i.HabitId == h.Id)).Should().Be(5);
    }

    [Fact]
    public async Task Create_MidHabitItemAdd_LeavesExistingPunches_And_NewPunchesAccumulate()
    {
        // RC-3's "new chapter items may be added at any time" note, end to end via the API:
        // adding a chapter mid-habit never disturbs existing punch records, and the new
        // item's punches fold into the same weekly window.
        var habit = ValueOf(await _habits.Create(new HabitCreateDto
        {
            Name = "Book study",
            Cycle = HabitCycle.Weekly,
            StartDate = HabitTestData.Today.AddDays(-3),
            Items = new[] { "Chapter 3", "Chapter 4" }.Select((n, i) => new ItemCreateDto
            {
                Name = n,
                Order = i,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "exercise", PropertyType = PropertyType.List, ItemUniqueness = ItemUniqueness.PerDay, Order = 0 },
                },
            }).ToList(),
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1",
                    IsRoot = true,
                    CriterionType = CriterionType.Condition,
                    PropertyName = "exercise",
                    ItemScope = ItemScope.All,
                    Threshold = 10,
                    SuccessType = SuccessType.Cumulative,
                },
            },
        }, CancellationToken.None));

        var puncher = new HabitPunchesController(_context, new Mock<ILogger<HabitPunchesController>>().Object);
        HabitTestData.SetupUserClaims(puncher, User);
        async Task PunchExercisesAsync(int itemId, int propertyId, params string[] entries)
        {
            await puncher.Create(habit.Id, itemId, new PunchCreateDto
            {
                Values = new List<PropertyValueCreateDto> { new() { PropertyId = propertyId, ListEntries = entries.ToList() } },
            }, CancellationToken.None);
        }

        var chapters = await _context.HabitItems.Include(i => i.Properties)
            .Where(i => i.HabitId == habit.Id).OrderBy(i => i.Order).ToListAsync();
        await PunchExercisesAsync(chapters[0].Id, chapters[0].Properties[0].Id, "Ex 1", "Ex 2", "Ex 3");
        await PunchExercisesAsync(chapters[1].Id, chapters[1].Properties[0].Id, "Ex 4", "Ex 5");
        ValueOf(await _habits.GetById(habit.Id, CancellationToken.None))
            .Progress.RootCriterion.CurrentValue.Should().Be(5);

        var added = Unwrap(await _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Chapter 5",
            Order = 9,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "exercise", PropertyType = PropertyType.List, ItemUniqueness = ItemUniqueness.PerDay, Order = 0 },
            },
        }, CancellationToken.None));

        // The add alone changes nothing — existing records and their aggregate stand.
        ValueOf(await _habits.GetById(habit.Id, CancellationToken.None))
            .Progress.RootCriterion.CurrentValue.Should().Be(5);

        await PunchExercisesAsync(added.Id, added.Properties[0].Id, "Ex 6", "Ex 7");
        var progress = ValueOf(await _habits.GetById(habit.Id, CancellationToken.None)).Progress.RootCriterion;
        progress.CurrentValue.Should().Be(7);
        progress.Passed.Should().BeFalse();

        await PunchExercisesAsync(chapters[0].Id, chapters[0].Properties[0].Id, "Ex 8", "Ex 9", "Ex 10");
        progress = ValueOf(await _habits.GetById(habit.Id, CancellationToken.None)).Progress.RootCriterion;
        progress.CurrentValue.Should().Be(10);
        progress.Passed.Should().BeTrue();

        // Original punch sessions on Chapter 3 are untouched (two list sessions).
        (await _context.Punches.CountAsync(p => p.HabitId == habit.Id && p.ItemId == chapters[0].Id))
            .Should().Be(2);
    }

    [Fact]
    public async Task Create_Rejects_DuplicateItemName()
    {
        var habit = await CreateChecklistAsync();
        var act = () => _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Item1",
            Order = 8,
            Properties = new List<PropertyCreateDto> { new() { Name = "x", PropertyType = PropertyType.Boolean, Order = 0 } },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_Item_Without_Properties()
    {
        var habit = await CreateChecklistAsync();
        var act = () => _controller.Create(habit.Id, new ItemCreateDto { Name = "Empty", Order = 1 }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.ItemWithoutProperties);
    }

    [Fact]
    public async Task Create_Rejects_Undefined_PropertyType_Value()
    {
        // M3 defense-in-depth: an out-of-range enum (unreachable through the string-only
        // JSON converter, reachable via other binding paths) → invalidPropertyType.
        var habit = await CreateChecklistAsync();
        var act = () => _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Weird",
            Order = 1,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "x", PropertyType = (PropertyType)99, Order = 0 },
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Invalid_Property_Definition()
    {
        var habit = await CreateChecklistAsync();
        var act = () => _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Bad",
            Order = 1,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "w", PropertyType = PropertyType.List, Order = 0 },   // missing uniqueness
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidItemUniqueness);
    }

    [Fact]
    public async Task Create_Rejects_SameNamed_Property_With_Different_Type()
    {
        // Habit-wide same-name ⇒ same-type rule: "done" is boolean on every existing item.
        var habit = await CreateChecklistAsync();
        var act = () => _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Extra chapter",
            Order = 8,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "done", PropertyType = PropertyType.Numeric, Order = 0 },
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Allows_SameNamed_Property_With_Same_Type()
    {
        var habit = await CreateChecklistAsync();
        var created = Unwrap(await _controller.Create(habit.Id, new ItemCreateDto
        {
            Name = "Extra chapter",
            Order = 8,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 },
            },
        }, CancellationToken.None));
        created.Name.Should().Be("Extra chapter");
        created.Properties.Single().Name.Should().Be("done");
    }

    // ── PUT update ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Renames_And_Reorders()
    {
        var habit = await CreateChecklistAsync();
        var item = await _context.HabitItems.FirstAsync(i => i.HabitId == habit.Id);

        var result = await _controller.Update(habit.Id, item.Id, new ItemUpdateDto { Name = "Renamed item", Order = -3 }, CancellationToken.None);

        result.Value!.Name.Should().Be("Renamed item");
        result.Value.Order.Should().Be(-3);
    }

    [Fact]
    public async Task Update_Rename_ConflictWithSibling_IsRejected()
    {
        var habit = await CreateChecklistAsync();
        var items = await _context.HabitItems.Where(i => i.HabitId == habit.Id).ToListAsync();
        var act = () => _controller.Update(habit.Id, items[0].Id, new ItemUpdateDto { Name = items[1].Name, Order = 0 }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateName);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Blocked_When_Item_Has_Punches()
    {
        var habit = await CreateChecklistAsync();
        var h = await _context.Habits.SingleAsync(x => x.Id == habit.Id);
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == h.Id).ToListAsync();
        await HabitTestData.SeedPunchAsync(_context, h, items[0], items[0].Properties[0], HabitTestData.Today, boolValue: true);

        var code = await ExpectCodeAsync(() => _controller.Delete(h.Id, items[0].Id, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.ItemHasPunches);
        (await _context.HabitItems.CountAsync(i => i.Id == items[0].Id)).Should().Be(1);
    }

    [Fact]
    public async Task Delete_Removes_Item_And_Its_Properties_When_Clean()
    {
        var habit = await CreateChecklistAsync();
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == habit.Id).ToListAsync();

        var result = await _controller.Delete(habit.Id, items[3].Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.HabitItems.CountAsync(i => i.HabitId == habit.Id)).Should().Be(3);
        (await _context.ItemProperties.CountAsync(p => p.ItemId == items[3].Id)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_Allowed_When_Item_Property_Is_Referenced_By_A_Criterion_Name()
    {
        // Spec FR-2.3 (revised): criteria bind properties by NAME — a punch-free item is
        // deletable even while the criterion resolves its property name via other items;
        // the scope simply shrinks.
        var habit = await CreateChecklistAsync();
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == habit.Id).ToListAsync();

        var result = await _controller.Delete(habit.Id, items[0].Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.HabitItems.CountAsync(i => i.HabitId == habit.Id)).Should().Be(3);
        // Root "count of done >= 4" survives with three in-scope items — it just cannot
        // pass anymore (max count 3 < threshold 4).
        var progress = ValueOf(await _habits.GetById(habit.Id, CancellationToken.None)).Progress.RootCriterion;
        progress.Threshold.Should().Be(4);
        progress.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_Shrinks_Criterion_Scope_Rows_With_The_Item()
    {
        // The root condition binds the property NAME "done"; scope = {Item1..Item4}. Deleting
        // the punch-free Item4 shrinks the scope membership (by design).
        var checklist = HabitTestData.DailyChecklist();
        var dto = checklist with
        {
            StartDate = HabitTestData.Today,   // no punches possible yet
            Criteria = new List<CriterionCreateDto>
            {
                checklist.Criteria[0] with
                {
                    ItemScope = ItemScope.Subset,
                    ScopeItemNames = new List<string> { "Item1", "Item2", "Item3", "Item4" },
                },
            },
        };
        var habit = ValueOf(await _habits.Create(dto, CancellationToken.None));

        (await _context.CriterionConditionScopeItems.CountAsync()).Should().Be(4);
        var item4 = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id && i.Name == "Item4");

        var result = await _controller.Delete(habit.Id, item4.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.CriterionConditionScopeItems.CountAsync()).Should().Be(3);
    }
}
