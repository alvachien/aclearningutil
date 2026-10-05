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
/// The UserLoginHistories table and its (UserId, LoginDate) unique index must reach
/// pre-existing databases through HabitSchemaBootstrap's generic Phase-1 DDL replay
/// (the same vehicle that carried IX_Punches_OwnerId — see HabitSchemaBootstrapTests),
/// and the one-row-per-user-per-day invariant must be enforced by real SQLite.
/// </summary>
public class UserLoginHistorySchemaBootstrapTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aclearningutil-loginhist-{Guid.NewGuid():N}.db");

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
    public async Task EnsureCreated_Creates_LoginHistory_Table_And_UniqueIndex()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        Tables().Should().Contain("UserLoginHistories");
        (await Indexes()).Should().Contain("IX_UserLoginHistories_UserId_LoginDate");
    }

    [Fact]
    public async Task Bootstrap_Recreates_Dropped_LoginHistory_Table_And_Index_On_ExistingDatabase()
    {
        // Simulate a deployed database that predates the feature: EnsureCreated does NOT
        // add a table to an existing database, so only the Phase-1 replay can bring it.
        using (var ctx = CreateContext())
        {
            ctx.Database.EnsureCreated();
#pragma warning disable EF1002 // Fixed table name constant, not user input.
            ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS \"UserLoginHistories\"");
#pragma warning restore EF1002
        }
        Tables().Should().NotContain("UserLoginHistories");           // precondition (DROP TABLE took its index too)
        (await Indexes()).Should().NotContain("IX_UserLoginHistories_UserId_LoginDate");

        var messages = new List<string>();
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, new RecordingLogger(messages));
        }

        Tables().Should().Contain("UserLoginHistories",
            "startup bootstrap replays the model DDL onto pre-existing databases");
        (await Indexes()).Should().Contain("IX_UserLoginHistories_UserId_LoginDate");
        // The genuinely NEW objects are called out at Information level by name.
        messages.Should().Contain(m => m.Contains("created UserLoginHistories", StringComparison.OrdinalIgnoreCase)
            || m.Contains("created \"UserLoginHistories\""));

        // Idempotence: a second run recreates nothing and leaves a single index.
        using (var ctx = CreateContext())
        {
            await HabitSchemaBootstrap.EnsureTablesAsync(ctx, NullLogger.Instance);
        }
        (await Indexes()).Count(n => n == "IX_UserLoginHistories_UserId_LoginDate").Should().Be(1);
    }

    [Fact]
    public async Task UniqueIndex_Rejects_Second_Row_For_Same_User_And_Day()
    {
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();

        var day = new DateOnly(2026, 10, 5);
        ctx.UserLoginHistories.AddRange(
            new UserLoginHistory
            {
                UserId = "u1", LoginDate = day,
                FirstLoginAt = new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc),
                LastLoginAt = new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc),
                LoginCount = 1,
            },
            new UserLoginHistory
            {
                UserId = "u1", LoginDate = day,
                FirstLoginAt = new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc),
                LastLoginAt = new DateTime(2026, 10, 5, 2, 0, 0, DateTimeKind.Utc),
                LoginCount = 1,
            });

        var act = async () => await ctx.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.And.InnerException.Should().BeOfType<SqliteException>()
            .Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT — unique index hit

        // A different user on the same day is fine.
        ctx.ChangeTracker.Clear();
        ctx.UserLoginHistories.Add(new UserLoginHistory
        {
            UserId = "u2", LoginDate = day,
            FirstLoginAt = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc),
            LastLoginAt = new DateTime(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc),
            LoginCount = 1,
        });
        await ctx.SaveChangesAsync();
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
