using System.Text.Json;
using FluentAssertions;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Utility;

namespace aclearningutil.test.Models;

/// <summary>
/// Wire-format contract for the habit API (M3): enum members are strings only — the
/// globally registered converter is configured through HabitJsonOptions (the exact code
/// path Program.cs registers), so these tests assert what the MVC JSON pipeline does:
/// integers / unknown strings fail binding (→ 400), omitted cycle binds to null
/// (→ 422 missingCycle at the controller), snake-case strings round-trip.
/// </summary>
public class HabitWireFormatTests
{
    private static JsonSerializerOptions AppLikeOptions()
    {
        // MVC's defaults + the app's shared converter registration.
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        HabitJsonOptions.ConfigureEnumConverters(options);
        return options;
    }

    [Fact]
    public void Integer_PropertyType_IsRejected_ByTheConverter()
    {
        var json = """{"name":"x","propertyType":99,"order":0}""";
        var act = () => JsonSerializer.Deserialize<PropertyCreateDto>(json, AppLikeOptions());
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Zero_Cycle_Integer_IsRejected_NotSilentlyDaily()
    {
        // The classic hazard: cycle 0 == Daily. The converter refuses the number outright.
        var json = """{"name":"H","cycle":0,"startDate":"2026-09-01"}""";
        var act = () => JsonSerializer.Deserialize<HabitCreateDto>(json, AppLikeOptions());
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Omitted_Cycle_Binds_To_Null_Not_Default()
    {
        var json = """{"name":"H","startDate":"2026-09-01","items":[],"criteria":[]}""";
        var dto = JsonSerializer.Deserialize<HabitCreateDto>(json, AppLikeOptions());

        dto.Should().NotBeNull();
        dto!.Cycle.Should().BeNull("the controller turns omission into 422 missingCycle");
    }

    [Fact]
    public void Unknown_Enum_String_IsRejected()
    {
        var json = """{"name":"H","cycle":"yearly","startDate":"2026-09-01"}""";
        var act = () => JsonSerializer.Deserialize<HabitCreateDto>(json, AppLikeOptions());
        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("\"daily\"", HabitCycle.Daily)]
    [InlineData("\"weekly\"", HabitCycle.Weekly)]
    [InlineData("\"monthly\"", HabitCycle.Monthly)]
    [InlineData("\"whole\"", HabitCycle.Whole)]
    public void SnakeCase_Cycle_Strings_Parse(string wire, HabitCycle expected)
    {
        var json = $$"""{"name":"H","cycle":{{wire}},"startDate":"2026-09-01"}""";
        var dto = JsonSerializer.Deserialize<HabitCreateDto>(json, AppLikeOptions());
        dto!.Cycle.Should().Be(expected);
    }

    [Fact]
    public void List_Uniqueness_PerCycle_RoundTrips_And_Serializes_As_Wire_Value()
    {
        var dto = new PropertyCreateDto
        {
            Name = "word",
            PropertyType = PropertyType.List,
            ItemUniqueness = ItemUniqueness.PerCycle,
        };

        var json = JsonSerializer.Serialize(dto, AppLikeOptions());
        json.Should().Contain("\"per_cycle\"");
        json.Should().Contain("\"list\"");

        var back = JsonSerializer.Deserialize<PropertyCreateDto>(json, AppLikeOptions());
        back!.ItemUniqueness.Should().Be(ItemUniqueness.PerCycle);
        back.PropertyType.Should().Be(PropertyType.List);
    }

    [Fact]
    public void Enum_Serialization_Emits_Wire_Values_Not_Numbers()
    {
        var options = AppLikeOptions();
        JsonSerializer.Serialize(HabitCycle.Monthly, options).Should().Be("\"monthly\"");
        JsonSerializer.Serialize(CompositeOperator.And, options).Should().Be("\"and\"");
        JsonSerializer.Serialize(CriterionType.Composite, options).Should().Be("\"composite\"");
        JsonSerializer.Serialize(SuccessType.Daily, options).Should().Be("\"daily\"");
        JsonSerializer.Serialize(SuccessType.Cumulative, options).Should().Be("\"cumulative\"");
        JsonSerializer.Serialize(AggregationMode.EverTrue, options).Should().Be("\"ever_true\"");
        JsonSerializer.Serialize(AggregationMode.UnionDistinct, options).Should().Be("\"union_distinct\"");
        JsonSerializer.Serialize(AggregationMode.Latest, options).Should().Be("\"latest\"");
    }

    [Fact]
    public void Criterion_Payload_RoundTrips_New_Field_Shapes()
    {
        // Leaves bind by NAME; successType/aggregationMode use the spec's wire spellings.
        var json = """
            {"name":"C1","isRoot":true,"criterionType":"condition","propertyName":"done",
             "itemScope":"all","threshold":2,"aggregationMode":"ever_true",
             "successType":"daily","cycleTarget":3}
            """;
        var dto = JsonSerializer.Deserialize<CriterionCreateDto>(json, AppLikeOptions());

        dto!.PropertyName.Should().Be("done");
        dto.AggregationMode.Should().Be(AggregationMode.EverTrue);
        dto.SuccessType.Should().Be(SuccessType.Daily);
        dto.CycleTarget.Should().Be(3);

        var back = JsonSerializer.Serialize(dto, AppLikeOptions());
        back.Should().Contain("\"propertyName\":\"done\"");
        back.Should().Contain("\"successType\":\"daily\"");
        back.Should().Contain("\"aggregationMode\":\"ever_true\"");
    }

    [Fact]
    public void Integer_SuccessType_IsRejected_ByTheConverter()
    {
        var json = """{"name":"C1","isRoot":true,"criterionType":"condition","successType":0}""";
        var act = () => JsonSerializer.Deserialize<CriterionCreateDto>(json, AppLikeOptions());
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Omitted_PropertyType_Binds_To_Null_Not_Boolean_Default()
    {
        // A4: the nullable wire field lets the controller turn omission into 422
        // missingPropertyType (a non-nullable enum would bind "absent" to boolean = 0).
        var json = """{"name":"x","order":0}""";
        var dto = JsonSerializer.Deserialize<PropertyCreateDto>(json, AppLikeOptions());
        dto!.PropertyType.Should().BeNull();

        var explicitDto = JsonSerializer.Deserialize<PropertyCreateDto>(
            """{"name":"x","propertyType":"numeric","order":0}""", AppLikeOptions());
        explicitDto!.PropertyType.Should().Be(PropertyType.Numeric);
    }

    [Fact]
    public void Omitted_CriterionType_Binds_To_Null_Not_Condition_Default()
    {
        // A4 twin on the criterion payload.
        var json = """{"name":"C1","isRoot":true}""";
        var dto = JsonSerializer.Deserialize<CriterionCreateDto>(json, AppLikeOptions());
        dto!.CriterionType.Should().BeNull();

        var explicitDto = JsonSerializer.Deserialize<CriterionCreateDto>(
            """{"name":"C1","isRoot":true,"criterionType":"composite"}""", AppLikeOptions());
        explicitDto!.CriterionType.Should().Be(CriterionType.Composite);
    }

    [Fact]
    public void Overflowing_Double_Literal_Parses_To_Infinity_For_The_App_To_Reject()
    {
        // A-L2 premise: "1e999" does NOT fail binding — System.Text.Json overflows it to
        // +Infinity, which is why the write paths guard with double.IsFinite (the endpoint
        // then rejects with invalidThreshold; see HabitsControllerTests).
        var json = """
            {"name":"C1","isRoot":false,"criterionType":"condition","propertyName":"distance","threshold":1e999}
            """;
        var dto = JsonSerializer.Deserialize<CriterionCreateDto>(json, AppLikeOptions());
        double.IsPositiveInfinity(dto!.Threshold!.Value).Should().BeTrue();
    }

    [Fact]
    public void DeactivatedDate_Serializes_As_YyyyMmDd_OrNull()
    {
        // A-L8: HabitOut.deactivatedDate mirrors the DateOnly convention (yyyy-MM-dd).
        var progress = new ProgressOutDto
        {
            CycleFrom = new DateOnly(2026, 9, 14),
            CycleTo = new DateOnly(2026, 9, 20),
            RootCriterion = new CriterionProgressOutDto { CriterionId = 1, Name = "C1" },
        };
        var active = new HabitOutDto
        {
            Id = 1,
            Name = "H",
            StartDate = new DateOnly(2026, 9, 1),
            Progress = progress,
        };
        JsonSerializer.Serialize(active, AppLikeOptions()).Should().Contain("\"deactivatedDate\":null");

        var deactivated = active with
        {
            State = HabitState.Inactive,
            DeactivatedDate = new DateOnly(2026, 10, 4),
        };
        JsonSerializer.Serialize(deactivated, AppLikeOptions()).Should().Contain("\"deactivatedDate\":\"2026-10-04\"");
    }

    [Fact]
    public void Progress_CycleTo_Serializes_Null_For_Open_Ended_Whole_Habit()
    {
        var progress = new ProgressOutDto
        {
            CycleFrom = new DateOnly(2026, 9, 1),
            CycleTo = null,
            RootCriterion = new CriterionProgressOutDto { CriterionId = 1, Name = "C1" },
        };

        var json = JsonSerializer.Serialize(progress, AppLikeOptions());
        json.Should().Contain("\"cycleTo\":null");
    }
}
