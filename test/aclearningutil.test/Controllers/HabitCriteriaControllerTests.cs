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

public class HabitCriteriaControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly HabitsController _habits;
    private readonly HabitCriteriaController _controller;
    private const string User = HabitTestData.TestUser;
    private const string Other = HabitTestData.OtherUser;

    public HabitCriteriaControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _habits = new HabitsController(_context, evaluation, new Mock<ILogger<HabitsController>>().Object);
        _controller = new HabitCriteriaController(_context);
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

    private async Task<(int habitId, ItemProperty distance, Criterion root)> SeedRunningAsync()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        var item = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id);
        var distance = item.Properties.Single(p => p.Name == "distance");
        var root = await _context.Criteria.Include(c => c.Condition).SingleAsync(c => c.HabitId == habit.Id && c.IsRoot);
        return (habit.Id, distance, root);
    }

    // ── GET ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_Returns_Full_Criterion_Definitions()
    {
        var (habitId, _, _) = await SeedRunningAsync();

        var result = await _controller.GetAll(habitId, CancellationToken.None);

        var dtos = result.Value!;
        dtos.Should().ContainSingle();
        var c1 = dtos.Single();
        c1.IsRoot.Should().BeTrue();
        c1.CriterionType.Should().Be(CriterionType.Condition);
        c1.PropertyName.Should().Be("distance");
        c1.ItemScope.Should().Be(ItemScope.All);
        c1.Threshold.Should().Be(10);
        c1.SuccessType.Should().Be(SuccessType.Cumulative);
        // Cumulative roots carry no cycleTarget — the target is derived from the tree.
        c1.CycleTarget.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_Composite_Habit_Shapes_Operands()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var result = await _controller.GetAll(habit.Id, CancellationToken.None);

        var root = result.Value!.Single(c => c.IsRoot);
        root.Operator.Should().Be(CompositeOperator.And);
        root.OperandCriterionIds.Should().HaveCount(2);
        result.Value!.Single(c => c.Name == "C1").PropertyName.Should().Be("pages");
    }

    [Fact]
    public async Task GetAll_Other_Users_Habit_NotFound()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        HabitTestData.SetupUserClaims(_controller, Other);
        (await ExpectCodeAsync(() => _controller.GetAll(habitId, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetById_Single_Criterion()
    {
        var (habitId, _, root) = await SeedRunningAsync();
        var result = await _controller.GetById(habitId, root.Id, CancellationToken.None);
        result.Value!.Name.Should().Be("C1");
    }

    [Fact]
    public async Task Endpoints_Without_User_Claim_Unauthorized()
    {
        // A-L1: unauthenticated → HabitException(401, unauthenticated) via the filter.
        HabitTestData.SetupUserClaims(_controller, string.Empty);

        var ex = await Assert.ThrowsAsync<HabitException>(() => _controller.GetAll(1, CancellationToken.None));
        ex.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        ex.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        var createEx = await Assert.ThrowsAsync<HabitException>(() => _controller.Create(
            1, new CriterionCreateDto { Name = "x", CriterionType = CriterionType.Condition }, CancellationToken.None));
        createEx.Code.Should().Be(HabitErrorCodes.Unauthenticated);

        foreach (var act in new Func<Task>[]
        {
            () => _controller.GetById(1, 1, CancellationToken.None),
            () => _controller.Update(1, 1, new CriterionUpdateDto { Name = "x" }, CancellationToken.None),
            () => _controller.Delete(1, 1, CancellationToken.None),
        })
        {
            var e = await Assert.ThrowsAsync<HabitException>(act);
            e.Code.Should().Be(HabitErrorCodes.Unauthenticated);
        }
    }

    // ── POST ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Adds_NonRoot_Condition_By_Name()
    {
        // Leaves bind the property by NAME on the standalone path too (no property IDs).
        var (habitId, _, _) = await SeedRunningAsync();

        var action = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.All,
            Threshold = 5,
        }, CancellationToken.None);

        action.Result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status201Created);
        var createdDto = Unwrap(action);
        createdDto.Name.Should().Be("C2");
        createdDto.PropertyName.Should().Be("distance");

        var condition = await _context.CriterionConditions.SingleAsync(l => l.CriterionId == createdDto.Id);
        condition.Threshold.Should().Be(5);
        condition.PropertyName.Should().Be("distance");
    }

    [Fact]
    public async Task Create_AggregationMode_Stored_And_Defaults_Are_Leaked_As_Null()
    {
        var (habitId, _, _) = await SeedRunningAsync();

        var explicitMode = Unwrap(await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            AggregationMode = AggregationMode.Max,
            Threshold = 5,
        }, CancellationToken.None));
        explicitMode.AggregationMode.Should().Be(AggregationMode.Max);

        var defaultMode = Unwrap(await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 5,
        }, CancellationToken.None));
        defaultMode.AggregationMode.Should().BeNull();   // null = the type default (Sum)

        // A mode incompatible with the property type is rejected.
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C4",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            AggregationMode = AggregationMode.UnionDistinct,
            Threshold = 5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Scope_And_Operand_Names_Outside_HabitCreate()
    {
        // Only scope-item / operand refs are name-based during HabitCreate; conditions always
        // bind by property name and stay valid here.
        var (habitId, _, root) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionNames = new List<string> { root.Name },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);

        var item = await _context.HabitItems.FirstAsync(i => i.HabitId == habitId);
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            ScopeItemNames = new List<string> { item.Name },
            Threshold = 5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Undefined_CriterionType()
    {
        // M3 defense-in-depth for the standalone criterion path.
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = false,
            CriterionType = (CriterionType)77,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_DuplicateName()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C1",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_Targets_On_NonRoot()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 5,
            CycleTarget = 3,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidTarget);

        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 5,
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Rejects_Condition_Missing_PropertyName()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Rejects_Unknown_Property_Name_And_Scope_Ids()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "no_such_property",
            Threshold = 5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);

        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            ScopeItemIds = new List<int> { 123456 },
            Threshold = 5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task Create_Rejects_Name_Not_In_Subset_Scope()
    {
        // Spec: the bound property must exist on at least one item in the scope.
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var appendix = new HabitItem { HabitId = habit.Id, OwnerId = User, Name = "Appendix", Order = 9, CreatedAt = DateTime.UtcNow };
        appendix.Properties.Add(new ItemProperty
        {
            Item = appendix, HabitId = habit.Id, OwnerId = User, Name = "flag",
            PropertyType = PropertyType.Boolean, Order = 0, CreatedAt = DateTime.UtcNow,
        });
        _context.HabitItems.Add(appendix);
        await _context.SaveChangesAsync();

        (await ExpectCodeAsync(() => _controller.Create(habit.Id, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "pages",
            ItemScope = ItemScope.Subset,
            ScopeItemIds = new List<int> { appendix.Id },
            Threshold = 5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.UnknownOperandName);
    }

    [Fact]
    public async Task Create_Rejects_Bad_Threshold()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 0,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    [Fact]
    public async Task Create_Rejects_Non_Integer_Boolean_Threshold()
    {
        // Boolean criteria count items — the threshold must be a whole number.
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyExerciseDayCount(), CancellationToken.None));
        (await ExpectCodeAsync(() => _controller.Create(habit.Id, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "done",
            Threshold = 1.5,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    [Fact]
    public async Task Create_Root_Switch_Clears_Previous_Root_Atomically()
    {
        var (habitId, _, previousRoot) = await SeedRunningAsync();

        var created = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = true,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 20,
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);

        Unwrap(created).IsRoot.Should().BeTrue();
        var roots = await _context.Criteria.CountAsync(c => c.HabitId == habitId && c.IsRoot);
        roots.Should().Be(1);

        // H1b: the demoted row loses its root-only fields too — ValidateTargets forbids
        // successType/cycleTarget on non-root criteria, so retained values would be forbidden state.
        var demoted = await _context.Criteria.SingleAsync(c => c.Id == previousRoot.Id);
        demoted.IsRoot.Should().BeFalse();
        demoted.SuccessType.Should().BeNull();
        demoted.CycleTarget.Should().BeNull();

        var demotedDto = (await _controller.GetById(habitId, previousRoot.Id, CancellationToken.None)).Value!;
        demotedDto.IsRoot.Should().BeFalse();
        demotedDto.SuccessType.Should().BeNull();
        demotedDto.CycleTarget.Should().BeNull();
    }

    [Fact]
    public async Task Create_Root_Switch_Clears_SuccessType_Of_Previous_Daily_Root_Too()
    {
        // The day-count shape's previous root carries BOTH root fields — both must clear.
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyExerciseDayCount(), CancellationToken.None));
        var previousRoot = await _context.Criteria.SingleAsync(c => c.HabitId == habit.Id && c.IsRoot);
        previousRoot.SuccessType.Should().Be(SuccessType.Daily);   // precondition
        previousRoot.CycleTarget.Should().Be(5);

        var added = await _controller.Create(habit.Id, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = true,
            CriterionType = CriterionType.Condition,
            PropertyName = "done",
            Threshold = 2,
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);

        Unwrap(added).IsRoot.Should().BeTrue();
        var demoted = await _context.Criteria.SingleAsync(c => c.Id == previousRoot.Id);
        demoted.IsRoot.Should().BeFalse();
        demoted.SuccessType.Should().BeNull();
        demoted.CycleTarget.Should().BeNull();
    }

    [Fact]
    public async Task Create_Daily_Root_Missing_CycleTarget_Is_Rejected()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = true,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 20,
            SuccessType = SuccessType.Daily,
            // cycleTarget omitted
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.MissingCycleTarget);
    }

    [Fact]
    public async Task Create_Root_Missing_SuccessType_Is_Rejected()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = true,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 20,
            // successType omitted — the root must select a mode explicitly
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Create_Composite_Validates_Operands()
    {
        var (habitId, _, root) = await SeedRunningAsync();

        // unknown operand id
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X1",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Or,
            OperandCriterionIds = new List<int> { root.Id, 999999 },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);

        // NOT with two operands
        (await ExpectCodeAsync(() => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X2",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { root.Id, root.Id },
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidOperandCount);

        // valid NOT — succeeds
        var ok = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X3",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { root.Id },
        }, CancellationToken.None);
        Unwrap(ok).Name.Should().Be("X3");
    }

    // ── PUT ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Changes_Threshold_And_Name()
    {
        var (habitId, _, root) = await SeedRunningAsync();

        var result = await _controller.Update(habitId, root.Id, new CriterionUpdateDto
        {
            Name = "WeeklyGoal",
            IsRoot = true,
            PropertyName = "distance",
            ItemScope = ItemScope.All,
            Threshold = 15,
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);

        result.Value!.Name.Should().Be("WeeklyGoal");
        result.Value.Threshold.Should().Be(15);
    }

    [Fact]
    public async Task Update_Clearing_Root_Without_New_Root_IsRejected()
    {
        var (habitId, _, root) = await SeedRunningAsync();
        var act = () => _controller.Update(habitId, root.Id, new CriterionUpdateDto
        {
            Name = root.Name,
            IsRoot = false,
            PropertyName = "distance",
            Threshold = 10,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.RootRequired);
    }

    [Fact]
    public async Task Update_Promoting_NonRoot_Switches_Role()
    {
        // H1b (PUT path): the demoted daily root must lose BOTH root fields in DB and DTO.
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyExerciseDayCount(), CancellationToken.None));
        var previousRoot = await _context.Criteria.SingleAsync(c => c.HabitId == habit.Id && c.IsRoot);
        var added = await _controller.Create(habit.Id, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "done",
            Threshold = 5,
        }, CancellationToken.None);

        await _controller.Update(habit.Id, Unwrap(added).Id, new CriterionUpdateDto
        {
            Name = "C2",
            IsRoot = true,
            PropertyName = "done",
            Threshold = 5,
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);

        var demoted = await _context.Criteria.SingleAsync(c => c.Id == previousRoot.Id);
        demoted.IsRoot.Should().BeFalse();
        demoted.SuccessType.Should().BeNull();
        demoted.CycleTarget.Should().BeNull();

        (await _context.Criteria.SingleAsync(c => c.Id == Unwrap(added).Id)).IsRoot.Should().BeTrue();

        var demotedDto = (await _controller.GetById(habit.Id, previousRoot.Id, CancellationToken.None)).Value!;
        demotedDto.SuccessType.Should().BeNull();
        demotedDto.CycleTarget.Should().BeNull();
    }

    [Fact]
    public async Task Create_Allows_Composite_Root_In_Daily_Mode()
    {
        // Spec revision: composite roots are valid in BOTH modes — a day passes exactly
        // when the root group passes (the evaluator uses the pass bit, not the operand count).
        var (habitId, _, root) = await SeedRunningAsync();
        var created = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X9",
            IsRoot = true,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { root.Id },
            SuccessType = SuccessType.Daily,
            CycleTarget = 3,   // 3 successful days (weekly: ≤ 7)
        }, CancellationToken.None);

        var dto = Unwrap(created);
        dto.SuccessType.Should().Be(SuccessType.Daily);
        dto.CycleTarget.Should().Be(3);
        var stored = await _context.Criteria.SingleAsync(c => c.Id == dto.Id);
        stored.SuccessType.Should().Be(SuccessType.Daily);
    }

    [Fact]
    public async Task Create_Rejects_CycleTarget_On_Cumulative_Composite_Root()
    {
        // The flip side: a cumulative root's target is derived — never input.
        var (habitId, _, root) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X9",
            IsRoot = true,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { root.Id },
            SuccessType = SuccessType.Cumulative,
            CycleTarget = 3,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidTarget);
    }

    [Fact]
    public async Task Update_Allows_Composite_Root_Daily_Mode()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var all = await _controller.GetAll(habit.Id, CancellationToken.None);
        var root = all.Value!.Single(c => c.IsRoot);

        var result = await _controller.Update(habit.Id, root.Id, new CriterionUpdateDto
        {
            Name = root.Name,
            IsRoot = true,
            Operator = CompositeOperator.And,
            OperandCriterionIds = root.OperandCriterionIds,
            SuccessType = SuccessType.Daily,
            CycleTarget = 2,   // two successful days per week
        }, CancellationToken.None);

        result.Value!.SuccessType.Should().Be(SuccessType.Daily);
        result.Value.CycleTarget.Should().Be(2);
    }

    [Fact]
    public async Task Create_Rejects_Daily_Composite_Root_Missing_CycleTarget()
    {
        // daily mode still requires cycle_target (the successful-days goal) on ANY root.
        var (habitId, _, root) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X8",
            IsRoot = true,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { root.Id },
            SuccessType = SuccessType.Daily,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.MissingCycleTarget);
    }

    [Fact]
    public async Task Create_Subset_Without_ScopeItemIds_Uses_ScopeItemsRequired()
    {
        // L1: was mislabelled as the generic notFound code on a 422.
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            ScopeItemIds = new List<int>(),
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.ScopeItemsRequired);

        var actNull = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(actNull)).Should().Be(HabitErrorCodes.ScopeItemsRequired);
    }

    [Fact]
    public async Task Update_Operands_Creating_Cycle_AreRejected()
    {
        // Composite habit: C1/C2 conditions, C3 root = AND(C1, C2).
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var criteria = await _controller.GetAll(habit.Id, CancellationToken.None);
        var c1 = criteria.Value!.Single(c => c.Name == "C1");
        var c3 = criteria.Value!.Single(c => c.Name == "C3");

        // Add C4 = NOT(C3) — legal while C3 does not reference C4…
        await _controller.Create(habit.Id, new CriterionCreateDto
        {
            Name = "C4",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.Not,
            OperandCriterionIds = new List<int> { c3.Id },
        }, CancellationToken.None);

        // …then re-point C3's operands to [C1, C4]: C3→C4→C3 cycle — rejected.
        var refreshed = await _controller.GetAll(habit.Id, CancellationToken.None);
        var act = () => _controller.Update(habit.Id, c3.Id, new CriterionUpdateDto
        {
            Name = "C3",
            IsRoot = true,
            Operator = CompositeOperator.And,
            OperandCriterionIds = new List<int> { c1.Id, refreshed.Value!.Single(c => c.Name == "C4").Id },
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.CircularCriterion);
    }

    [Fact]
    public async Task Update_DailyCycle_DayCount_Keeps_CycleTarget_1_Rule()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.DailyChecklist(), CancellationToken.None));
        var root = await _context.Criteria.Include(c => c.Condition).SingleAsync(c => c.HabitId == habit.Id && c.IsRoot);

        var act = () => _controller.Update(habit.Id, root.Id, new CriterionUpdateDto
        {
            Name = "C1",
            IsRoot = true,
            PropertyName = root.Condition!.PropertyName,
            Threshold = 4,
            SuccessType = SuccessType.Daily,
            CycleTarget = 2,   // invalid for a daily-cycle habit
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidTarget);
    }

    // ── A3: duplicate references → 422 duplicateReference before any insert ──────────

    [Fact]
    public async Task Create_Rejects_Duplicate_Scope_Item_Ids()
    {
        // A duplicated scope id would violate the (CriterionConditionId, ItemId) PK → 500.
        var (habitId, _, _) = await SeedRunningAsync();
        var item = await _context.HabitItems.FirstAsync(i => i.HabitId == habitId);
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            ScopeItemIds = new List<int> { item.Id, item.Id },
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateReference);
        (await _context.Criteria.CountAsync(c => c.Name == "C2")).Should().Be(0);
    }

    [Fact]
    public async Task Create_Rejects_Duplicate_Operand_Ids()
    {
        // AND with the same operand twice would violate the (CompositeId, OperandCriterionId) PK.
        var (habitId, _, root) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X9",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.And,
            OperandCriterionIds = new List<int> { root.Id, root.Id },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateReference);
    }

    [Fact]
    public async Task Update_Rejects_Duplicate_Operand_Ids()
    {
        // The PUT path validates the same reference lists via the shared standalone validators.
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var all = await _controller.GetAll(habit.Id, CancellationToken.None);
        var c3 = all.Value!.Single(c => c.Name == "C3");
        var c1 = all.Value!.Single(c => c.Name == "C1");

        var act = () => _controller.Update(habit.Id, c3.Id, new CriterionUpdateDto
        {
            Name = "C3",
            IsRoot = true,
            Operator = CompositeOperator.And,
            OperandCriterionIds = new List<int> { c1.Id, c1.Id },
            SuccessType = SuccessType.Cumulative,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateReference);
    }

    [Fact]
    public async Task Create_Rejects_Too_Many_Operand_Ids()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var ids = Enumerable.Range(1, HabitRuleValidator.MaxReferencesPerCriterion + 1).ToList();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "X8",
            IsRoot = false,
            CriterionType = CriterionType.Composite,
            Operator = CompositeOperator.And,
            OperandCriterionIds = ids,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.TooManyEntries);
    }

    // ── A4: omitted criterionType → coded 422, never silent condition=0 ───────────────

    [Fact]
    public async Task Create_Rejects_Omitted_CriterionType()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C9",
            IsRoot = false,
            // CriterionType omitted entirely
            PropertyName = "distance",
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.MissingCriterionType);
    }

    [Fact]
    public async Task Create_Accepts_Explicit_CriterionType()
    {
        // A4 flip side: the standalone add with an explicit valid type still works
        // (Create_Adds_NonRoot_Condition_By_Name above is the composite proof for PUT-side
        // shapes; this guards the create side directly).
        var (habitId, _, _) = await SeedRunningAsync();
        var created = Unwrap(await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C10",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 5,
        }, CancellationToken.None));
        created.CriterionType.Should().Be(CriterionType.Condition);
    }

    [Fact]
    public async Task Create_Rejects_Overlong_PropertyName()
    {
        // A-L5 wire bound on the standalone path (stored column max length 500).
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C12",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = new string('p', HabitRuleValidator.MaxNameLength + 1),
            Threshold = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.ValidationTooLong);
    }

    // ── A-L2: non-finite numeric bounds ───────────────────────────────────────────────

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Create_Rejects_NonFinite_Threshold(double threshold)
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C11",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = threshold,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidThreshold);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Root_Is_Protected()
    {
        var (habitId, _, root) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Delete(habitId, root.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.RootCriterionProtected);
    }

    [Fact]
    public async Task Delete_InUse_Operand_Is_Blocked()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.CompositeHabit(), CancellationToken.None));
        var all = await _controller.GetAll(habit.Id, CancellationToken.None);
        var c1 = all.Value!.Single(c => c.Name == "C1");

        (await ExpectCodeAsync(() => _controller.Delete(habit.Id, c1.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.CriterionInUse);
    }

    [Fact]
    public async Task Delete_Unreferenced_NonRoot_Succeeds_And_Removes_Detail_Rows()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        var extra = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C2",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            Threshold = 3,
        }, CancellationToken.None);
        var otherExtra = await _controller.Create(habitId, new CriterionCreateDto
        {
            Name = "C3",
            IsRoot = false,
            CriterionType = CriterionType.Condition,
            PropertyName = "distance",
            ItemScope = ItemScope.Subset,
            ScopeItemIds = new List<int> { (await _context.HabitItems.FirstAsync(i => i.HabitId == habitId)).Id },
            Threshold = 4,
        }, CancellationToken.None);

        var result = await _controller.Delete(habitId, Unwrap(otherExtra).Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.Criteria.CountAsync(c => c.Name == "C3")).Should().Be(0);
        (await _context.CriterionConditions.CountAsync(l => l.CriterionId == Unwrap(otherExtra).Id)).Should().Be(0);
        (await _context.CriterionConditionScopeItems.CountAsync(s => s.CriterionConditionId == Unwrap(otherExtra).Id)).Should().Be(0);
        // C2 and the root are untouched.
        (await _context.Criteria.CountAsync(c => c.Id == Unwrap(extra).Id)).Should().Be(1);
    }

    [Fact]
    public async Task Delete_Other_Users_Criterion_NotFound()
    {
        var (habitId, _, root) = await SeedRunningAsync();
        HabitTestData.SetupUserClaims(_controller, Other);
        (await ExpectCodeAsync(() => _controller.Delete(habitId, root.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }
}
