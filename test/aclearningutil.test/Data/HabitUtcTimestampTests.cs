using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;

namespace aclearningutil.test.Data;

/// <summary>
/// L5: SQLite has no instant type — DateTime columns read back with Kind=Unspecified,
/// which made response JSON omit the trailing 'Z' and left the UI guessing the zone.
/// The habit entities' timestamp properties carry a read-side UTC converter; this is
/// verified against REAL SQLite (the InMemory provider never loses the Kind).
/// </summary>
public class HabitUtcTimestampTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aclearningutil-utc-{Guid.NewGuid():N}.db");

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task HabitTimestamps_ReloadFromSqlite_AsUtc_AndSerializeWithZuluSuffix()
    {
        var createdAt = new DateTime(2026, 9, 1, 12, 34, 56, 789, DateTimeKind.Utc);
        var punchedAt = new DateTime(2026, 9, 2, 8, 0, 0, 0, DateTimeKind.Utc);

        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();

            var habit = new Habit
            {
                OwnerId = "u1",
                Name = "H",
                Cycle = HabitCycle.Daily,
                StartDate = new DateOnly(2026, 9, 1),
                State = HabitState.Active,
                CreatedAt = createdAt,
            };
            var item = new HabitItem { Habit = habit, OwnerId = "u1", Name = "Run", Order = 0, CreatedAt = createdAt };
            var property = new ItemProperty
            {
                Item = item,
                Habit = habit,
                OwnerId = "u1",
                Name = "distance",
                PropertyType = PropertyType.Numeric,
                Order = 0,
                CreatedAt = createdAt,
            };
            item.Properties.Add(property);
            habit.Items.Add(item);
            habit.Criteria.Add(new Criterion
            {
                Habit = habit,
                OwnerId = "u1",
                Name = "C1",
                CriterionType = CriterionType.Condition,
                IsRoot = true,
                SuccessType = SuccessType.Cumulative,
                CreatedAt = createdAt,
                // Leaves bind by NAME; cumulative roots carry no stored cycleTarget.
                Condition = new CriterionCondition { PropertyName = "distance", Threshold = 10, ItemScope = ItemScope.All },
            });
            habit.Criteria.Add(new Criterion
            {
                Habit = habit,
                OwnerId = "u1",
                Name = "C0",
                CriterionType = CriterionType.Condition,
                CreatedAt = createdAt,
                Condition = new CriterionCondition { PropertyName = "distance", Threshold = 1, ItemScope = ItemScope.All },
            });
            ctx.Habits.Add(habit);
            await ctx.SaveChangesAsync();

            ctx.Punches.Add(new Punch
            {
                HabitId = habit.Id,
                ItemId = item.Id,
                OwnerId = "u1",
                PunchedAt = punchedAt,
                PunchDate = new DateOnly(2026, 9, 2),
                CreatedAt = createdAt,
                Values = { new PunchValue { PropertyId = property.Id, NumValue = 5 } },
            });
            await ctx.SaveChangesAsync();
        }

        // Fresh context → genuine provider round-trip (no change-tracker shortcut).
        using (var ctx = CreateContext())
        {
            var habit = await ctx.Habits.SingleAsync(h => h.OwnerId == "u1");
            habit.CreatedAt.Should().Be(createdAt);
            habit.CreatedAt.Kind.Should().Be(DateTimeKind.Utc, "the read-side converter restores the instant's zone");

            var item = await ctx.HabitItems.SingleAsync(i => i.HabitId == habit.Id);
            item.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            var property = await ctx.ItemProperties.SingleAsync(p => p.ItemId == item.Id);
            property.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            var criterion = await ctx.Criteria.SingleAsync(c => c.HabitId == habit.Id && c.IsRoot);
            criterion.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

            var punch = await ctx.Punches.Include(p => p.Values).SingleAsync(p => p.HabitId == habit.Id);
            punch.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
            punch.PunchedAt.Kind.Should().Be(DateTimeKind.Utc);
            punch.PunchedAt.Should().Be(punchedAt);

            // Response JSON now ends with Z (System.Text.Json writes Kind=Utc instants as
            // ISO-8601 zulu) — what the UI's Date parsing needs for zone-correct display.
            var json = JsonSerializer.Serialize(punch.PunchedAt);
            json.Should().EndWith("Z\"");
            JsonSerializer.Serialize(habit.CreatedAt).Should().EndWith("Z\"");
        }
    }

    [Fact]
    public async Task StorageFormat_StaysTheSame_WallClockUtcTextWithoutOffset()
    {
        // The converter must only re-mark the Kind on read — the stored bytes are unchanged
        // (UTC wall clock, no offset suffix), so pre-fix rows stay readable and comparable.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
            ctx.Habits.Add(new Habit
            {
                OwnerId = "u1",
                Name = "H",
                Cycle = HabitCycle.Daily,
                StartDate = new DateOnly(2026, 9, 1),
                State = HabitState.Active,
                CreatedAt = new DateTime(2026, 9, 1, 10, 20, 30, DateTimeKind.Utc),
            });
            await ctx.SaveChangesAsync();
        }

        using var verify = CreateContext();
        var raw = await verify.Database
            .SqlQueryRaw<string>("SELECT substr(CreatedAt, 1, 30) AS \"Value\" FROM Habits").ToListAsync();
        raw.Should().ContainSingle();
        raw[0].Should().Be("2026-09-01 10:20:30");
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
