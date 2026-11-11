using System.Security.Claims;
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
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Controllers;

/// <summary>
/// Learning time by user/day/category: POST upserts the caller's beat row with a
/// server-clock increment (capped, deduped, never client-forged), GET serves the
/// caller's own rows inside a bounded window. Scaffolding mirrors
/// <see cref="UserLoginHistoriesControllerTests"/>.
/// </summary>
public class UserLearningTimesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly UserLearningTimesController _controller;
    private const string TestUserId = "test-user-123";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public UserLearningTimesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _controller = new UserLearningTimesController(
            _context, new Mock<ILogger<UserLearningTimesController>>().Object);
        SetupUserClaims(TestUserId);
    }

    /// <summary>Null <paramref name="userId"/> simulates a token without a user-id claim.</summary>
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

    /// <summary>The shared auth contract: ClaimTypes.NameIdentifier with a "sub" fallback.</summary>
    private void SetupSubClaim(string userId)
    {
        var identity = new ClaimsIdentity([new Claim("sub", userId)], "Test");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private async Task<ActionResult<LearningTimeDto>> PostAsync(int categoryId)
    {
        return await _controller.RecordBeat(
            new LearningTimeBeatRequest { CategoryId = categoryId }, CancellationToken.None);
    }

    private async Task<LearningTimeDto> BeatAsync(int categoryId)
    {
        var result = await PostAsync(categoryId);
        result.Result.Should().BeOfType<OkObjectResult>();
        return ((OkObjectResult)result.Result!).Value.As<LearningTimeDto>();
    }

    private async Task SeedAsync(
        string userId, DateOnly day, int categoryId, int seconds, DateTime lastBeatAt,
        string? userName = null)
    {
        _context.UserLearningTimes.Add(new UserLearningTime
        {
            UserId = userId,
            LearnDate = day,
            CategoryId = categoryId,
            LearningSeconds = seconds,
            LastBeatAt = lastBeatAt,
            UserName = userName,
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public void ComputeCredit_Pure_Function_Covers_Every_Rule()
    {
        UserLearningTimesController.ComputeCredit(0, isNewRow: true)
            .Should().Be(UserLearningTimesController.FirstBeatCreditSeconds,
                "the day's first beat bootstraps the minute its heartbeat loop watched");
        UserLearningTimesController.ComputeCredit(60.4, isNewRow: false)
            .Should().Be(60, "an on-interval beat credits the elapsed time");
        UserLearningTimesController.ComputeCredit(0.2, isNewRow: false)
            .Should().Be(0, "a second in the same instant (multi-tab / replay) credits nothing");
        UserLearningTimesController.ComputeCredit(3600, isNewRow: false)
            .Should().Be(UserLearningTimesController.MaxCreditPerBeatSeconds,
                "a resumed-after-idle gap is capped; the idle minutes were never verified");
        UserLearningTimesController.ComputeCredit(-5, isNewRow: false)
            .Should().Be(0, "clock skew (beat before the stored one) cannot remove seconds");
        UserLearningTimesController.ComputeCredit(double.NaN, isNewRow: false)
            .Should().Be(0);
    }

    [Fact]
    public async Task Post_Creates_Todays_Row_With_Bootstrap_Credit()
    {
        var dto = await BeatAsync(1);

        dto.LearnDate.Should().Be(Today);
        dto.CategoryId.Should().Be(1);
        dto.LearningSeconds.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds);
        dto.LastBeatAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.CountAsync(t => t.UserId == TestUserId)).Should().Be(1);
    }

    [Fact]
    public async Task Post_Immediate_Redispatch_Is_Deduplicated()
    {
        await BeatAsync(1);
        var second = await BeatAsync(1);

        second.LearningSeconds.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds,
            "the seconds between two instant beats round to 0 - replays and a second tab add nothing");

        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.CountAsync(t => t.UserId == TestUserId)).Should().Be(1);
    }

    [Fact]
    public async Task Post_Credits_Elapsed_Time_From_Server_Clock()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(TestUserId, Today, 3, 120, now.AddSeconds(-70));

        var dto = await BeatAsync(3);

        // 120 stored + round(elapsed ≈ 70.0x s). The +2 ceiling only absorbs test
        // scheduling drift; the point is that the server clock, not the client, decides.
        dto.LearningSeconds.Should().BeInRange(190, 192);
    }

    [Fact]
    public async Task Post_Clamps_Resumed_Long_Gap()
    {
        var now = DateTime.UtcNow;
        await SeedAsync(TestUserId, Today, 2, 600, now.AddMinutes(-30));

        var dto = await BeatAsync(2);

        dto.LearningSeconds.Should().Be(
            600 + UserLearningTimesController.MaxCreditPerBeatSeconds,
            "a half-hour gap after a hidden tab credits at most the cap");
    }

    [Fact]
    public async Task Post_Rejects_Invalid_Category()
    {
        (await PostAsync(0)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await PostAsync(7)).Result.Should().BeOfType<BadRequestObjectResult>();

        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Post_Keeps_One_Row_Per_Category()
    {
        await BeatAsync(1);
        await BeatAsync(5);

        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.CountAsync(
            t => t.UserId == TestUserId && t.LearnDate == Today)).Should().Be(2);
    }

    [Fact]
    public async Task Post_Second_Category_Same_Day_Credits_From_Shared_Cursor_Not_A_Second_Bootstrap()
    {
        await BeatAsync(1);
        var second = await BeatAsync(5);

        second.CategoryId.Should().Be(5);
        second.LearningSeconds.Should().Be(0,
            "the day's cursor moved one instant ago; opening a new category row does not re-collect the bootstrap");

        _context.ChangeTracker.Clear();
        var dayTotal = await _context.UserLearningTimes
            .Where(t => t.UserId == TestUserId && t.LearnDate == Today)
            .SumAsync(t => t.LearningSeconds);
        dayTotal.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds,
            "the day's total counts ONE bootstrap, not one per category");
    }

    [Fact]
    public async Task Post_Six_Category_Beat_Loop_Cannot_Inflate_The_Day_Total()
    {
        // Review finding H1's repro: an abusive client that beats all six categories
        // in a tight loop. Rows for every category already exist from earlier today;
        // after a 5-minute idle, six instant beats follow.
        var now = DateTime.UtcNow;
        for (var category = 1; category <= 6; category++)
        {
            await SeedAsync(TestUserId, Today, category, 0, now.AddMinutes(-5));
        }

        for (var category = 1; category <= 6; category++)
        {
            await BeatAsync(category);
        }

        // Per-row crediting would have paid 6 capped gaps; the shared day cursor
        // (MAX LastBeatAt across the day's rows) makes the first beat credit the one
        // real gap and every later beat see the cursor it just moved — ~0.
        _context.ChangeTracker.Clear();
        var dayTotal = await _context.UserLearningTimes
            .Where(t => t.UserId == TestUserId && t.LearnDate == Today)
            .SumAsync(t => t.LearningSeconds);
        dayTotal.Should().Be(UserLearningTimesController.MaxCreditPerBeatSeconds,
            "six instant beats credit the single real gap (capped), never six gaps");
    }

    [Fact]
    public async Task Post_Is_Scoped_To_Caller()
    {
        await BeatAsync(1);
        SetupUserClaims("other-user");
        await BeatAsync(1);

        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.CountAsync(t => t.UserId == TestUserId)).Should().Be(1);
        (await _context.UserLearningTimes.CountAsync(t => t.UserId == "other-user")).Should().Be(1);
    }

    [Fact]
    public async Task Post_Without_UserId_Claim_Returns_Unauthorized()
    {
        SetupUserClaims(null);

        var result = await PostAsync(1);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await _context.UserLearningTimes.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Post_Falls_Back_To_Sub_Claim_When_NameIdentifier_Absent()
    {
        SetupSubClaim("sub-user-42");

        var dto = await BeatAsync(4);

        dto.LearningSeconds.Should().Be(UserLearningTimesController.FirstBeatCreditSeconds);
        _context.ChangeTracker.Clear();
        (await _context.UserLearningTimes.AnyAsync(t => t.UserId == "sub-user-42")).Should().BeTrue();
    }

    [Fact]
    public async Task Get_Returns_Only_Current_User_Day_Desc_Category_Asc()
    {
        await SeedAsync(TestUserId, Today, 5, 60, DateTime.UtcNow);
        await SeedAsync(TestUserId, Today, 1, 60, DateTime.UtcNow);
        await SeedAsync(TestUserId, Today.AddDays(-1), 1, 60, DateTime.UtcNow);
        await SeedAsync("other-user", Today, 1, 60, DateTime.UtcNow);

        var result = await _controller.GetLearningTimes(null, null, null, CancellationToken.None);

        result.Value.Should().NotBeNull();
        result.Value!.Select(d => (d.LearnDate, d.CategoryId)).Should().Equal(
            (Today, 1), (Today, 5), (Today.AddDays(-1), 1));
    }

    [Fact]
    public async Task Get_Default_Window_Excludes_Rows_Older_Than_90_Days()
    {
        await SeedAsync(TestUserId, Today.AddDays(-200), 1, 60, DateTime.UtcNow);
        await SeedAsync(TestUserId, Today, 1, 60, DateTime.UtcNow);

        var result = await _controller.GetLearningTimes(null, null, null, CancellationToken.None);

        result.Value!.Select(d => d.LearnDate).Should().Equal(Today);
    }

    [Fact]
    public async Task Get_Honors_Category_Filter()
    {
        await SeedAsync(TestUserId, Today, 1, 60, DateTime.UtcNow);
        await SeedAsync(TestUserId, Today, 3, 60, DateTime.UtcNow);

        var result = await _controller.GetLearningTimes(null, null, 3, CancellationToken.None);

        result.Value!.Should().OnlyContain(d => d.CategoryId == 3);
    }

    [Fact]
    public async Task Get_Rejects_Inverted_Range_And_Invalid_Category()
    {
        (await _controller.GetLearningTimes(Today, Today.AddDays(-1), null, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
        (await _controller.GetLearningTimes(null, null, 9, CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Get_Without_UserId_Claim_Returns_Unauthorized()
    {
        SetupUserClaims(null);

        var result = await _controller.GetLearningTimes(null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    // ── userName capture + leaderboard ───────────────────────────────────────

    private async Task BeatWithUserAsync(int categoryId, string? userName)
    {
        var result = await _controller.RecordBeat(
            new LearningTimeBeatRequest { CategoryId = categoryId, UserName = userName },
            CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Post_Stores_UserName_And_Last_Non_Empty_Wins()
    {
        await BeatWithUserAsync(1, "Alice");
        await BeatWithUserAsync(1, null);
        var row = await _context.UserLearningTimes.SingleAsync(t => t.UserId == TestUserId);
        row.UserName.Should().Be("Alice", "a beat without the field never erases the stored label");

        await BeatWithUserAsync(1, "  Bob  ");
        row = await _context.UserLearningTimes.SingleAsync(t => t.UserId == TestUserId);
        row.UserName.Should().Be("Bob", "names are trimmed");

        await BeatWithUserAsync(1, "   ");
        row = await _context.UserLearningTimes.SingleAsync(t => t.UserId == TestUserId);
        row.UserName.Should().Be("Bob", "blank trims to null and is ignored like a missing field");
    }

    [Fact]
    public async Task Post_Clamps_UserName_To_Column_Width()
    {
        await BeatWithUserAsync(1, new string('x', 250));

        var row = await _context.UserLearningTimes.SingleAsync(t => t.UserId == TestUserId);
        row.UserName!.Length.Should().Be(200);
    }

    [Fact]
    public async Task Leaderboard_Sums_Per_User_Orders_Desc_And_Tops_Out()
    {
        var now = DateTime.UtcNow;
        // alice: 100 + 50 across two days/categories; bob: 300; carol: 40; dan: 10.
        await SeedAsync("alice", Today, 1, 100, now, "Alice A");
        await SeedAsync("alice", Today.AddDays(-1), 3, 50, now);
        await SeedAsync("bob", Today, 2, 300, now, "Bob B");
        await SeedAsync("carol", Today, 5, 40, now, "Carol C");
        await SeedAsync("dan", Today, 1, 10, now, "Dan D");

        var result = await _controller.GetLeaderboard(7, 3, CancellationToken.None);

        result.Value.Should().NotBeNull();
        result.Value!.Select(e => (e.UserName, e.TotalSeconds)).Should().Equal(
            ("Bob B", 300), ("Alice A", 150), ("Carol C", 40));
    }

    [Fact]
    public async Task Leaderboard_Respects_Window_And_Defaults()
    {
        var now = DateTime.UtcNow;
        await SeedAsync("old", Today.AddDays(-40), 1, 999, now, "Old"); // outside 30
        await SeedAsync("old", Today.AddDays(-91), 1, 777, now);        // outside 92
        await SeedAsync("recent", Today.AddDays(-3), 1, 10, now, "Recent");

        var thirty = await _controller.GetLeaderboard(30, 5, CancellationToken.None);
        // Window 30: only the -3 day row; 'old' (-40/-91) is entirely outside.
        thirty.Value!.Select(e => e.UserName).Should().Equal("Recent");

        var maxWindow = await _controller.GetLeaderboard(1000, 5, CancellationToken.None);
        // 'old' reappears once -91 falls inside the 92-day clamp: 999 + 777 = 1776s.
        maxWindow.Value!.Select(e => e.UserName).Should().Equal("Old", "Recent");

        // Defaults: window 7 / top 3 need no arguments.
        await SeedAsync("recent", Today, 1, 5, now);
        var dflt = await _controller.GetLeaderboard(null, null, CancellationToken.None);
        dflt.Value!.Should().HaveCount(1);
        dflt.Value![0].TotalSeconds.Should().Be(15);
    }

    [Fact]
    public async Task Leaderboard_Falls_Back_To_Id_Prefix_And_Ties_Break_Stably()
    {
        var now = DateTime.UtcNow;
        await SeedAsync("zzz-no-name", Today, 1, 100, now); // no UserName stored
        await SeedAsync("aaa-no-name", Today, 1, 100, now); // tie -> id ascending

        var result = await _controller.GetLeaderboard(7, 3, CancellationToken.None);

        // Same seconds: ordered by user id; no stored name -> the id's first 8 chars.
        result.Value!.Select(e => (e.UserName, e.UserId)).Should().Equal(
            ("aaa-no-n", "aaa-no-name"), ("zzz-no-n", "zzz-no-name"));
    }

    [Fact]
    public async Task Leaderboard_Top_Clamps_To_Ten()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 12; i++)
        {
            await SeedAsync($"user-{i:D2}", Today, 1, 100 - i, now);
        }

        var result = await _controller.GetLeaderboard(7, 999, CancellationToken.None);

        result.Value!.Should().HaveCount(UserLearningTimesController.LeaderboardMaxTop);
        result.Value!.Select(e => e.UserId).Should().Equal("user-00", "user-01", "user-02",
            "user-03", "user-04", "user-05", "user-06", "user-07", "user-08", "user-09");
    }

    [Fact]
    public async Task Leaderboard_Empty_Table_Returns_Empty_List()
    {
        var result = await _controller.GetLeaderboard(null, null, CancellationToken.None);

        result.Value.Should().NotBeNull();
        result.Value!.Should().BeEmpty();
    }

    // ── leaderboard opt-out ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPreferences_Without_Row_Returns_Visible()
    {
        var result = await _controller.GetPreferences(CancellationToken.None);

        result.Value!.LeaderboardOptOut.Should().BeFalse("absence of a row means visible");
    }

    [Fact]
    public async Task PutPreferences_Upserts_Single_Row_And_Roundtrips()
    {
        var off = new LearningTimePreferencesDto { LeaderboardOptOut = false };
        var on = new LearningTimePreferencesDto { LeaderboardOptOut = true };

        (await _controller.UpdatePreferences(on, CancellationToken.None)).Result!
            .Should().BeOfType<OkObjectResult>();
        (await _controller.GetPreferences(CancellationToken.None)).Value!
            .LeaderboardOptOut.Should().BeTrue();

        await _controller.UpdatePreferences(off, CancellationToken.None);
        (await _controller.GetPreferences(CancellationToken.None)).Value!
            .LeaderboardOptOut.Should().BeFalse();

        _context.ChangeTracker.Clear();
        (await _context.LeaderboardPreferences.CountAsync(p => p.UserId == TestUserId))
            .Should().Be(1, "every PUT updates the one row per user");
    }

    [Fact]
    public async Task Leaderboard_Excludes_Opted_Out_Users()
    {
        var now = DateTime.UtcNow;
        await SeedAsync("alice", Today, 1, 500, now, "Alice");
        await SeedAsync("bob", Today, 1, 900, now, "Bob");
        SetupUserClaims("bob");
        await _controller.UpdatePreferences(
            new LearningTimePreferencesDto { LeaderboardOptOut = true }, CancellationToken.None);
        SetupUserClaims(TestUserId);

        var result = await _controller.GetLeaderboard(7, 3, CancellationToken.None);

        // Bob leads on seconds but opted out; the board shows only Alice.
        result.Value!.Select(e => e.UserName).Should().Equal("Alice");
    }

    [Fact]
    public async Task Leaderboard_Includes_User_After_Opt_Back_In()
    {
        var now = DateTime.UtcNow;
        await SeedAsync("alice", Today, 1, 500, now, "Alice");
        SetupUserClaims("alice");
        await _controller.UpdatePreferences(
            new LearningTimePreferencesDto { LeaderboardOptOut = true }, CancellationToken.None);
        await _controller.UpdatePreferences(
            new LearningTimePreferencesDto { LeaderboardOptOut = false }, CancellationToken.None);
        SetupUserClaims(TestUserId);

        var result = await _controller.GetLeaderboard(7, 3, CancellationToken.None);

        result.Value!.Select(e => e.UserName).Should().Equal("Alice");
    }

    [Fact]
    public async Task PutPreferences_Without_UserId_Claim_Returns_Unauthorized()
    {
        SetupUserClaims(null);

        var result = await _controller.UpdatePreferences(
            new LearningTimePreferencesDto { LeaderboardOptOut = true }, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await _context.LeaderboardPreferences.CountAsync()).Should().Be(0);
    }

    public void Dispose() => _context.Dispose();
}
