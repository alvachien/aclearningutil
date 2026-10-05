using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Utility;

namespace aclearningutil.test.Data;

/// <summary>
/// Validates the habit-tracking schema against REAL SQLite (not the InMemory provider):
/// EnsureCreated DDL validity (string enums, DateOnly, the filtered partial unique index),
/// the single-root DB-level invariant, and the bootstrap that adds the tables to
/// pre-existing deployed databases.
/// </summary>
public class HabitSchemaBootstrapTests : IDisposable
{
    private static readonly string[] HabitTables =
    {
        "Habits", "HabitItems", "ItemProperties", "Criteria", "CriterionLeaves",
        "CriterionLeafScopeItems", "CriterionComposites", "CriterionCompositeOperands",
        "Punches", "PunchValues",
    };

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aclearningutil-habit-{Guid.NewGuid():N}.db");

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new AppDbContext(options);
    }

    private List<string> Tables()
    {
        using var ctx = CreateContext();
        return ctx.Database
            .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'table'")
            .ToList();
    }

    private async Task<List<string>> Indexes()
    {
        using var ctx = CreateContext();
        return await ctx.Database
            .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'index'")
            .ToListAsync();
    }

    [Fact]
    public async Task EnsureCreated_Creates_All_Habit_Tables_And_RootIndex()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var tables = Tables();
        tables.Should().Contain(HabitTables);

        var indexes = await Indexes();
        indexes.Should().Contain("IX_Criteria_HabitId_Root",
            "the partial unique index enforcing the single-root invariant must be emitted");
        indexes.Should().Contain("IX_Punches_OwnerId",
            "A-L3: the tenant-key index on Punches is part of the model and its DDL");
    }

    [Fact]
    public async Task Bootstrap_Recreates_Dropped_Punch_OwnerIndex_On_ExistingDatabase()
    {
        // A-L3 + A5b: an index added to the model must reach databases that predate it —
        // the Phase-1 replay of the model script (CREATE INDEX IF NOT EXISTS) is the vehicle.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
        }
        using (var ctx = CreateContext())
        {
#pragma warning disable EF1002 // Fixed index name constant, not user input.
            ctx.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS \"IX_Punches_OwnerId\"");
#pragma warning restore EF1002
        }
        (await Indexes()).Should().NotContain("IX_Punches_OwnerId");   // precondition

        var messages = new List<string>();
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, new RecordingLogger(messages));
        }

        (await Indexes()).Should().Contain("IX_Punches_OwnerId",
            "startup bootstrap re-applies model indexes to pre-existing databases");
        // A5a: a genuinely NEW object is called out at Information level with its name.
        messages.Should().Contain(m =>
            m.Contains("created IX_Punches_OwnerId", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Phase 1: created \"IX_Punches_OwnerId\""));

        // Idempotence: a second run is a no-op, index stays single.
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, NullLogger.Instance);
        }

        (await Indexes()).Count(n => n == "IX_Punches_OwnerId").Should().Be(1);
    }

    [Fact]
    public async Task Bootstrap_Logs_Phase1_Applied_And_Skipped_Statements()
    {
        // A5a: startup logging names the created objects and the skipped non-DDL statements.
        var messages = new List<string>();
        var logger = new RecordingLogger(messages);

        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, logger);
        }

        messages.Should().Contain(m => m.Contains("Phase 1 (create)"),
            "the per-phase summary is logged");
        messages.Should().Contain(m => m.Contains("IX_Punches_OwnerId"),
            "applied statements are logged with their object names");
        // Fresh database: every DDL statement is a skipped no-op via IF NOT EXISTS — still
        // "applied" (executed); the non-DDL seed INSERTs are the skipped ones.
        messages.Should().Contain(m => m.Contains("skipped non-DDL statement"),
            "non-DDL statements (seed INSERTs) are logged as skipped");
    }

    /// <summary>Minimal ILogger that records formatted log messages (any level).</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _messages;
        public RecordingLogger(List<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task PartialUniqueIndex_Rejects_Second_Root_In_Same_Habit()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var habit = new Habit
        {
            OwnerId = "u1",
            Name = "H",
            Cycle = HabitCycle.Weekly,
            StartDate = new DateOnly(2026, 9, 1),
            State = HabitState.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Habits.Add(habit);
        await ctx.SaveChangesAsync();

        ctx.Criteria.AddRange(
            new Criterion { HabitId = habit.Id, OwnerId = "u1", Name = "R1", CriterionType = CriterionType.Condition, IsRoot = true, SuccessType = SuccessType.Daily, CycleTarget = 1, CreatedAt = DateTime.UtcNow },
            new Criterion { HabitId = habit.Id, OwnerId = "u1", Name = "R2", CriterionType = CriterionType.Condition, IsRoot = true, SuccessType = SuccessType.Daily, CycleTarget = 1, CreatedAt = DateTime.UtcNow });

        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.And.InnerException.Should().BeOfType<SqliteException>()
            .Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT — partial unique index hit
    }

    [Fact]
    public async Task UniqueIndexes_Reject_Duplicate_Names_Scoped_PerHabit()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var habit = new Habit { OwnerId = "u1", Name = "H", Cycle = HabitCycle.Daily, StartDate = new DateOnly(2026, 9, 1), CreatedAt = DateTime.UtcNow };
        var item = new HabitItem { Habit = habit, OwnerId = "u1", Name = "Run", Order = 0, CreatedAt = DateTime.UtcNow };
        habit.Items.Add(item);
        item.Properties.Add(new ItemProperty { Item = item, Habit = habit, OwnerId = "u1", Name = "distance", PropertyType = PropertyType.Numeric, Order = 0, CreatedAt = DateTime.UtcNow });
        ctx.Habits.Add(habit);
        await ctx.SaveChangesAsync();

        var dupeItem = new HabitItem { HabitId = habit.Id, OwnerId = "u1", Name = "Run", Order = 1, CreatedAt = DateTime.UtcNow };
        ctx.HabitItems.Add(dupeItem);
        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Bootstrap_Recreates_Dropped_Habit_Tables_Keeps_Other_Data()
    {
        // Arrange: a database created WITHOUT habit tables — like a deployed DB predating the feature.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
            ctx.TtsMappings.Add(new TtsMapping { Sentence = "keep me", FileName = "a.mp3", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();

            // Child-first so FK enforcement during the drop stays satisfied.
#pragma warning disable EF1002 // Table names are hard-coded constants from HabitTables, not user input.
            foreach (var table in HabitTables.Reverse())
            {
                ctx.Database.ExecuteSqlRaw($"DROP TABLE IF EXISTS \"{table}\"");
            }
#pragma warning restore EF1002
        }

        Tables().Should().NotContain("Habits");

        // Act: the startup bootstrap.
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, NullLogger.Instance);
        }

        // Assert: habit tables exist and are queryable; pre-existing data untouched.
        Tables().Should().Contain(HabitTables);
        using (var ctx = CreateContext())
        {
            (await ctx.Habits.AnyAsync()).Should().BeFalse();
            (await ctx.TtsMappings.AnyAsync(t => t.Sentence == "keep me")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Bootstrap_Completes_Half_Applied_Schema_Even_When_Habits_Table_Exists()
    {
        // L3: the old probe early-returned as soon as Habits existed, so a replay that died
        // after the first table (leaving the rest missing) was treated as complete forever.
        // Recreate exactly that state: ONLY the Habits table, everything else dropped.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
            ctx.TtsMappings.Add(new TtsMapping { Sentence = "keep", FileName = "a.mp3", CreatedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();

#pragma warning disable EF1002 // Child tables only, hard-coded from HabitTables — not user input.
            foreach (var table in HabitTables.Reverse().Where(t => t != "Habits"))
            {
                ctx.Database.ExecuteSqlRaw($"DROP TABLE IF EXISTS \"{table}\"");
            }
#pragma warning restore EF1002
        }

        Tables().Should().Contain("Habits");
        Tables().Should().NotContain("PunchValues");

        // Act: the startup bootstrap — must detect Habits alone is NOT a complete schema.
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, NullLogger.Instance);
        }

        // Assert: every habit table AND the filtered partial unique index now exist,
        // pre-existing rows untouched.
        Tables().Should().Contain(HabitTables);
        (await Indexes()).Should().Contain("IX_Criteria_HabitId_Root",
            "the replay must also recreate the partial root index a half-applied schema was missing");
        using (var verify = CreateContext())
        {
            (await verify.TtsMappings.AnyAsync(t => t.Sentence == "keep")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Bootstrap_IsIdempotent_On_FreshAndExistingDatabases()
    {
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
        }

        // No-op when the tables already exist (fresh DB path).
        await HabitSchemaBootstrap.EnsureTablesAsync(CreateContext(), NullLogger.Instance);
        await HabitSchemaBootstrap.EnsureTablesAsync(CreateContext(), NullLogger.Instance);

        Tables().Should().Contain(HabitTables);
    }

    [Fact]
    public async Task Bootstrap_Upgrades_Legacy_Habit_Schema_And_IsIdempotent()
    {
        // A database on the superseded (v0.3.13-lineage) habit schema: Habits without
        // DeactivatedDate, Criteria with DailyTarget and no SuccessType, CriterionLeaves
        // keyed by a NOT NULL PropertyId FK, plus scope-item children.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
#pragma warning disable EF1002 // Fixed legacy DDL constants — no user input anywhere here.
            foreach (var table in HabitTables.Reverse())
            {
                ctx.Database.ExecuteSqlRaw($"DROP TABLE IF EXISTS \"{table}\"");
            }
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "Habits" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Habits" PRIMARY KEY AUTOINCREMENT,
                    "OwnerId" TEXT NOT NULL, "Name" TEXT NOT NULL, "Description" TEXT NULL, "Cycle" TEXT NOT NULL,
                    "StartDate" TEXT NOT NULL, "EndDate" TEXT NULL, "State" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL)
                """);
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "HabitItems" ("Id" INTEGER NOT NULL CONSTRAINT "PK_HabitItems" PRIMARY KEY AUTOINCREMENT,
                    "HabitId" INTEGER NOT NULL, "OwnerId" TEXT NOT NULL, "Name" TEXT NOT NULL, "Order" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL, CONSTRAINT "FK_HabitItems_Habits_HabitId" FOREIGN KEY ("HabitId") REFERENCES "Habits" ("Id") ON DELETE CASCADE)
                """);
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "ItemProperties" ("Id" INTEGER NOT NULL CONSTRAINT "PK_ItemProperties" PRIMARY KEY AUTOINCREMENT,
                    "ItemId" INTEGER NOT NULL, "HabitId" INTEGER NOT NULL, "OwnerId" TEXT NOT NULL, "Name" TEXT NOT NULL,
                    "PropertyType" TEXT NOT NULL, "BaseRate" REAL NULL, "ItemUniqueness" TEXT NULL, "Order" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL, CONSTRAINT "FK_ItemProperties_HabitItems_ItemId" FOREIGN KEY ("ItemId") REFERENCES "HabitItems" ("Id") ON DELETE CASCADE)
                """);
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "Criteria" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Criteria" PRIMARY KEY AUTOINCREMENT,
                    "HabitId" INTEGER NOT NULL, "OwnerId" TEXT NOT NULL, "Name" TEXT NOT NULL, "CriterionType" TEXT NOT NULL,
                    "IsRoot" INTEGER NOT NULL, "DailyTarget" REAL NULL, "CycleTarget" REAL NULL, "CreatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_Criteria_Habits_HabitId" FOREIGN KEY ("HabitId") REFERENCES "Habits" ("Id") ON DELETE CASCADE)
                """);
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "CriterionLeaves" ("Id" INTEGER NOT NULL CONSTRAINT "PK_CriterionLeaves" PRIMARY KEY AUTOINCREMENT,
                    "CriterionId" INTEGER NOT NULL, "PropertyId" INTEGER NOT NULL, "ItemScope" TEXT NOT NULL, "Threshold" REAL NOT NULL,
                    CONSTRAINT "FK_CriterionLeaves_Criteria_CriterionId" FOREIGN KEY ("CriterionId") REFERENCES "Criteria" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_CriterionLeaves_ItemProperties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "ItemProperties" ("Id") ON DELETE RESTRICT)
                """);
            ctx.Database.ExecuteSqlRaw("""
                CREATE TABLE "CriterionLeafScopeItems" ("CriterionLeafId" INTEGER NOT NULL, "ItemId" INTEGER NOT NULL,
                    CONSTRAINT "PK_CriterionLeafScopeItems" PRIMARY KEY ("CriterionLeafId", "ItemId"),
                    CONSTRAINT "FK_CLSI_CriterionLeaves_CriterionLeafId" FOREIGN KEY ("CriterionLeafId") REFERENCES "CriterionLeaves" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_CLSI_HabitItems_ItemId" FOREIGN KEY ("ItemId") REFERENCES "HabitItems" ("Id") ON DELETE CASCADE)
                """);
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "Habits" (OwnerId, Name, Cycle, StartDate, State, CreatedAt) VALUES
                    ('u1', 'Legacy A', 'Weekly', '2026-09-01', 'Active', '2026-09-01 00:00:00'),
                    ('u1', 'Legacy B', 'Daily',   '2026-09-01', 'Active', '2026-09-01 00:00:00')
                """);
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "HabitItems" (HabitId, OwnerId, Name, "Order", CreatedAt) VALUES
                    (1, 'u1', 'Run', 0, '2026-09-01 00:00:00'), (2, 'u1', 'Items', 0, '2026-09-01 00:00:00')
                """);
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "ItemProperties" (ItemId, HabitId, OwnerId, Name, PropertyType, "Order", CreatedAt) VALUES
                    (1, 1, 'u1', 'distance', 'Numeric', 0, '2026-09-01 00:00:00'),
                    (2, 2, 'u1', 'done',     'Boolean', 0, '2026-09-01 00:00:00')
                """);
            // Habit 1: cumulative root (DailyTarget NULL) + non-root row. Habit 2: daily root
            // (DailyTarget set) — the legacy day-count spelling. The CriterionType values are
            // the pre-rename enum strings ('Leaf') — the upgrade must rewrite them to 'Condition'.
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "Criteria" (HabitId, OwnerId, Name, CriterionType, IsRoot, DailyTarget, CycleTarget, CreatedAt) VALUES
                    (1, 'u1', 'R1', 'Leaf', 1, NULL, 10,  '2026-09-01 00:00:00'),
                    (1, 'u1', 'R2', 'Leaf', 0, NULL, NULL,'2026-09-01 00:00:00'),
                    (2, 'u1', 'R3', 'Leaf', 1, 1,    5,   '2026-09-01 00:00:00')
                """);
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "CriterionLeaves" (CriterionId, PropertyId, ItemScope, Threshold) VALUES
                    (1, 1, 'All', 10), (2, 2, 'All', 1), (3, 2, 'Subset', 1)
                """);
            ctx.Database.ExecuteSqlRaw("""
                INSERT INTO "CriterionLeafScopeItems" (CriterionLeafId, ItemId) VALUES (3, 2)
                """);
#pragma warning restore EF1002
        }

        // Act: the startup bootstrap replays the model DDL and runs the upgrade.
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, NullLogger.Instance);
        }

        using (var ctx = CreateContext())
        {
            var habitColumns = ctx.Database.SqlQueryRaw<string>(
                "SELECT name AS \"Value\" FROM pragma_table_info('Habits')").ToList();
            habitColumns.Should().Contain("DeactivatedDate");

            var conditionColumns = ctx.Database.SqlQueryRaw<string>(
                "SELECT name AS \"Value\" FROM pragma_table_info('CriterionLeaves')").ToList();
            conditionColumns.Should().Contain("PropertyName").And.Contain("AggregationMode");
            conditionColumns.Should().NotContain("PropertyId", "the rebuilt condition table binds by name");

            var criteria = await ctx.Criteria.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
            criteria.Select(c => c.SuccessType).Should().Equal(
                new SuccessType?[] { SuccessType.Cumulative, null, SuccessType.Daily },
                "root without DailyTarget backfills to Cumulative, non-root stays NULL, root with DailyTarget becomes Daily");
            criteria[2].CycleTarget.Should().Be(5, "daily-mode targets are preserved");
            criteria.Select(c => c.CriterionType).Should().Equal(
                new[] { CriterionType.Condition, CriterionType.Condition, CriterionType.Condition },
                "the legacy 'Leaf' enum strings are rewritten to 'Condition' by the terminology migration");

            var conditions = await ctx.CriterionConditions.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
            conditions.Select(l => l.PropertyName).Should().Equal("distance", "done", "done");
            conditions.Select(l => l.Id).Should().Equal(new[] { 1, 2, 3 }, "row identities survive the rebuild");
            (await ctx.CriterionConditionScopeItems.AsNoTracking().CountAsync(s => s.CriterionConditionId == 3 && s.ItemId == 2))
                .Should().Be(1, "child scope rows are re-attached around the rebuild");

            // The rebuilt condition rows are writable through EF (the legacy NOT NULL
            // PropertyId would otherwise block every new insert).
            var h = await ctx.Habits.SingleAsync(x => x.Name == "Legacy A");
            var distanceItem = await ctx.HabitItems.Include(i => i.Properties)
                .FirstAsync(i => i.HabitId == h.Id);
            ctx.CriterionConditions.Add(new CriterionCondition
            {
                Criterion = new Criterion
                {
                    HabitId = h.Id,
                    OwnerId = "u1",
                    Name = "R-new",
                    CriterionType = CriterionType.Condition,
                    SuccessType = SuccessType.Cumulative,
                    CreatedAt = DateTime.UtcNow,
                },
                PropertyName = "distance",
                ItemScope = ItemScope.All,
                Threshold = 5,
            });
            await ctx.SaveChangesAsync();
        }

        // Idempotence: a second run changes nothing.
        await HabitSchemaBootstrap.EnsureTablesAsync(CreateContext(), NullLogger.Instance);
        using (var verify = CreateContext())
        {
            (await verify.CriterionConditions.CountAsync()).Should().Be(4);
            (await verify.CriterionConditionScopeItems.CountAsync()).Should().Be(1);
            (await verify.Criteria.CountAsync(c => c.SuccessType != null)).Should().Be(3);
            (await verify.Criteria.CountAsync(c => c.CriterionType == CriterionType.Composite)).Should().Be(0);
        }
    }

    [Fact]
    public async Task EnumsAndDates_RoundTrip_AsSpecValues_InSqlite()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var habit = new Habit { OwnerId = "u1", Name = "H", Cycle = HabitCycle.Monthly, StartDate = new DateOnly(2026, 9, 15), CreatedAt = DateTime.UtcNow };
        ctx.Habits.Add(habit);
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

#pragma warning disable EF1003 // Identifier is a locally-generated numeric key, not user input.
        var rawCycle = ctx.Database.SqlQueryRaw<string>(
            "SELECT Cycle AS \"Value\" FROM Habits WHERE Id = " + habit.Id).Single();
#pragma warning restore EF1003
        rawCycle.Should().Be("Monthly", "enum strings are stored via HasConversion<string>");

        var reloaded = await ctx.Habits.SingleAsync(h => h.Id == habit.Id);
        reloaded.Cycle.Should().Be(HabitCycle.Monthly);
        reloaded.StartDate.Should().Be(new DateOnly(2026, 9, 15), "DateOnly round-trips through TEXT");
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
