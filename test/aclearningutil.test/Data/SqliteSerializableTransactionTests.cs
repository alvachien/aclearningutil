using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using System.Data;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.Services;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Data;

/// <summary>
/// A2: the habit punch/share write paths now open their transactions explicitly with
/// <see cref="IsolationLevel.Serializable"/>. Verified provider behavior:
/// Microsoft.Data.Sqlite maps Serializable to <c>BEGIN IMMEDIATE</c> — the write lock is
/// taken AT BEGIN, so a second writer cannot read-before-write alongside an in-flight
/// transaction. Notably the pre-fix no-argument overload resolved (via EF's
/// IsolationLevel.Unspecified → Microsoft.Data.Sqlite's static
/// <c>DefaultIsolationLevel = Serializable</c>) to the SAME immediate BEGIN — so the old
/// code was already writer-serializing on this stack; the explicit level does not change
/// today's behavior but PINS the invariant against provider/EF default drift, which is
/// what the design doc promises for the boolean one-row-per-day upsert.
///
/// The shared-cache in-memory harness rejects the contended second BEGIN with
/// SQLITE_LOCKED (error 6, not busy-retried) quickly — used for the deterministic
/// isolation-level proofs — while the invariant-under-concurrency test uses a FILE
/// database like production.
/// </summary>
public class SqliteSerializableTransactionTests : IDisposable
{
    private readonly string _sharedName = Guid.NewGuid().ToString("N");
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"aclearningutil-serializable-{Guid.NewGuid():N}.db");
    private readonly List<AppDbContext> _contexts = [];

    private AppDbContext SharedContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_sharedName};Mode=Memory;Cache=Shared")
            .Options;
        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        _contexts.Add(context);
        return context;
    }

    private AppDbContext FileContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_filePath}")
            .Options;
        var context = new AppDbContext(options);
        _contexts.Add(context);
        return context;
    }

    [Fact]
    public async Task Serializable_Begin_Takes_The_WriteLock_Up_Front()
    {
        using var first = SharedContext();
        await first.Database.EnsureCreatedAsync();

        // BEGIN IMMEDIATE holds the write lock at begin — a second Serializable transaction
        // on another connection cannot even open while the first is in flight.
        await using (var tx = await first.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            using var second = SharedContext();
            var act = async () => await second.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            await act.Should().ThrowAsync<SqliteException>();
            tx.Rollback();
        }

        // After the first releases, a fresh Serializable transaction opens normally.
        using var third = SharedContext();
        await using var retry = await third.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await retry.CommitAsync();
    }

    [Fact]
    public async Task Provider_Default_Begin_Also_Reserves_The_Write_Lock_At_Begin()
    {
        // FINDING (review A2 asked to verify the provider mapping): the no-argument
        // BeginTransactionAsync(ct) overload is NOT deferred on this stack either —
        // EF Core passes IsolationLevel.Unspecified, which Microsoft.Data.Sqlite resolves
        // to its static DefaultIsolationLevel = Serializable → BEGIN IMMEDIATE. So the
        // pre-fix code already serialized writers, and the explicit
        // BeginTransactionAsync(IsolationLevel.Serializable, ct) pins the SAME behavior by
        // intent instead of depending on the provider default. Empirically: a second
        // Serializable begin cannot open while a default-overload transaction is in flight.
        using var first = SharedContext();
        await first.Database.EnsureCreatedAsync();

        await using var deferredDefault = await first.Database.BeginTransactionAsync();
        using var second = SharedContext();
        var act = async () => await second.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await act.Should().ThrowAsync<SqliteException>();
        await deferredDefault.RollbackAsync();
    }

    [Fact]
    public async Task Concurrent_Boolean_Punches_Preserve_One_Row_Per_Day_Invariant()
    {
        // The concrete regression guard for A2: two simultaneous boolean punches for the
        // SAME (property, item, day) against a file database must never leave two live
        // boolean rows. Each request runs its own DbContext + Serializable transaction; the
        // second writer serializes behind the first (BEGIN IMMEDIATE) and upserts the row.
        var habitId = await CreateRunningHabitWithBoolAsync();
        var (habitRow, itemRow, boolPropId) = await LoadTargetsAsync(habitId);

        var act1 = FileContext();
        var act2 = FileContext();
        var c1 = PunchController(act1, HabitTestData.TestUser);
        var c2 = PunchController(act2, HabitTestData.TestUser);

        async Task CreateAsync(HabitPunchesController c)
        {
            await c.Create(habitId, itemRow, new PunchCreateDto
            {
                Values = new List<PropertyValueCreateDto>
                {
                    new() { PropertyId = boolPropId, BoolValue = true },
                },
            }, CancellationToken.None);
        }

        // Fire both truly concurrently. The Serializable transactions serialize: one may
        // surface a transient busy/locked condition (which the controller's own SaveChanges
        // either commits or throws), but the end state must never violate the invariant.
        var results = await Task.WhenAll(
            CaptureAsync(() => CreateAsync(c1)),
            CaptureAsync(() => CreateAsync(c2)));

        _ = habitRow;   // referenced only to establish the seed graph is committed
        var boolRows = await act1.PunchValues
            .Where(v => v.PropertyId == boolPropId && v.BoolValue != null)
            .ToListAsync();

        boolRows.Should().ContainSingle(
            "the boolean same-day upsert must keep exactly one live value row even when two writers hit the same day");

        // At least one of the two concurrent writes must have succeeded (the other either
        // serialized to an upsert or hit a transient conflict) — no phantom-empty state.
        results.Should().Contain(r => r.Succeeded);

        await act1.DisposeAsync();
        await act2.DisposeAsync();
    }

    private sealed record Attempt(bool Succeeded, string? Error);

    private static async Task<Attempt> CaptureAsync(Func<Task> act)
    {
        try
        {
            await act();
            return new Attempt(true, null);
        }
        catch (Exception ex)
        {
            return new Attempt(false, ex.Message);
        }
    }

    private static HabitPunchesController PunchController(AppDbContext ctx, string user)
    {
        var controller = new HabitPunchesController(ctx, new Mock<ILogger<HabitPunchesController>>().Object);
        HabitTestData.SetupUserClaims(controller, user);
        return controller;
    }

    private async Task<int> CreateRunningHabitWithBoolAsync()
    {
        // Create the habit through the real controller on a file DB so both punch requests
        // target rows already committed by an independent transaction.
        using var ctx = FileContext();
        await ctx.Database.EnsureCreatedAsync();
        var evaluation = new HabitEvaluationService(ctx);
        var habits = new HabitsController(ctx, evaluation, new Mock<ILogger<HabitsController>>().Object);
        HabitTestData.SetupUserClaims(habits, HabitTestData.TestUser);

        var dto = HabitTestData.WeeklyExerciseDayCount();
        await habits.Create(dto, CancellationToken.None);
        var habit = await ctx.Habits.SingleAsync();
        return habit.Id;
    }

    private async Task<(Habit habit, int itemId, int boolPropId)> LoadTargetsAsync(int habitId)
    {
        using var ctx = FileContext();
        var habit = await ctx.Habits.SingleAsync(h => h.Id == habitId);
        var item = await ctx.HabitItems.Include(i => i.Properties).FirstAsync(i => i.HabitId == habitId);
        var boolProp = item.Properties.Single(p => p.Name == "done");
        return (habit, item.Id, boolProp.Id);
    }

    public void Dispose()
    {
        foreach (var ctx in _contexts)
        {
            ctx.Dispose();
        }
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = _filePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
