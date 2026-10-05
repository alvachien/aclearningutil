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

/// <summary>
/// POST / GET / PUT / DELETE on the item-properties sub-resource. PropertyType and
/// ItemUniqueness are immutable after creation; deletion is blocked ONLY while punch
/// values reference the property — condition criteria bind the property by NAME and survive
/// the deletion (spec FR-2.3). Same-named properties share one type across the habit.
/// See docs/design-habit-api.md § Item Properties.
/// </summary>
public class ItemPropertiesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly HabitsController _habits;
    private readonly ItemPropertiesController _controller;
    private const string User = HabitTestData.TestUser;

    public ItemPropertiesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        var evaluation = new HabitEvaluationService(_context);
        _habits = new HabitsController(_context, evaluation, new Mock<ILogger<HabitsController>>().Object);
        _controller = new ItemPropertiesController(_context, evaluation);
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

    /// <summary>Creates a single-item weekly "Run" habit; returns (habitId, itemId, distanceProperty).</summary>
    private async Task<(int habitId, int itemId, ItemProperty distance)> SeedRunningAsync()
    {
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        var item = await _context.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habit.Id);
        return (habit.Id, item.Id, item.Properties.Single(p => p.Name == "distance"));
    }

    // ── POST ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Adds_Numeric_Property_With_BaseRate()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();

        var result = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "hours", PropertyType = PropertyType.Numeric, BaseRate = 0.5, Order = 1,
        }, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status201Created);
        var createdDto = Unwrap(result);
        createdDto.Name.Should().Be("hours");
        createdDto.BaseRate.Should().Be(0.5);
    }

    [Fact]
    public async Task Create_Adds_List_Property_With_Uniqueness()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var result = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "exercise", PropertyType = PropertyType.List, ItemUniqueness = ItemUniqueness.PerCycle, Order = 1,
        }, CancellationToken.None);
        Unwrap(result).ItemUniqueness.Should().Be(ItemUniqueness.PerCycle);
    }

    [Fact]
    public async Task Create_Rejects_DuplicateName()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = distance.Name, PropertyType = PropertyType.Numeric, Order = 2,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public async Task Create_Rejects_BaseRate_On_Boolean()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "flag", PropertyType = PropertyType.Boolean, BaseRate = 2, Order = 2,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Fact]
    public async Task Create_Rejects_NonPositive_BaseRate()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "count", PropertyType = PropertyType.Numeric, BaseRate = 0, Order = 2,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Fact]
    public async Task Create_Rejects_List_Without_Uniqueness_And_NonList_With_Uniqueness()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "items", PropertyType = PropertyType.List, Order = 2,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidItemUniqueness);

        (await ExpectCodeAsync(() => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "count", PropertyType = PropertyType.Numeric,
            ItemUniqueness = ItemUniqueness.PerDay, Order = 3,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.InvalidItemUniqueness);
    }

    [Fact]
    public async Task Create_Wrong_Item_Path_NotFound()
    {
        var (habitId, _, _) = await SeedRunningAsync();
        (await ExpectCodeAsync(() => _controller.Create(habitId, 9999, new PropertyCreateDto
        {
            Name = "x", PropertyType = PropertyType.Boolean, Order = 0,
        }, CancellationToken.None))).Should().Be(HabitErrorCodes.NotFound);
    }

    /// <summary>Adds a second item directly (the sub-resource tests focus on properties).</summary>
    private async Task<(int habit, int item)> SeedSecondItemAsync(int habitId, string itemName,
        string propertyName, PropertyType type)
    {
        var h = await _context.Habits.SingleAsync(x => x.Id == habitId);
        var item = new HabitItem { Habit = h, OwnerId = User, Name = itemName, Order = 5, CreatedAt = DateTime.UtcNow };
        item.Properties.Add(new ItemProperty
        {
            Item = item, Habit = h, OwnerId = User, Name = propertyName,
            PropertyType = type, Order = 0, CreatedAt = DateTime.UtcNow,
        });
        _context.HabitItems.Add(item);
        await _context.SaveChangesAsync();
        return (habitId, item.Id);
    }

    [Fact]
    public async Task Create_Rejects_SameNameProperty_WithDifferentType_AcrossItems()
    {
        // Habit-wide same-name ⇒ same-type rule: "pace" is boolean on another item.
        var (habitId, itemId, _) = await SeedRunningAsync();
        await SeedSecondItemAsync(habitId, "Evening run", "pace", PropertyType.Boolean);

        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "pace", PropertyType = PropertyType.Numeric, Order = 3,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }

    [Fact]
    public async Task Create_Allows_SameNameProperty_WithSameType_AcrossItems()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        await SeedSecondItemAsync(habitId, "Evening run", "minutes", PropertyType.Numeric);

        var result = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "minutes", PropertyType = PropertyType.Numeric, Order = 3,
        }, CancellationToken.None);
        ((ObjectResult)result.Result!).StatusCode.Should().Be(StatusCodes.Status201Created);
    }

    // ── A4: omitted propertyType → coded 422, never silent boolean=0 ──────────────────

    [Fact]
    public async Task Create_Rejects_Omitted_PropertyType()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "mystery",   // propertyType omitted entirely
            Order = 3,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.MissingPropertyType);
        (await _context.ItemProperties.CountAsync(p => p.Name == "mystery")).Should().Be(0);
    }

    [Fact]
    public async Task Create_Accepts_Explicit_PropertyType_Still()
    {
        // A4 flip side: an explicit valid value works (covered by the happy-path tests —
        // asserted again here with a nested item-create shape).
        var (habitId, itemId, _) = await SeedRunningAsync();
        var created = Unwrap(await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "minutes", PropertyType = PropertyType.Numeric, Order = 4,
        }, CancellationToken.None));
        created.PropertyType.Should().Be(PropertyType.Numeric);
    }

    [Fact]
    public async Task ItemCreate_Rejects_Omitted_Nested_PropertyType()
    {
        // The nested item-create path enforces the same missingPropertyType rule.
        var habit = ValueOf(await _habits.Create(HabitTestData.WeeklyRunning(), CancellationToken.None));
        var items = new HabitItemsController(_context, new HabitEvaluationService(_context));
        HabitTestData.SetupUserClaims(items, User);

        var act = () => items.Create(habit.Id, new ItemCreateDto
        {
            Name = "Bad nested",
            Order = 1,
            Properties = new List<PropertyCreateDto>
            {
                new() { Name = "x", Order = 0 },   // propertyType omitted
            },
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.MissingPropertyType);
    }

    // ── A-L2: non-finite numeric bounds ───────────────────────────────────────────────

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Create_Rejects_NonFinite_BaseRate(double baseRate)
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "weird", PropertyType = PropertyType.Numeric, BaseRate = baseRate, Order = 5,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public async Task Update_Rejects_NonFinite_BaseRate(double baseRate)
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var act = () => _controller.Update(habitId, itemId, distance.Id, new PropertyUpdateDto
        {
            Name = distance.Name, BaseRate = baseRate, Order = 0,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    // ── A-L5: name length limits (design DDL max lengths) ─────────────────────────────

    [Fact]
    public async Task Create_Rejects_Overlong_Name_And_Accepts_At_Limit()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var act = () => _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = new string('n', HabitRuleValidator.MaxNameLength + 1),
            PropertyType = PropertyType.Boolean, Order = 6,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.ValidationTooLong);

        var atLimit = Unwrap(await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = new string('n', HabitRuleValidator.MaxNameLength),
            PropertyType = PropertyType.Boolean, Order = 6,
        }, CancellationToken.None));
        atLimit.Name.Should().HaveLength(HabitRuleValidator.MaxNameLength);
    }

    // ── GET ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_Returns_Property_With_Aggregates()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var h = await _context.Habits.SingleAsync(x => x.Id == habitId);
        var item = await _context.HabitItems.Include(i => i.Properties).SingleAsync(i => i.Id == itemId);
        await HabitTestData.SeedPunchAsync(_context, h, item, distance, HabitTestData.Today, numValue: 2.5);

        var result = await _controller.GetById(habitId, itemId, distance.Id, CancellationToken.None);

        result.Value!.Name.Should().Be("distance");
        result.Value.CurrentCycleValue.Should().Be(2.5);
        result.Value.TodayValue.Should().Be(2.5);
    }

    [Fact]
    public async Task GetById_Property_Belongs_To_Other_Item_NotFound()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var otherItemId = itemId + 12345;   // an item id that cannot exist in this habit

        (await ExpectCodeAsync(() => _controller.GetById(habitId, otherItemId, distance.Id, CancellationToken.None)))
            .Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task GetById_TodayValue_Null_For_Unpunched_Property()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var result = await _controller.GetById(habitId, itemId, distance.Id, CancellationToken.None);
        result.Value!.TodayValue.Should().BeNull();
        result.Value.CurrentCycleValue.Should().Be(0);
    }

    // ── PUT ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_Changes_Name_Order_And_BaseRate()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var result = await _controller.Update(habitId, itemId, distance.Id, new PropertyUpdateDto
        {
            Name = "km", BaseRate = 2.0, Order = 5,
        }, CancellationToken.None);

        result.Value!.Name.Should().Be("km");
        result.Value.BaseRate.Should().Be(2.0);
        result.Value.Order.Should().Be(5);
    }

    [Fact]
    public async Task Update_Can_Clear_BaseRate()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var created = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "reps", PropertyType = PropertyType.Numeric, BaseRate = 1.5, Order = 0,
        }, CancellationToken.None);

        var updated = await _controller.Update(habitId, itemId, Unwrap(created).Id, new PropertyUpdateDto
        {
            Name = "reps", BaseRate = null, Order = 0,
        }, CancellationToken.None);

        updated.Value!.BaseRate.Should().BeNull();
    }

    [Fact]
    public async Task Update_BaseRate_On_Boolean_IsRejected()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var boolProp = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "flag", PropertyType = PropertyType.Boolean, Order = 0,
        }, CancellationToken.None);

        var act = () => _controller.Update(habitId, itemId, Unwrap(boolProp).Id, new PropertyUpdateDto
        {
            Name = "flag", BaseRate = 1.0, Order = 0,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidBaseRate);
    }

    [Fact]
    public async Task Update_Rename_Conflict_IsRejected()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var other = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "minutes", PropertyType = PropertyType.Numeric, Order = 1,
        }, CancellationToken.None);

        var act = () => _controller.Update(habitId, itemId, Unwrap(other).Id, new PropertyUpdateDto
        {
            Name = distance.Name, Order = 1,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.DuplicateName);
    }

    // ── DELETE ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Succeeds_When_Unreferenced()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        var extra = await _controller.Create(habitId, itemId, new PropertyCreateDto
        {
            Name = "tempo", PropertyType = PropertyType.Numeric, Order = 1,
        }, CancellationToken.None);

        var result = await _controller.Delete(habitId, itemId, Unwrap(extra).Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        (await _context.ItemProperties.CountAsync(p => p.Name == "tempo")).Should().Be(0);
    }

    [Fact]
    public async Task Delete_Blocked_When_Punch_Values_Reference_It()
    {
        var (habitId, itemId, distance) = await SeedRunningAsync();
        var h = await _context.Habits.SingleAsync(x => x.Id == habitId);
        var item = await _context.HabitItems.Include(i => i.Properties).SingleAsync(i => i.Id == itemId);
        await HabitTestData.SeedPunchAsync(_context, h, item, distance, HabitTestData.Today, numValue: 1);

        var code = await ExpectCodeAsync(() => _controller.Delete(habitId, itemId, distance.Id, CancellationToken.None));
        code.Should().Be(HabitErrorCodes.ItemHasPunches);
    }

    [Fact]
    public async Task Delete_Allowed_When_Condition_Criterion_Binds_Its_Name()
    {
        // Spec FR-2.3 (revised): the criterion binds the property NAME — deleting the
        // last row defining it is allowed without punches; the criterion survives and
        // simply stops passing (its scope no longer defines the name).
        var (habitId, itemId, distance) = await SeedRunningAsync();

        var result = await _controller.Delete(habitId, itemId, distance.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        var condition = await _context.CriterionConditions.AsNoTracking()
            .SingleAsync(l => l.Criterion!.HabitId == habitId);
        condition.PropertyName.Should().Be("distance");

        var progress = ValueOf(await _habits.GetById(habitId, CancellationToken.None)).Progress.RootCriterion;
        progress.CurrentValue.Should().Be(0);
        progress.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Update_Rename_ToSameName_DifferentType_AcrossItems_IsRejected()
    {
        var (habitId, itemId, _) = await SeedRunningAsync();
        await SeedSecondItemAsync(habitId, "Evening run", "pace", PropertyType.Boolean);
        var distance = (await _context.ItemProperties
            .Where(p => p.ItemId == itemId).ToListAsync())
            .Single(p => p.Name == "distance");

        var act = () => _controller.Update(habitId, itemId, distance.Id, new PropertyUpdateDto
        {
            Name = "pace", Order = 0,
        }, CancellationToken.None);
        (await ExpectCodeAsync(act)).Should().Be(HabitErrorCodes.InvalidPropertyType);
    }
}
