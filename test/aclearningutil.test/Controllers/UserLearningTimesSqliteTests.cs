using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;

namespace aclearningutil.test.Controllers;

/// <summary>
/// Production-translation safety net for the learning-time stack. The other
/// controller tests run on EF's in-memory provider, which never validates that
/// the LINQ (heartbeat upsert, the leaderboard's GroupBy/Sum/Max aggregates and
/// its NOT IN opt-out sub-query, the DateOnly range filters) actually
/// translates to SQLite SQL — the provider that production runs on. This suite
/// executes the SAME controller calls against a real SQLite database (file in
/// temp, same engine/format as production) so any translation or type-mapping
/// failure fails here instead of on the deployed server.
/// Mirrors HabitSchemaBootstrapTests' file-DB lifecycle and the claims
/// scaffolding of UserLearningTimesControllerTests.
/// </summary>
public sealed class UserLearningTimesSqliteTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AppDbContext _context;
    private readonly UserLearningTimesController _controller;
    private const string TestUserId = "sqlite-user-1";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public UserLearningTimesSqliteTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"kb-times-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();
        _controller = new UserLearningTimesController(
            _context, new Mock<ILogger<UserLearningTimesController>>().Object);
        SetupUserClaims(TestUserId);
    }

    private void SetupUserClaims(string? userId)
    {
        var identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private async Task<LearningTimeDto> BeatAsync(int categoryId, string? userName = null)
    {
        var result = await _controller.RecordBeat(
            new LearningTimeBeatRequest { CategoryId = categoryId, UserName = userName },
            CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        return ((OkObjectResult)result.Result!).Value.As<LearningTimeDto>();
    }

    [Fact]
    public async Task Heartbeat_Upsert_Runs_On_Real_Sqlite_And_Roundtrips_Utc_And_Dateonly()
    {
        var first = await BeatAsync(1, "Alice");
        first.LearningSeconds.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds);
        first.LearnDate.Should().Be(Today);

        // Immediate replay on real SQLite: elapsed rounds to 0 -> deduped, and
        // LastBeatAt survived the TEXT round-trip as UTC (the subtraction in
        // ComputeCredit only behaves like this if the converter works).
        var second = await BeatAsync(1);
        second.LearningSeconds.Should().Be(first.LearningSeconds);
        second.LastBeatAt.Kind.Should().Be(DateTimeKind.Utc);

        // Backdate LastBeatAt 70s through raw SQL — the same storage format
        // production holds — and one more beat must credit exactly ~70s.
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE UserLearningTimes SET LastBeatAt = datetime('now', '-70 seconds') WHERE UserId = {0}",
            TestUserId);
        // The raw UPDATE bypasses the change tracker; drop the tracked (stale)
        // entity so the controller's next query reads the written row — real
        // requests each get a fresh DbContext and never see this situation.
        _context.ChangeTracker.Clear();
        var third = await BeatAsync(1);
        third.LearningSeconds.Should().BeInRange(
            first.LearningSeconds + 69, first.LearningSeconds + 75,
            "server-measured elapsed from the stored UTC text");

        // The unique (UserId, LearnDate, CategoryId) index holds: exactly one row.
        (await _context.UserLearningTimes.CountAsync(t => t.UserId == TestUserId && t.CategoryId == 1))
            .Should().Be(1);
    }

    [Fact]
    public async Task Shared_Day_Cursor_Bootstraps_Once_And_Credits_New_Category_From_Other_Rows_On_Sqlite()
    {
        var first = await BeatAsync(1); // day's first beat: bootstrap
        first.LearningSeconds.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds);

        // Backdate the only row's cursor ~70s through raw SQL (production storage
        // format), then beat a FRESH category: its row must credit the shared day
        // cursor read from the OTHER category's row (~70s), not re-issue the 60s
        // new-row bootstrap — the H1 rule, translated on the production provider.
        await _context.Database.ExecuteSqlRawAsync(
            "UPDATE UserLearningTimes SET LastBeatAt = datetime('now', '-70 seconds') " +
            "WHERE UserId = {0} AND CategoryId = 1",
            TestUserId);
        _context.ChangeTracker.Clear();

        var fresh = await BeatAsync(2);
        fresh.LearningSeconds.Should().BeInRange(
            69, UserLearningTimesController.MaxCreditPerBeatSeconds,
            "a new category credits the shared cursor's elapsed time, never a second bootstrap");

        (await _context.UserLearningTimes.SumAsync(t => t.LearningSeconds)).Should().BeInRange(
            60 + 69, 60 + UserLearningTimesController.MaxCreditPerBeatSeconds,
            "day total: ONE bootstrap plus the one real gap");
    }

    [Fact]
    public async Task Preferences_And_Leaderboard_Aggregates_Translate_And_Run_On_Sqlite()
    {
        await BeatAsync(1, "Alice"); // alice visible: 60s in category 1

        SetupUserClaims("bob");
        await BeatAsync(2, "Bob");
        (await _controller.UpdatePreferences(
            new LearningTimePreferencesDto { LeaderboardOptOut = true }, CancellationToken.None))
            .Result!.Should().BeOfType<OkObjectResult>();
        SetupUserClaims(TestUserId);

        // GroupBy + Sum + Max(string) + OrderBy(aggregate) + Take + NOT IN
        // sub-query — the leaderboard's full shape, on the production provider.
        var board = await _controller.GetLeaderboard(7, 3, CancellationToken.None);
        board.Value.Should().NotBeNull();
        // Exactly one entry: Bob opted out and must be excluded by the NOT IN sub-query.
        board.Value!.Select(e => (e.UserId, e.UserName, e.TotalSeconds)).Should().Equal(
            (TestUserId, "Alice", UserLearningTimesController.FirstBeatCreditSeconds));

        var prefs = await _controller.GetPreferences(CancellationToken.None);
        prefs.Value!.LeaderboardOptOut.Should().BeFalse();

        // Own-window GET with DateOnly range filters on real SQLite storage.
        var rows = await _controller.GetLearningTimes(
            Today.AddDays(-1), Today, 1, CancellationToken.None);
        rows.Value!.Should().HaveCount(1);
        rows.Value![0].CategoryId.Should().Be(1);
    }

    [Fact]
    public async Task Leaderboard_Window_Range_Filter_Excludes_Older_Days_On_Sqlite()
    {
        await BeatAsync(3);
        // A "last week" row for the same user, written directly the way the
        // upsert would (LearnDate as stored TEXT date).
        _context.UserLearningTimes.Add(new UserLearningTime
        {
            UserId = TestUserId,
            LearnDate = Today.AddDays(-10),
            CategoryId = 3,
            LearningSeconds = 500,
            LastBeatAt = DateTime.UtcNow.AddDays(-10),
        });
        await _context.SaveChangesAsync();

        var week = await _controller.GetLeaderboard(7, 3, CancellationToken.None);
        week.Value![0].TotalSeconds.Should().Be(
            UserLearningTimesController.FirstBeatCreditSeconds,
            "the -10d row is outside the window: DateOnly comparison works against TEXT dates");

        var month = await _controller.GetLeaderboard(30, 3, CancellationToken.None);
        month.Value![0].TotalSeconds.Should().Be(
            500 + UserLearningTimesController.FirstBeatCreditSeconds);
    }

    public void Dispose()
    {
        _context.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* temp litter is acceptable */ }
    }
}
