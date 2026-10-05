using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;
using aclearningutil.Utility;

namespace aclearningutil.test.Services;

/// <summary>
/// M2: the habit exception filter must turn infrastructure failures into RFC 7807 too —
/// unique-index violations (lost races past the app-level pre-check) map to the design's
/// duplicateName, everything else to a 500 problem document (never an empty body).
/// L3: JsonExceptions raised at enum binding map to invalidEnumValue.
/// The SQLite-backed test proves the real provider messages match the mapped signatures.
/// </summary>
public class HabitExceptionFilterTests : IDisposable
{
    private readonly HabitExceptionFilterAttribute _filter = new();
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aclearningutil-filter-{Guid.NewGuid():N}.db");

    private static ExceptionContext ContextFor(Exception exception)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };
    }

    private static ProblemDetails Problem(ExceptionContext context) =>
        ((ObjectResult)context.Result!).Value.Should().BeAssignableTo<ProblemDetails>().Subject;

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new AppDbContext(options);
    }

    // ── duplicateName from unique-index violations ───────────────────────────────────

    [Theory]
    [InlineData("HabitItems.HabitId, HabitItems.Name")]
    [InlineData("ItemProperties.ItemId, ItemProperties.Name")]
    [InlineData("Criteria.HabitId, Criteria.Name")]
    [InlineData("HabitShareGrants.HabitId, HabitShareGrants.GranteeUserId")]
    public void NameIndexViolation_MapsTo422DuplicateName(string constraintColumns)
    {
        // A3: the share-grant (HabitId, GranteeUserId) unique index rides the same
        // mapping — a lost invite race is 422 duplicateName, not a 500.
        var sqlite = new SqliteException(
            $"SQLite Error 19: 'UNIQUE constraint failed: {constraintColumns}'.", 19);
        var context = ContextFor(new DbUpdateException("An error occurred while updating the entries.", sqlite));

        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Extensions["code"].Should().Be(HabitErrorCodes.DuplicateName);
    }

    // ── A-L1: the identity/envelope failures are RFC 7807 too ──────────────────────

    [Fact]
    public void Unauthenticated_HabitException_MapsTo401ProblemWithCode()
    {
        var context = ContextFor(HabitException.Unauthenticated("User ID not found in token."));

        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Title.Should().Be("Unauthorized");
        problem.Extensions["code"].Should().Be(HabitErrorCodes.Unauthenticated);
        problem.Detail.Should().Be("User ID not found in token.");
    }

    [Fact]
    public void NotFound_HabitException_MapsTo404ProblemWithCode()
    {
        var context = ContextFor(HabitException.NotFound("Habit"));

        _filter.OnException(context);

        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Title.Should().Be("Not Found");
        problem.Extensions["code"].Should().Be(HabitErrorCodes.NotFound);
    }

    [Fact]
    public async Task RealSqlite_DuplicateShareGrant_IsMappedTo422ByTheFilter()
    {
        // A3: the (HabitId, GranteeUserId) unique index really fires on SQLite when the
        // app pre-check loses a race — and the filter maps it to duplicateName.
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        var habit = new Habit
        {
            OwnerId = "u1", Name = "H", Cycle = HabitCycle.Weekly,
            StartDate = new DateOnly(2026, 9, 1), State = HabitState.Active, CreatedAt = DateTime.UtcNow,
        };
        ctx.Habits.Add(habit);
        await ctx.SaveChangesAsync();
        ctx.HabitShareGrants.Add(new HabitShareGrant
        {
            HabitId = habit.Id, OwnerId = "u1", GranteeUserId = "u2",
            GranteeUserName = "Bob", OwnerName = "Alice", CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        DbUpdateException? caught = null;
        try
        {
            ctx.HabitShareGrants.Add(new HabitShareGrant
            {
                HabitId = habit.Id, OwnerId = "u1", GranteeUserId = "u2",
                GranteeUserName = "Bobby", OwnerName = "Alice", CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the share-grant unique index really fires on real SQLite");

        var context = ContextFor(caught!);
        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Extensions["code"].Should().Be(HabitErrorCodes.DuplicateName);
    }

    [Fact]
    public void RootPartialIndexViolation_IsNotDuplicateName_AndMapsTo500Problem()
    {
        // "Criteria.HabitId" (no , Criteria.Name) is the single-root partial index — a lost
        // root-switch race, not a name conflict → generic RFC 7807 500.
        var sqlite = new SqliteException(
            "SQLite Error 19: 'UNIQUE constraint failed: Criteria.HabitId'.", 19);
        var context = ContextFor(new DbUpdateException("boom", sqlite));

        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void OtherDbUpdateFailure_MapsToRfc7807_500_NotEmptyBody()
    {
        var context = ContextFor(new DbUpdateException("boom", new InvalidOperationException("unrelated")));

        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Title.Should().Be("Internal Server Error");
        problem.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RealSqlite_DuplicateInsert_IsMappedByTheFilter()
    {
        // End-to-end proof with the real provider: a raw duplicate item row (bypassing the
        // controller's AnyAsync pre-check, i.e. what a concurrent writer does) throws a
        // DbUpdateException whose message the filter maps to duplicateName.
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
        var habit = new Habit
        {
            OwnerId = "u1",
            Name = "H",
            Cycle = HabitCycle.Daily,
            StartDate = new DateOnly(2026, 9, 1),
            State = HabitState.Active,
            CreatedAt = DateTime.UtcNow,
        };
        var item = new HabitItem { Habit = habit, OwnerId = "u1", Name = "Run", Order = 0, CreatedAt = DateTime.UtcNow };
        habit.Items.Add(item);
        ctx.Habits.Add(habit);
        await ctx.SaveChangesAsync();

        DbUpdateException? caught = null;
        try
        {
            ctx.HabitItems.Add(new HabitItem { HabitId = habit.Id, OwnerId = "u1", Name = "Run", Order = 1, CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("the unique index really fires on real SQLite");

        var context = ContextFor(caught!);
        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Extensions["code"].Should().Be(HabitErrorCodes.DuplicateName);
    }

    // ── HabitException (existing behaviour) and JsonException (L3) ──────────────────

    [Fact]
    public void HabitException_StillMapsToItsOwnStatusAndCode()
    {
        var context = ContextFor(HabitException.Unprocessable(HabitErrorCodes.MissingCycle, "cycle required"));

        _filter.OnException(context);

        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Extensions["code"].Should().Be("missingCycle");
    }

    [Fact]
    public void JsonException_WithPathFromEnumBinding_MapsTo422InvalidEnumValue()
    {
        // Real converter failure: the app's own options reject the integer enum value and
        // the raised JsonException carries the offending property path (in the MVC pipeline
        // binding failures are turned into a 400 by the input formatter; this filter is the
        // safety net for any that reach it as unhandled).
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        HabitJsonOptions.ConfigureEnumConverters(options);
        JsonException? thrown = null;
        try
        {
            JsonSerializer.Deserialize<PropertyCreateDto>(
                """{"name":"x","propertyType":99,"order":0}""", options);
        }
        catch (JsonException ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull("the string-only converter must reject integer enum values");
        thrown!.Path.Should().NotBeNullOrEmpty("the filter keys off the property path to tell binding failures apart");

        var context = ContextFor(thrown);
        _filter.OnException(context);

        context.ExceptionHandled.Should().BeTrue();
        var problem = Problem(context);
        problem.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem.Extensions["code"].Should().Be(HabitErrorCodes.InvalidEnumValue);
    }

    [Fact]
    public void JsonException_WithoutPath_IsLeftForTheDefaultPipeline()
    {
        var context = ContextFor(new JsonException("malformed"));

        _filter.OnException(context);

        context.ExceptionHandled.Should().BeFalse();
        context.Result.Should().BeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
