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
/// Per-day login history: POST upserts the caller's (user, today) row, GET serves the
/// caller's own days inside a bounded window. Scaffolding mirrors
/// <see cref="UserLearningRatingsControllerTests"/> (user-scoping scaffolding).
/// </summary>
public class UserLoginHistoriesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly UserLoginHistoriesController _controller;
    private const string TestUserId = "test-user-123";

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public UserLoginHistoriesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _controller = new UserLoginHistoriesController(
            _context, new Mock<ILogger<UserLoginHistoriesController>>().Object);
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

    private async Task<UserLoginHistoryDto> PostAsync()
    {
        var result = await _controller.RecordLogin(CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
        return ((OkObjectResult)result.Result!).Value.As<UserLoginHistoryDto>();
    }

    private async Task SeedAsync(string userId, DateOnly day, int count = 1, DateTime? lastLoginAt = null)
    {
        _context.UserLoginHistories.Add(new UserLoginHistory
        {
            UserId = userId,
            LoginDate = day,
            FirstLoginAt = day.ToDateTime(new TimeOnly(8, 0)),
            LastLoginAt = lastLoginAt ?? day.ToDateTime(new TimeOnly(20, 0)),
            LoginCount = count,
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task Post_Creates_Todays_Row()
    {
        var dto = await PostAsync();

        dto.LoginDate.Should().Be(Today);
        dto.LoginCount.Should().Be(1);
        dto.FirstLoginAt.Should().Be(dto.LastLoginAt);

        _context.ChangeTracker.Clear();
        (await _context.UserLoginHistories.CountAsync(h => h.UserId == TestUserId)).Should().Be(1);
    }

    [Fact]
    public void IsDistinctLogin_Implements_The_Dedup_Window()
    {
        var now = DateTime.UtcNow;
        UserLoginHistoriesController.IsDistinctLogin(now, now.AddSeconds(-301))
            .Should().BeTrue("a report past the 5-minute window is a distinct sign-in");
        UserLoginHistoriesController.IsDistinctLogin(now, now.AddSeconds(-299))
            .Should().BeFalse("a report inside the window is a duplicate");
        UserLoginHistoriesController.IsDistinctLogin(now, now.AddMinutes(-1))
            .Should().BeFalse("even a 1-per-minute scripted loop credits at most one login per window");
        UserLoginHistoriesController.IsDistinctLogin(now, now.AddSeconds(30))
            .Should().BeFalse("a stored instant ahead of the clock (skew) is indistinguishable from a replay");
    }

    [Fact]
    public async Task Post_Within_Dedup_Window_Is_A_NoOp_Duplicate_Report()
    {
        var first = await PostAsync();
        var second = await PostAsync();

        second.LoginCount.Should().Be(1,
            "a same-day re-POST moments later is a duplicate report (review finding H3), not a new sign-in");
        second.LastLoginAt.Should().Be(first.LastLoginAt,
            "a duplicate does not even walk LastLoginAt forward");
        second.FirstLoginAt.Should().Be(first.FirstLoginAt);

        _context.ChangeTracker.Clear();
        (await _context.UserLoginHistories.CountAsync(h => h.UserId == TestUserId)).Should().Be(1);
    }

    [Fact]
    public async Task Post_After_Dedup_Window_Counts_As_A_New_Sign_In_And_Keeps_First()
    {
        await SeedAsync(TestUserId, Today, count: 2, lastLoginAt: DateTime.UtcNow.AddMinutes(-6));

        _context.ChangeTracker.Clear();
        var firstBefore = (await _context.UserLoginHistories.SingleAsync(h => h.UserId == TestUserId)).FirstLoginAt;

        var dto = await PostAsync();

        dto.LoginCount.Should().Be(3, "six minutes after the last accepted sign-in, the report is a distinct login");
        dto.LastLoginAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        _context.ChangeTracker.Clear();
        var row = await _context.UserLoginHistories.SingleAsync(h => h.UserId == TestUserId);
        row.FirstLoginAt.Should().Be(firstBefore,
            "FirstLoginAt stays immutable however many sign-ins follow");
    }

    [Fact]
    public async Task Post_Is_Scoped_To_Caller()
    {
        await PostAsync();
        SetupUserClaims("other-user");
        await PostAsync();

        _context.ChangeTracker.Clear();
        (await _context.UserLoginHistories.CountAsync(h => h.UserId == TestUserId)).Should().Be(1);
        (await _context.UserLoginHistories.CountAsync(h => h.UserId == "other-user")).Should().Be(1);
    }

    [Fact]
    public async Task Post_Without_UserId_Claim_Returns_Unauthorized()
    {
        SetupUserClaims(null);

        var result = await _controller.RecordLogin(CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
        (await _context.UserLoginHistories.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Post_Falls_Back_To_Sub_Claim_When_NameIdentifier_Absent()
    {
        SetupSubClaim("sub-user-42");

        var dto = await PostAsync();

        dto.LoginCount.Should().Be(1);
        _context.ChangeTracker.Clear();
        (await _context.UserLoginHistories.AnyAsync(h => h.UserId == "sub-user-42")).Should().BeTrue();
    }

    [Fact]
    public async Task Get_Without_UserId_Claim_Returns_Unauthorized()
    {
        SetupUserClaims(null);

        var result = await _controller.GetHistory(null, null, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Get_Returns_Only_Current_User_Ordered_Newest_First()
    {
        await SeedAsync(TestUserId, Today);
        await SeedAsync(TestUserId, Today.AddDays(-1));
        await SeedAsync("other-user", Today);

        var result = await _controller.GetHistory(null, null, CancellationToken.None);

        result.Value.Should().NotBeNull();
        result.Value!.Select(d => d.LoginDate).Should().Equal(Today, Today.AddDays(-1));
    }

    [Fact]
    public async Task Get_Default_Window_Excludes_Rows_Older_Than_90_Days()
    {
        await SeedAsync(TestUserId, Today.AddDays(-200));
        await SeedAsync(TestUserId, Today);

        var result = await _controller.GetHistory(null, null, CancellationToken.None);

        result.Value!.Select(d => d.LoginDate).Should().Equal(Today);
    }

    [Fact]
    public async Task Get_Honors_Explicit_From_To()
    {
        var from = Today.AddDays(-10);
        var to = Today.AddDays(-1);
        await SeedAsync(TestUserId, from.AddDays(-1));
        await SeedAsync(TestUserId, from);
        await SeedAsync(TestUserId, Today.AddDays(-5));
        await SeedAsync(TestUserId, to);
        await SeedAsync(TestUserId, to.AddDays(1));

        var result = await _controller.GetHistory(from, to, CancellationToken.None);

        result.Value!.Select(d => d.LoginDate).Should().BeInDescendingOrder();
        result.Value!.Select(d => d.LoginDate).Should().OnlyContain(d => d >= from && d <= to);
        result.Value!.Should().HaveCount(3);
    }

    [Fact]
    public async Task Get_Rejects_Inverted_Range()
    {
        var result = await _controller.GetHistory(Today, Today.AddDays(-1), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    public void Dispose() => _context.Dispose();
}
