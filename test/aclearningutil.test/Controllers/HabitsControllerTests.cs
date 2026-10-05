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

public class HabitsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly HabitsController _controller;
    private const string User = HabitTestData.TestUser;
    private const string Other = HabitTestData.OtherUser;

    public HabitsControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _controller = new HabitsController(_context, evaluation, new Mock<ILogger<HabitsController>>().Object);
        HabitTestData.SetupUserClaims(_controller, User);
    }

    public void Dispose() => _context.Dispose();

    /// <summary>Runs an action expected to fail with a HabitException and returns its code.</summary>
    private static async Task<string> ExpectCodeAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<HabitException>(act);
        ex.Detail.Should().NotBeNullOrWhiteSpace();
        return ex.Code;
    }

    /// <summary>A-L1: a failed identity check must surface as the unauthenticated ProblemDetails
    /// envelope (HabitException 401), never a bare UnauthorizedObjectResult body.</summary>
    private static async Task ExpectUnauthenticatedAsync(Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<HabitException>(act);
        ex.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        ex.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ex.Detail.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>Extracts the payload from an ActionResult — value form or wrapped ObjectResult (CreatedAtAction).</summary>
    private static HabitOutDto ValueOf(ActionResult<HabitOutDto> result)
    {
        if (result.Value != null)
        {
            return result.Value;
        }
        var obj = result.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        return obj.Value.Should().BeAssignableTo<HabitOutDto>().Subject;
    }

    // ── GET /api/Habits ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns_Only_Current_User_Habits_With_Progress()
    {
        await HabitTestData.SeedMinimalRunningAsync(_context, User);
        await HabitTestData.SeedMinimalRunningAsync(_context, Other);

        var result = await _controller.GetAll(CancellationToken.None);

        result.Value.Should().HaveCount(1);
        result.Value!.Single().Progress.RootCriterion.Name.Should().Be("C1");
        // Tenant key is internal and never part of the response contract (FR-6).
        typeof(HabitOutDto).GetProperty("OwnerId").Should().BeNull();
    }

    [Fact]
    public async Task GetAll_Empty_For_New_User()
    {
        var result = await _controller.GetAll(CancellationToken.None);
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAll_Without_User_Claim_Returns_Unauthorized()
    {
        HabitTestData.SetupUserClaims(_controller, string.Empty);
        // A-L1: the 401 envelope is an RFC 7807 problem with code=unauthenticated —
        // produced by the HabitException the controller throws (the filter maps it).
        await ExpectUnauthenticatedAsync(() => _controller.GetAll(CancellationToken.None));
    }

    [Fact]
    public async Task Other_Endpoints_Without_User_Claim_Return_Unauthorized()
    {
        HabitTestData.SetupUserClaims(_controller, string.Empty);

        var id = 1;
        await ExpectUnauthenticatedAsync(() => _controller.GetById(id, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.Update(
            id, new HabitUpdateDto { Name = "x", Cycle = HabitCycle.Daily, StartDate = default }, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.Deactivate(id, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.Delete(id, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.GetHistory(id, null, null, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.GetShares(id, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.AddShare(id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = "Bob" }, CancellationToken.None));
        await ExpectUnauthenticatedAsync(() => _controller.RemoveShare(id, 1, CancellationToken.None));
    }

    // ── GET /api/Habits/{id} ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_Returns_Habit_With_Embedded_Progress()
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 4.0);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 7.0);

        var result = await _controller.GetById(habit.Id, CancellationToken.None);

        result.Value!.HasPunches.Should().BeTrue();
        result.Value.State.Should().Be(HabitState.Active);
        var root = result.Value.Progress.RootCriterion;
        root.CurrentValue.Should().Be(11.0);   // 4 + 7 this week
        root.SuccessType.Should().Be(SuccessType.Cumulative);
        // Cumulative target is derived from the threshold — never stored/exposed as cycleTarget.
        root.CycleTarget.Should().BeNull();
        root.Threshold.Should().Be(10);
        root.Passed.Should().BeTrue();
    }

    [Fact]
    public async Task GetById_Other_Users_Habit_Returns_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        (await ExpectCodeAsync(() => _controller.GetById(habit.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── POST /api/Habits — happy path & atomicity ────────────────────────────────────

    [Fact]
    public async Task Create_ValidHabit_Persists_Full_Graph_And_Returns_201()
    {
        var dto = HabitTestData.CompositeHabit();

        var action = await _controller.Create(dto, CancellationToken.None);
        action.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);

        var created = ValueOf(action);
        created.Progress.Criteria.Should().HaveCount(2);   // non-root C1 + C2
        created.Progress.RootCriterion.Operator.Should().Be(CompositeOperator.And);

        (await _context.Habits.CountAsync()).Should().Be(1);
        (await _context.HabitItems.CountAsync()).Should().Be(3);
        (await _context.ItemProperties.CountAsync()).Should().Be(6);
        (await _context.Criteria.CountAsync()).Should().Be(3);
        (await _context.Criteria.CountAsync(c => c.IsRoot)).Should().Be(1);

        // Leaves bind property NAMES (spec): no property-row FK exists on a criterion condition.
        var pagesCondition = await _context.CriterionConditions.OrderBy(l => l.Id).FirstAsync();
        pagesCondition.PropertyName.Should().Be("pages");
        (await _context.CriterionConditions.CountAsync(l => l.PropertyName == "finished")).Should().Be(1);
        (await _context.CriterionCompositeOperands.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Create_UnresolvableOperandName_Rolls_Back_Everything()
    {
        var dto = HabitTestData.CompositeHabit();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0],
                dto.Criteria[1],
                new()
                {
                    Name = "C3", IsRoot = true, CriterionType = CriterionType.Composite,
                    Operator = CompositeOperator.And,
                    OperandCriterionNames = new List<string> { "C1", "DOES_NOT_EXIST" },
                    SuccessType = SuccessType.Cumulative,
                },
            },
        };

        var code = await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None));

        code.Should().Be(HabitErrorCodes.UnknownOperandName);
        (await _context.Habits.CountAsync()).Should().Be(0);
        (await _context.HabitItems.CountAsync()).Should().Be(0);
        (await _context.Criteria.CountAsync()).Should().Be(0);
    }

    // ── POST /api/Habits — validation matrix ─────────────────────────────────────────

    [Fact]
    public async Task Create_Rejects_NoItems()
    {
        var dto = HabitTestData.WeeklyRunning() with { Items = new List<ItemCreateDto>() };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NoItems);
    }

    [Fact]
    public async Task Create_Rejects_NoCriteria()
    {
        var dto = HabitTestData.WeeklyRunning() with { Criteria = new List<CriterionCreateDto>() };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NoCriteria);
    }

    [Fact]
    public async Task Create_Rejects_Item_Without_Properties()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Items = new List<ItemCreateDto> { new() { Name = "Empty", Order = 0, Properties = new List<PropertyCreateDto>() } },
        };
        // L2: noItems stays reserved for habit-without-items; an item without properties
        // has its own code.
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.ItemWithoutProperties);
    }

    [Fact]
    public async Task Create_Rejects_InvalidDateRange()
    {
        var dto = HabitTestData.WeeklyRunning(start: HabitTestData.Today, end: HabitTestData.Today.AddDays(-1));
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidDateRange);
    }

    [Fact]
    public async Task Create_Rejects_BlankName()
    {
        var dto = HabitTestData.WeeklyRunning() with { Name = " " };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidName);
    }

    [Fact]
    public async Task Create_Rejects_DuplicateItemName()
    {
        var baseItem = HabitTestData.WeeklyRunning().Items[0];
        var dto = HabitTestData.WeeklyRunning() with
        {
            Items = new List<ItemCreateDto> { baseItem, baseItem },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_DuplicatePropertyName_Within_Item()
    {
        var item = HabitTestData.WeeklyRunning().Items[0];
        var dto = HabitTestData.WeeklyRunning() with
        {
            Items = new List<ItemCreateDto>
            {
                item with { Properties = new List<PropertyCreateDto> { item.Properties[0], item.Properties[0] } },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_DuplicateCriterionName()
    {
        var c = HabitTestData.WeeklyRunning().Criteria[0];
        var dto = HabitTestData.WeeklyRunning() with
        {
            Criteria = new List<CriterionCreateDto>
            {
                c,
                c with { IsRoot = false },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_Multiple_Roots()
    {
        var c = HabitTestData.WeeklyRunning().Criteria[0];
        var dto = HabitTestData.WeeklyRunning() with
        {
            Criteria = new List<CriterionCreateDto> { c, c with { Name = "C2" } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NoRootCriterion);
    }

    [Fact]
    public async Task Create_Rejects_Missing_Root()
    {
        var c = HabitTestData.WeeklyRunning().Criteria[0] with { IsRoot = false };
        var dto = HabitTestData.WeeklyRunning() with { Criteria = new List<CriterionCreateDto> { c } };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NoRootCriterion);
    }

    [Fact]
    public async Task Create_Rejects_Daily_Root_Without_CycleTarget()
    {
        // cycle_target is required only in daily mode (the successful-days goal); the
        // cumulative shape needs none (its target is derived).
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { SuccessType = SuccessType.Daily, CycleTarget = null },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.MissingCycleTarget);
    }

    [Fact]
    public async Task Create_Rejects_Root_Without_SuccessType()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto> { dto.Criteria[0] with { SuccessType = null } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Cumulative_Root_With_CycleTarget()
    {
        // A cumulative target is derived from the tree — submitting one is rejected.
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto> { dto.Criteria[0] with { CycleTarget = 42 } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Targets_On_NonRoot()
    {
        var dto = HabitTestData.CompositeHabit();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { CycleTarget = 5 },   // non-root condition gains a target
                dto.Criteria[1],
                dto.Criteria[2],
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Accepts_Composite_Root_In_Daily_Mode()
    {
        // Spec: composite roots are valid in BOTH modes. A day passes exactly when the
        // root group passes (the evaluator uses the pass bit, never the operand count) —
        // this is the morning_exercise / journaling / sleep template shape.
        var dto = HabitTestData.CompositeHabit();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0],
                dto.Criteria[1],
                dto.Criteria[2] with { SuccessType = SuccessType.Daily, CycleTarget = 2 },
            },
        };
        var created = ValueOf(await _controller.Create(dto, CancellationToken.None));
        created.Progress.RootCriterion.SuccessType.Should().Be(SuccessType.Daily);
        created.Progress.RootCriterion.CycleTarget.Should().Be(2);
    }

    [Fact]
    public async Task Create_Composite_Root_Cumulative_Reveals_Derived_Target()
    {
        // Cumulative AND composite root (CompositeHabit shape): cycleTarget is not stored —
        // the threshold field carries the derived required-passing-operands count.
        var created = ValueOf(await _controller.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        created.Progress.RootCriterion.SuccessType.Should().Be(SuccessType.Cumulative);
        created.Progress.RootCriterion.CycleTarget.Should().BeNull();
        created.Progress.RootCriterion.Threshold.Should().Be(2);
        created.Progress.RootCriterion.SuccessfulDays.Should().BeNull();
        created.Progress.RootCriterion.CurrentDayValue.Should().BeNull();
    }

    [Fact]
    public async Task Create_Rejects_Missing_Cycle()
    {
        // M3: an omitted cycle must not silently bind to Daily (the DTO field is nullable).
        var dto = HabitTestData.WeeklyRunning() with { Cycle = null };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.MissingCycle);
    }

    [Fact]
    public async Task Update_Rejects_Missing_Cycle()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var dto = new HabitUpdateDto
        {
            Name = habit.Name,
            Description = habit.Description,
            Cycle = null,
            StartDate = habit.StartDate,
            EndDate = habit.EndDate,
        };
        (await ExpectCodeAsync(() => _controller.Update(habit.Id, dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.MissingCycle);
    }

    [Fact]
    public async Task Create_Rejects_DailyCycle_DayCount_CycleTarget_Not_1()
    {
        var checklist = HabitTestData.DailyChecklist();
        var dto = checklist with
        {
            Criteria = new List<CriterionCreateDto>
            {
                checklist.Criteria[0] with { CycleTarget = 3 },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Weekly_DayCount_CycleTarget_Over_7()
    {
        var dto = HabitTestData.WeeklyExerciseDayCount(cycleTarget: 21);
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Threshold_Not_Positive()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { Threshold = 0 },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    [Fact]
    public async Task Create_Rejects_Not_With_Two_Operands()
    {
        var dto = HabitTestData.CompositeHabit();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0],
                dto.Criteria[1],
                new()
                {
                    Name = "C3", IsRoot = true, CriterionType = CriterionType.Composite,
                    Operator = CompositeOperator.Not,
                    OperandCriterionNames = new List<string> { "C1", "C2" },
                    SuccessType = SuccessType.Cumulative,
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidOperandCount);
    }

    [Fact]
    public async Task Create_Rejects_Circular_Criteria()
    {
        // C1 = AND(C2, C3), C2 = AND(C1, C3): cycle between C1 and C2.
        var dto = HabitTestData.WeeklyRunning() with
        {
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1", IsRoot = false, CriterionType = CriterionType.Composite,
                    Operator = CompositeOperator.And, OperandCriterionNames = new List<string> { "C2", "C3" },
                },
                new()
                {
                    Name = "C2", IsRoot = false, CriterionType = CriterionType.Composite,
                    Operator = CompositeOperator.And, OperandCriterionNames = new List<string> { "C1", "C3" },
                },
                new()
                {
                    Name = "C3", IsRoot = true, CriterionType = CriterionType.Condition,
                    PropertyName = "distance", ItemScope = ItemScope.All,
                    Threshold = 10, SuccessType = SuccessType.Cumulative,
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.CircularCriterion);
    }

    [Fact]
    public async Task Create_Rejects_BaseRate_On_NonNumeric()
    {
        var checklist = HabitTestData.DailyChecklist();
        var item = checklist.Items[0] with
        {
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "done", PropertyType = PropertyType.Boolean, BaseRate = 2.0, Order = 0 },
            },
        };
        var dto = checklist with { Items = new List<ItemCreateDto> { item } };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Fact]
    public async Task Create_Rejects_Negative_BaseRate()
    {
        var running = HabitTestData.WeeklyRunning();
        var item = running.Items[0] with
        {
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "distance", PropertyType = PropertyType.Numeric, BaseRate = -1, Order = 0 },
            },
        };
        var dto = running with { Items = new List<ItemCreateDto> { item } };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Fact]
    public async Task Create_Rejects_List_Property_Without_Uniqueness()
    {
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Items = new List<ItemCreateDto>
            {
                new()
                {
                    Name = "Words", Order = 0,
                    Properties = new List<PropertyCreateDto>
                    {
                        new() { Name = "word", PropertyType = PropertyType.List, Order = 0 },
                    },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidItemUniqueness);
    }

    [Fact]
    public async Task Create_Rejects_Uniqueness_On_NonList()
    {
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Items = new List<ItemCreateDto>
            {
                new()
                {
                    Name = "Run", Order = 0,
                    Properties = new List<PropertyCreateDto>
                    {
                        new()
                        {
                            Name = "distance", PropertyType = PropertyType.Numeric,
                            ItemUniqueness = ItemUniqueness.PerDay, Order = 0,
                        },
                    },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidItemUniqueness);
    }

    [Fact]
    public async Task Create_Rejects_Scope_Names_And_Ids_Both_Set()
    {
        // (Leaves bind only names — there is no property-ID field to collide any more.)
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with
                {
                    ItemScope = ItemScope.Subset,
                    ScopeItemNames = new List<string> { "Run" },
                    ScopeItemIds = new List<int> { 0 },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Same_Property_Name_Different_Type_Across_Items()
    {
        // Habit-wide same-name ⇒ same-type rule: a criterion binds the name across the
        // scope, so one name may not carry two types.
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Items = new List<ItemCreateDto>
            {
                running.Items[0],
                new()
                {
                    Name = "Slices", Order = 1,
                    Properties = new List<PropertyCreateDto>
                    {
                        new() { Name = "distance", PropertyType = PropertyType.List, ItemUniqueness = ItemUniqueness.PerDay, Order = 0 },
                    },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Whole_Open_Ended_Reports_Null_CycleTo_And_Allows_Daily_Mode()
    {
        var running = HabitTestData.WeeklyRunning() with
        {
            Cycle = HabitCycle.Whole,
            StartDate = HabitTestData.Today.AddDays(-2),
        };
        var cumulative = ValueOf(await _controller.Create(running, CancellationToken.None));
        cumulative.Progress.CycleFrom.Should().Be(running.StartDate);
        cumulative.Progress.CycleTo.Should().BeNull();   // spec FR-4.1 — never a sentinel

        // Day-count mode is permitted on whole cycles; cycle_target (days needed) is unbounded.
        var dayCount = running with
        {
            Name = "Lifetime days",
            Criteria = new List<CriterionCreateDto>
            {
                running.Criteria[0] with { SuccessType = SuccessType.Daily, CycleTarget = 100 },
            },
        };
        var created = ValueOf(await _controller.Create(dayCount, CancellationToken.None));
        created.Progress.RootCriterion.SuccessType.Should().Be(SuccessType.Daily);
        created.Progress.RootCriterion.CycleTarget.Should().Be(100);
    }

    [Fact]
    public async Task Create_Rejects_Condition_With_Composite_Fields()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { Operator = CompositeOperator.And, OperandCriterionNames = new List<string> { "C1" } },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Unknown_Property_Name()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { PropertyName = "no_such_property" },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.UnknownOperandName);
    }

    [Fact]
    public async Task Create_Rejects_Unknown_Scope_Item_Name()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with { ItemScope = ItemScope.Subset, ScopeItemNames = new List<string> { "Ghost item" } },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.UnknownOperandName);
    }

    [Fact]
    public async Task Create_Submits_Valid_Template_Shaped_Payload()
    {
        // Sanity: the canonical valid shapes (vocabulary with uniqueness) do create.
        var dto = HabitTestData.DailyVocabulary();
        var action = await _controller.Create(dto, CancellationToken.None);
        action.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    // ── PUT /api/Habits/{id} ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Changes_Basic_Fields()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var dto = new HabitUpdateDto
        {
            Name = "Renamed",
            Description = null,
            Cycle = HabitCycle.Monthly,
            StartDate = HabitTestData.Today.AddDays(-5),
            EndDate = null,
        };

        var result = await _controller.Update(habit.Id, dto, CancellationToken.None);

        result.Value!.Name.Should().Be("Renamed");
        result.Value.Cycle.Should().Be(HabitCycle.Monthly);
    }

    [Fact]
    public async Task Update_Blocks_Cycle_Change_When_Punches_Exist()
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 1);

        var dto = new HabitUpdateDto
        {
            Name = habit.Name,
            Cycle = HabitCycle.Monthly,
            StartDate = habit.StartDate,
        };
        (await ExpectCodeAsync(() => _controller.Update(habit.Id, dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.StructuralChangeBlocked);
    }

    [Fact]
    public async Task Update_Allows_Other_Field_Changes_When_Punches_Exist()
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 1);

        var dto = new HabitUpdateDto
        {
            Name = "Renamed with history",
            Cycle = HabitCycle.Weekly /* unchanged */,
            StartDate = HabitTestData.Today.AddDays(-2),
            EndDate = null,
        };
        var result = await _controller.Update(habit.Id, dto, CancellationToken.None);
        result.Value!.Name.Should().Be("Renamed with history");
    }

    [Fact]
    public async Task Update_Rejects_EndDate_Before_StartDate()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var dto = new HabitUpdateDto
        {
            Name = habit.Name,
            Cycle = habit.Cycle,
            StartDate = HabitTestData.Today,
            EndDate = HabitTestData.Today.AddDays(-2),
        };
        (await ExpectCodeAsync(() => _controller.Update(habit.Id, dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidDateRange);
    }

    [Fact]
    public async Task Update_Other_Users_Habit_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        var dto = new HabitUpdateDto { Name = "x", Cycle = HabitCycle.Daily, StartDate = HabitTestData.Today };
        (await ExpectCodeAsync(() => _controller.Update(habit.Id, dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── POST /api/Habits/{id}/Deactivate ─────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_Sets_Inactive_And_Records_Date()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var result = await _controller.Deactivate(habit.Id, CancellationToken.None);
        result.Value!.State.Should().Be(HabitState.Inactive);
        var stored = await _context.Habits.SingleAsync(h => h.Id == habit.Id);
        stored.State.Should().Be(HabitState.Inactive);
        stored.DeactivatedDate.Should().Be(HabitTestData.Today);   // spec FR-2.4
    }

    [Fact]
    public async Task Deactivated_Habit_Reports_Last_Actual_Cycle_Window()
    {
        // Spec FR-2.2: an inactive habit shows the cycle containing DeactivatedDate − 1,
        // not whatever calendar cycle "today" happens to sit in.
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, start: HabitTestData.Today.AddDays(-30));
        await _controller.Deactivate(habit.Id, CancellationToken.None);

        var anchor = HabitTestData.Today.AddDays(-1);
        var monday = MondayOf(anchor);
        var result = await _controller.GetById(habit.Id, CancellationToken.None);
        result.Value!.Progress.CycleFrom.Should().Be(monday);
        result.Value.Progress.CycleTo.Should().Be(monday.AddDays(6));
    }

    [Fact]
    public async Task Deactivate_Twice_Is_Rejected()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await _controller.Deactivate(habit.Id, CancellationToken.None);
        (await ExpectCodeAsync(() => _controller.Deactivate(habit.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.AlreadyInactive);
    }

    [Fact]
    public async Task Deactivate_Other_Users_Habit_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        (await ExpectCodeAsync(() => _controller.Deactivate(habit.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── DELETE /api/Habits/{id} ──────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Removes_Full_Graph_Including_Punches()
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, HabitTestData.Today, numValue: 3);

        var result = await _controller.Delete(habit.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.Habits.CountAsync()).Should().Be(0);
        (await _context.HabitItems.CountAsync()).Should().Be(0);
        (await _context.ItemProperties.CountAsync()).Should().Be(0);
        (await _context.Criteria.CountAsync()).Should().Be(0);
        (await _context.CriterionConditions.CountAsync()).Should().Be(0);
        (await _context.Punches.CountAsync()).Should().Be(0);
        (await _context.PunchValues.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_Other_Users_Habit_NotFound_And_Leaves_Data()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        // A-L1: bare NotFound() replaced by the notFound ProblemDetails envelope.
        var code = await ExpectCodeAsync(() => _controller.Delete(habit.Id, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.NotFound);
        (await _context.Habits.CountAsync()).Should().Be(1);
    }

    // ── GET /api/Habits/{id}/History ─────────────────────────────────────────────────

    [Fact]
    public async Task History_Returns_Every_Window_Day_Newest_First_With_Latching()
    {
        // New history semantics (spec FR-3.4): EVERY day of the active window inside the
        // range gets a row — punch-less days included, empty-session form — and the
        // cumulative pass latches from the day the running total first reaches the target.
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, start: HabitTestData.Today.AddDays(-30));
        var monday = MondayOf(HabitTestData.Today.AddDays(-7));   // last week: strictly past
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday, numValue: 3.5);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday.AddDays(1), numValue: 6.0);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday.AddDays(2), numValue: 4.0);

        var result = await _controller.GetHistory(habit.Id, monday, monday.AddDays(3), CancellationToken.None);

        result.Value.Should().HaveCount(4);                        // +1 punch-less day row
        result.Value![0].Date.Should().Be(monday.AddDays(3));      // newest first
        result.Value[0].Punches.Should().BeEmpty();                // nothing recorded that day
        result.Value[0].IsSuccessful.Should().BeTrue();            // the pass carries on (latched)
        result.Value[0].Criteria[0].CurrentValue.Should().Be(13.5);
        result.Value[1].Punches.Should().HaveCount(1);
        result.Value[1].Punches[0].Values[0].NumValue.Should().Be(4.0);
        result.Value[1].Punches[0].Values[0].PropertyName.Should().Be("distance");
        // Running totals through each day's end: 3.5 → 9.5 (miss) → 13.5 (hit, latched).
        result.Value[1].IsSuccessful.Should().BeTrue();
        result.Value[2].IsSuccessful.Should().BeFalse();
        result.Value[2].Criteria[0].CurrentValue.Should().Be(9.5);
        // Every row carries the cycle window the day belongs to (weekly Mon–Sun).
        result.Value[1].CycleFrom.Should().Be(monday);
        result.Value[1].CycleTo.Should().Be(monday.AddDays(6));
    }

    [Fact]
    public async Task History_Respects_Date_Filter()
    {
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var monday = MondayOf(HabitTestData.Today);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday, numValue: 1);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday.AddDays(2), numValue: 1);

        var result = await _controller.GetHistory(habit.Id, monday, monday, CancellationToken.None);
        result.Value!.Should().HaveCount(1);
        result.Value![0].Date.Should().Be(monday);
    }

    [Fact]
    public async Task History_Rejects_Inverted_Range()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        (await ExpectCodeAsync(() => _controller.GetHistory(
            habit.Id, HabitTestData.Today, HabitTestData.Today.AddDays(-1), CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidDateRange);
    }

    [Fact]
    public async Task History_Other_Users_Habit_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        (await ExpectCodeAsync(() => _controller.GetHistory(habit.Id, null, null, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    // ── Live progress semantics via GetById (FR-4; deterministic clock: same-week seeds) ──

    [Fact]
    public async Task Progress_Composite_Root_Reports_Passing_Operand_Count()
    {
        var dto = HabitTestData.CompositeHabit();
        var created = ValueOf(await _controller.Create(dto, CancellationToken.None));
        var habit = await _context.Habits.SingleAsync(h => h.Id == created.Id);
        var ch1 = await _context.HabitItems.Include(i => i.Properties)
            .FirstAsync(i => i.HabitId == habit.Id && i.Name == "Chapter 1");
        var pages = ch1.Properties.Single(p => p.Name == "pages");
        var finished = ch1.Properties.Single(p => p.Name == "finished");

        await HabitTestData.SeedPunchAsync(_context, habit, ch1, pages, HabitTestData.Today, numValue: 35);
        await HabitTestData.SeedPunchAsync(_context, habit, ch1, finished, HabitTestData.Today, boolValue: true);

        var result = await _controller.GetById(habit.Id, CancellationToken.None);
        var progress = result.Value!.Progress;

        progress.RootCriterion.Operator.Should().Be(CompositeOperator.And);
        progress.RootCriterion.CurrentValue.Should().Be(1);       // C1 passes, C2 does not (1/2 < 2)
        progress.RootCriterion.Threshold.Should().Be(2);
        progress.RootCriterion.Passed.Should().BeFalse();
        progress.Criteria.Should().OnlyContain(c => !c.IsRoot);
    }

    [Fact]
    public async Task Progress_Boolean_Aggregation_Counts_Items_Once_Per_Window()
    {
        var dto = new HabitCreateDto
        {
            Name = "Book",
            Cycle = HabitCycle.Weekly,
            StartDate = HabitTestData.Today.AddDays(-3),
            Items = new[] { "Ch1", "Ch2", "Ch3" }.Select((n, i) => new ItemCreateDto
            {
                Name = n,
                Order = i,
                Properties = new List<PropertyCreateDto> { new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 } },
            }).ToList(),
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1", IsRoot = true, CriterionType = CriterionType.Condition,
                    PropertyName = "done", ItemScope = ItemScope.All,
                    Threshold = 3, SuccessType = SuccessType.Cumulative,
                },
            },
        };
        var created = ValueOf(await _controller.Create(dto, CancellationToken.None));
        var habit = await _context.Habits.SingleAsync(h => h.Id == created.Id);
        var items = await _context.HabitItems.Include(i => i.Properties).Where(i => i.HabitId == habit.Id).ToListAsync();

        // Chapter 1 done twice across two sessions → the item counts once.
        await HabitTestData.SeedPunchAsync(_context, habit, items[0], items[0].Properties[0], HabitTestData.Today, boolValue: true);
        await HabitTestData.SeedPunchAsync(_context, habit, items[0], items[0].Properties[0], HabitTestData.Today, boolValue: true);

        var result = await _controller.GetById(habit.Id, CancellationToken.None);
        result.Value!.Progress.RootCriterion.CurrentValue.Should().Be(1);
        result.Value.Progress.RootCriterion.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Progress_List_Aggregation_UnionDistinct_Dedupes_Per_Item()
    {
        // Daily habit → the cycle window is exactly today; union_distinct counts distinct
        // entries over the window regardless of item_uniqueness (per_day dedupe is
        // punch-time only).
        var dto = HabitTestData.DailyVocabulary();
        var created = ValueOf(await _controller.Create(dto, CancellationToken.None));
        var habit = await _context.Habits.SingleAsync(h => h.Id == created.Id);
        var item = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id);
        var word = item.Properties.Single(p => p.Name == "word");

        await HabitTestData.SeedPunchAsync(_context, habit, item, word, HabitTestData.Today, listEntries: new[] { "a", "b" });
        await HabitTestData.SeedPunchAsync(_context, habit, item, word, HabitTestData.Today, listEntries: new[] { "b", "c" });

        var result = await _controller.GetById(habit.Id, CancellationToken.None);
        result.Value!.Progress.RootCriterion.CurrentValue.Should().Be(3);  // a,b + b,c deduped within today
    }

    // ── api/Habits/{id}/Shares — per-user invitations ────────────────────────────────

    [Fact]
    public async Task Shares_Empty_Initially_And_Lists_Grants_After_Invite()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);

        (await _controller.GetShares(habit.Id, CancellationToken.None)).Value.Should().BeEmpty();

        await _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = "Bob" }, CancellationToken.None);

        var grants = (await _controller.GetShares(habit.Id, CancellationToken.None)).Value!;
        grants.Should().ContainSingle();
        grants[0].GranteeUserId.Should().Be(Other);
        grants[0].GranteeUserName.Should().Be("Bob");
    }

    [Fact]
    public async Task AddShare_Snapshots_OwnerName_From_Caller_Claims_Not_Body()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        HabitTestData.SetupUserClaims(_controller, User, "Alice");

        await _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = "Bob" }, CancellationToken.None);

        var grant = await _context.HabitShareGrants.SingleAsync();
        grant.OwnerName.Should().Be("Alice"); // taken from the "name" claim, never the body
        grant.OwnerId.Should().Be(User);
    }

    [Fact]
    public async Task AddShare_Duplicate_Grantee_Is_Rejected()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = "Bob" }, CancellationToken.None);

        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = "Bobby" }, CancellationToken.None));

        code.Should().Be(HabitErrorCodes.DuplicateName);
        (await _context.HabitShareGrants.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("", "Bob")]
    [InlineData("habit-user-2", "   ")]
    public async Task AddShare_Blank_Grantee_Fields_Are_Rejected(string granteeId, string granteeName)
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);

        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = granteeId, GranteeUserName = granteeName }, CancellationToken.None));

        code.Should().Be(HabitErrorCodes.InvalidGrantee);
        (await _context.HabitShareGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AddShare_Self_Invite_Is_Rejected()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);

        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = User, GranteeUserName = "Alice" }, CancellationToken.None));

        code.Should().Be(HabitErrorCodes.InvalidGrantee);
    }

    [Fact]
    public async Task AddShare_Other_Users_Habit_NotFound()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);

        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = User, GranteeUserName = "Alice" }, CancellationToken.None));

        code.Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task RemoveShare_Deletes_The_Grant()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var grant = await HabitTestData.SeedGrantAsync(_context, habit, Other);

        var result = await _controller.RemoveShare(habit.Id, grant.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.HabitShareGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RemoveShare_Foreign_GrantId_Is_NotFound_And_Leaves_It_Intact()
    {
        var (mine, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, User);
        var (theirs, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context, Other);
        var foreignGrant = await HabitTestData.SeedGrantAsync(_context, theirs, HabitTestData.ThirdUser);

        // Grant-scoped 404 (matches the habit gate): no leak, no deletion. A-L1: the
        // notFound ProblemDetails envelope, not a bare NotFoundResult body.
        var code = await ExpectCodeAsync(() => _controller.RemoveShare(mine.Id, foreignGrant.Id, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.NotFound);
        (await _context.HabitShareGrants.AnyAsync(g => g.Id == foreignGrant.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Habit_Removes_Its_Share_Grants()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        await HabitTestData.SeedGrantAsync(_context, habit, Other);

        await _controller.Delete(habit.Id, CancellationToken.None);

        (await _context.HabitShareGrants.CountAsync()).Should().Be(0);
    }

    // ── A1: mid-cycle history windows evaluate cumulative from the CYCLE start ──────

    [Fact]
    public async Task History_FromMidCycleDay_EvaluatesCumulativeFromCycleStart()
    {
        // A1 (HIGH): weekly sum ≥ 10; punch 8 Mon + 3 Wed. GET History?from=Wed must see
        // the Monday punch even though it precedes the window — the root ran 11 ≥ 10.
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, start: HabitTestData.Today.AddDays(-30));
        var monday = HabitTestData.LastWeekMonday;
        var wednesday = monday.AddDays(2);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday, numValue: 8);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, wednesday, numValue: 3);

        var result = await _controller.GetHistory(habit.Id, wednesday, wednesday, CancellationToken.None);

        var day = result.Value.Should().ContainSingle().Subject;
        day.CycleFrom.Should().Be(monday);            // the cycle starts before the window
        day.Criteria[0].CurrentValue.Should().Be(11); // 8 (pre-window) + 3
        day.IsSuccessful.Should().BeTrue();           // 11 >= 10
    }

    [Fact]
    public async Task History_FromMidCycleDay_KeepsLatchedPass_WhenNonMonotonicAggregateFell()
    {
        // A1 latching variant: avg ≥ 10 passes Mon (12); a Tue 0.1 drags the mean to 6.05.
        // A window starting Tue replays the cycle prefixes from the (loaded) cycle start
        // and must still report the latched pass.
        var (habit, item, property, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, start: HabitTestData.Today.AddDays(-30));
        var condition = await _context.CriterionConditions.SingleAsync(l => l.Criterion!.HabitId == habit.Id);
        condition.AggregationMode = AggregationMode.Avg;
        await _context.SaveChangesAsync();

        var monday = HabitTestData.LastWeekMonday;
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday, numValue: 12);
        await HabitTestData.SeedPunchAsync(_context, habit, item, property, monday.AddDays(1), numValue: 0.1);

        var result = await _controller.GetHistory(habit.Id, monday.AddDays(1), monday.AddDays(1), CancellationToken.None);

        var day = result.Value!.Single();
        day.Criteria[0].CurrentValue.Should().BeApproximately(6.05, 1e-9);
        day.IsSuccessful.Should().BeTrue("a cycle pass latches from its first passing day, even outside the requested window");
    }

    [Fact]
    public async Task History_NestedCompositeRoot_FromMidCycleDay_PassesOnEarlierCyclePunch()
    {
        // A1 nested-composite variant: root C4 = AND(C3 = OR(C1,C2), C1) cumulative.
        // A Monday distance 8 ≥ 5 passes C1 → C3 → C4; a window from Wed must see it.
        var created = ValueOf(await _controller.Create(HabitTestData.WeeklyNestedComposite(), CancellationToken.None));
        var habit = await _context.Habits.SingleAsync(h => h.Id == created.Id);
        var item = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id);
        var distance = item.Properties.Single(p => p.Name == "distance");
        var monday = HabitTestData.LastWeekMonday;
        await HabitTestData.SeedPunchAsync(_context, habit, item, distance, monday, numValue: 8);

        var wednesday = monday.AddDays(2);
        var result = await _controller.GetHistory(habit.Id, wednesday, wednesday, CancellationToken.None);

        var day = result.Value!.Single();
        var rootRow = day.Criteria.Single(r => r.Name == "C4");
        rootRow.CurrentValue.Should().Be(2);      // C3 and C1 both pass on [Mon..Wed]
        rootRow.Passed.Should().BeTrue();
        day.IsSuccessful.Should().BeTrue();
    }

    // ── A-L4: history clamps its end at deactivation ─────────────────────────────────

    [Fact]
    public async Task History_Excludes_Days_At_Or_After_Deactivation()
    {
        // No derived outcomes on or after DeactivatedDate — the habit's last active day is
        // DeactivatedDate − 1 (FR-2.2/FR-2.4), so even punch-less days (which a NOT-style
        // root would "achieve") stop appearing.
        var deactivatedOn = HabitTestData.Today.AddDays(-2);
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(
            _context, start: HabitTestData.Today.AddDays(-30),
            state: HabitState.Inactive, deactivatedDate: deactivatedOn);

        var result = await _controller.GetHistory(habit.Id, null, null, CancellationToken.None);

        result.Value.Should().NotBeEmpty();
        result.Value!.Max(d => d.Date).Should().Be(deactivatedOn.AddDays(-1));
        result.Value!.Should().OnlyContain(d => d.Date < deactivatedOn);

        // A range fully after the deactivation point yields no rows at all.
        var empty = await _controller.GetHistory(habit.Id, deactivatedOn, HabitTestData.Today, CancellationToken.None);
        empty.Value.Should().BeEmpty();
    }

    // ── A3: duplicate references → 422 duplicateReference (not a PK-violation 500) ───

    [Fact]
    public async Task Create_Rejects_Duplicate_Scope_Item_Names()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0] with
                {
                    ItemScope = ItemScope.Subset,
                    ScopeItemNames = new List<string> { "Run", "Run" },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.DuplicateReference);
    }

    [Fact]
    public async Task Create_Rejects_Duplicate_Operand_Names()
    {
        var dto = HabitTestData.CompositeHabit();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                dto.Criteria[0],
                dto.Criteria[1],
                dto.Criteria[2] with { OperandCriterionNames = new List<string> { "C1", "C1" } },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.DuplicateReference);
        (await _context.Habits.CountAsync()).Should().Be(0);
    }

    // ── A4: omitted criterionType / propertyType → coded 422, never enum 0 ────────────

    [Fact]
    public async Task Create_Rejects_Omitted_PropertyType_In_Nested_Items()
    {
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Items = new List<ItemCreateDto>
            {
                running.Items[0] with
                {
                    Properties = new List<PropertyCreateDto>
                    {
                        new() { Name = "distance", Order = 0 },   // propertyType omitted
                    },
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.MissingPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Omitted_CriterionType_In_Nested_Criteria()
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1", IsRoot = true,   // criterionType omitted
                    PropertyName = "distance", ItemScope = ItemScope.All,
                    Threshold = 10, SuccessType = SuccessType.Cumulative,
                },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.MissingCriterionType);
    }

    [Fact]
    public async Task Create_Accepts_Explicit_Enum_Values_In_Nested_Payloads()
    {
        // A4 flip side: an explicit valid value still creates (the payload builders all
        // pass the now-nullable fields explicitly).
        var created = ValueOf(await _controller.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        created.Cycle.Should().Be(HabitCycle.Weekly);
    }

    // ── A-L2: non-finite numeric bounds rejected ──────────────────────────────────────

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Create_Rejects_NonFinite_Threshold(double threshold)
    {
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto> { dto.Criteria[0] with { Threshold = threshold } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Create_Rejects_NonFinite_CycleTarget(double target)
    {
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Criteria = new List<CriterionCreateDto>
            {
                running.Criteria[0] with { SuccessType = SuccessType.Daily, CycleTarget = target },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Negative_Finite_Threshold_Still()
    {
        // Finite negatives keep their existing rejection (the finiteness guard is additive).
        var dto = HabitTestData.WeeklyRunning();
        dto = dto with
        {
            Criteria = new List<CriterionCreateDto> { dto.Criteria[0] with { Threshold = -2 } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    // ── A-L5: server-side payload limits ──────────────────────────────────────────────

    [Fact]
    public async Task Create_Rejects_Overlong_Names_And_Description()
    {
        var dto = HabitTestData.WeeklyRunning() with { Name = new string('n', 501) };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.ValidationTooLong);

        dto = HabitTestData.WeeklyRunning() with { Description = new string('d', 2001) };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.ValidationTooLong);

        // Boundary: at-limit values pass.
        var atLimit = HabitTestData.WeeklyRunning() with { Name = new string('n', 500), Description = new string('d', 2000) };
        var created = ValueOf(await _controller.Create(atLimit, CancellationToken.None));
        created.Name.Should().HaveLength(500);
    }

    [Fact]
    public async Task Create_Rejects_Overlong_Criterion_PropertyName()
    {
        var running = HabitTestData.WeeklyRunning();
        var dto = running with
        {
            Criteria = new List<CriterionCreateDto>
            {
                running.Criteria[0] with { PropertyName = new string('p', 501) },
            },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.ValidationTooLong);
    }

    [Fact]
    public async Task Create_Rejects_Too_Many_Items_And_Criteria()
    {
        var items = Enumerable.Range(1, HabitRuleValidator.MaxItemsPerHabit + 1)
            .Select(i => new ItemCreateDto
            {
                Name = $"Item{i}",
                Order = i,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 },
                },
            }).ToList();
        var criterion = new CriterionCreateDto
        {
            Name = "C1", IsRoot = true, CriterionType = CriterionType.Condition,
            PropertyName = "done", ItemScope = ItemScope.All,
            Threshold = 1, SuccessType = SuccessType.Cumulative,
        };

        var over = HabitTestData.WeeklyRunning() with { Items = items };
        (await ExpectCodeAsync(() => _controller.Create(over, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.TooManyEntries);

        var manyCriteria = Enumerable.Range(1, HabitRuleValidator.MaxCriteriaPerHabit + 1)
            .Select(i => criterion with { Name = $"C{i}", IsRoot = i == 1 })
            .ToList();
        var overCriteria = HabitTestData.WeeklyRunning() with { Criteria = manyCriteria };
        (await ExpectCodeAsync(() => _controller.Create(overCriteria, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.TooManyEntries);
    }

    [Fact]
    public async Task Create_Accepts_Max_Items_And_Properties()
    {
        // Boundary at-limit: 100 items × 100 properties would be heavy — 100 items with
        // one property each is the meaningful create-path limit test.
        var items = Enumerable.Range(1, HabitRuleValidator.MaxItemsPerHabit)
            .Select(i => new ItemCreateDto
            {
                Name = $"Item{i}",
                Order = i,
                Properties = new List<PropertyCreateDto>
                {
                    new() { Name = "done", PropertyType = PropertyType.Boolean, Order = 0 },
                },
            }).ToList();
        var dto = HabitTestData.WeeklyRunning() with
        {
            Items = items,
            Criteria = new List<CriterionCreateDto>
            {
                new()
                {
                    Name = "C1", IsRoot = true, CriterionType = CriterionType.Condition,
                    PropertyName = "done", ItemScope = ItemScope.All,
                    Threshold = 1, SuccessType = SuccessType.Cumulative,
                },
            },
        };

        var created = ValueOf(await _controller.Create(dto, CancellationToken.None));
        (await _context.HabitItems.CountAsync(i => i.HabitId == created.Id)).Should().Be(HabitRuleValidator.MaxItemsPerHabit);
    }

    [Fact]
    public async Task Create_Rejects_Too_Many_Properties_On_Item()
    {
        var props = Enumerable.Range(1, HabitRuleValidator.MaxPropertiesPerItem + 1)
            .Select(i => new PropertyCreateDto { Name = $"p{i}", PropertyType = PropertyType.Numeric, Order = i })
            .ToList();
        var dto = HabitTestData.WeeklyRunning() with
        {
            Items = new List<ItemCreateDto> { new() { Name = "Run", Order = 0, Properties = props } },
        };
        (await ExpectCodeAsync(() => _controller.Create(dto, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.TooManyEntries);
    }

    // ── A-L7: whitespace-padded self-invite ───────────────────────────────────────────

    [Fact]
    public async Task AddShare_Self_Invite_With_Padded_Grantee_Id_Is_Rejected()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = $"  {User}  ", GranteeUserName = "Alice" }, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.InvalidGrantee);
        (await _context.HabitShareGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AddShare_Rejects_Overlong_Grantee_Fields()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);
        var code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = new string('u', 201), GranteeUserName = "Bob" }, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.ValidationTooLong);

        code = await ExpectCodeAsync(() => _controller.AddShare(habit.Id,
            new ShareGrantCreateDto { GranteeUserId = Other, GranteeUserName = new string('b', 501) }, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.ValidationTooLong);
    }

    // ── A-L8: DeactivatedDate on the habit DTOs ───────────────────────────────────────

    [Fact]
    public async Task DeactivatedDate_Exposed_On_List_And_Detail_After_Deactivate()
    {
        var (habit, _, _, _) = await HabitTestData.SeedMinimalRunningAsync(_context);

        // Active habit: null on both surfaces.
        (await _controller.GetAll(CancellationToken.None)).Value!.Single().DeactivatedDate.Should().BeNull();
        (await _controller.GetById(habit.Id, CancellationToken.None)).Value!.DeactivatedDate.Should().BeNull();

        await _controller.Deactivate(habit.Id, CancellationToken.None);

        // The Deactivate response itself carries it…
        var detail = (await _controller.GetById(habit.Id, CancellationToken.None)).Value!;
        detail.DeactivatedDate.Should().Be(HabitTestData.Today);
        // …and so does the list.
        (await _controller.GetAll(CancellationToken.None)).Value!.Single().DeactivatedDate
            .Should().Be(HabitTestData.Today);
    }

    /// <summary>Monday of the ISO week containing the date (mirrors the window rule).</summary>
    private static DateOnly MondayOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}
