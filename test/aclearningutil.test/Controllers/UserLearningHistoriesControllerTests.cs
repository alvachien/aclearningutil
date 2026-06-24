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
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Controllers;

public class UserLearningHistoriesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<UserLearningHistoriesController>> _mockLogger;
    private readonly UserLearningHistoriesController _controller;
    private const string TestUserId = "test-user-123";

    public UserLearningHistoriesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _mockLogger = new Mock<ILogger<UserLearningHistoriesController>>();
        _controller = new UserLearningHistoriesController(_context, _mockLogger.Object);
        SetupUserClaims(TestUserId);
    }

    private void SetupUserClaims(string userId)
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, userId)
        };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // Use IDs >= 100 to avoid conflicts with seed data (category IDs 1-6)
    private async Task<LearningContent> SeedContentAsync()
    {
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Id = 100,
            CategoryId = 100,
            NameChinese = "测试内容",
            NameEnglish = "Test Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContentCategories.Add(category);
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();
        return content;
    }

    [Fact]
    public async Task GetAll_Returns_Only_Current_User_Histories()
    {
        // Arrange
        var content = await SeedContentAsync();
        _context.UserLearningHistories.AddRange(
            new UserLearningHistory { UserId = TestUserId, ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = "other-user", ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(null, null);

        // Assert
        var histories = result.Value;
        histories.Should().NotBeNull();
        histories.Should().HaveCount(2);
        histories!.All(h => h.UserId == TestUserId).Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_With_ContentId_Filter_Returns_Filtered_Histories()
    {
        // Arrange
        var content1 = await SeedContentAsync();
        var content2 = new LearningContent
        {
            Id = 101,
            CategoryId = 100,
            NameChinese = "内容2",
            NameEnglish = "Content2",
            FileUrl = "url2",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content2);
        await _context.SaveChangesAsync();

        _context.UserLearningHistories.AddRange(
            new UserLearningHistory { UserId = TestUserId, ContentId = content1.Id, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content2.Id, LearnDate = DateTime.Today, SuccessIndicator = true }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(content1.Id, null);

        // Assert
        var histories = result.Value;
        histories.Should().NotBeNull();
        histories.Should().HaveCount(1);
        histories![0].ContentId.Should().Be(content1.Id);
    }

    [Fact]
    public async Task GetAll_With_ItemId_Filter_Returns_Filtered_Histories()
    {
        // Arrange
        var content = await SeedContentAsync();
        _context.UserLearningHistories.AddRange(
            new UserLearningHistory { UserId = TestUserId, ContentId = content.Id, ItemId = 1, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content.Id, ItemId = 2, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content.Id, ItemId = 1, LearnDate = DateTime.Today, SuccessIndicator = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(null, 1);

        // Assert
        var histories = result.Value;
        histories.Should().NotBeNull();
        histories.Should().HaveCount(2);
        histories!.All(h => h.ItemId == 1).Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_With_ContentId_And_ItemId_Filters_Returns_Filtered_Histories()
    {
        // Arrange
        var content1 = await SeedContentAsync();
        var content2 = new LearningContent
        {
            Id = 101,
            CategoryId = 100,
            NameChinese = "内容2",
            NameEnglish = "Content2",
            FileUrl = "url2",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content2);
        await _context.SaveChangesAsync();

        _context.UserLearningHistories.AddRange(
            new UserLearningHistory { UserId = TestUserId, ContentId = content1.Id, ItemId = 1, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content1.Id, ItemId = 2, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = TestUserId, ContentId = content2.Id, ItemId = 1, LearnDate = DateTime.Today, SuccessIndicator = true }
        );
        await _context.SaveChangesAsync();

        // Act - search for content1 + item 1
        var result = await _controller.GetAll(content1.Id, 1);

        // Assert
        var histories = result.Value;
        histories.Should().NotBeNull();
        histories.Should().HaveCount(1);
        histories![0].ContentId.Should().Be(content1.Id);
        histories[0].ItemId.Should().Be(1);
    }

    [Fact]
    public async Task GetById_Existing_User_History_Returns_History()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = TestUserId,
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(history.Id);

        // Assert
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(TestUserId);
        result.Value.SuccessIndicator.Should().BeTrue();
    }

    [Fact]
    public async Task GetById_Other_User_History_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = "other-user",
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(history.Id);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Valid_History_Returns_CreatedAtAction()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            ContentId = content.Id,
            SuccessIndicator = true
        };

        // Act
        var result = await _controller.Create(history);

        // Assert
        var createdAtActionResult = result.Result as CreatedAtActionResult;
        createdAtActionResult.Should().NotBeNull();
        var created = createdAtActionResult!.Value as UserLearningHistory;
        created.Should().NotBeNull();
        created!.UserId.Should().Be(TestUserId);
        created.LearnDate.Should().Be(DateTime.Today);
    }

    [Fact]
    public async Task Create_Invalid_ContentId_Returns_BadRequest()
    {
        // Arrange
        var history = new UserLearningHistory
        {
            ContentId = 999,
            SuccessIndicator = true
        };

        // Act
        var result = await _controller.Create(history);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Existing_User_History_Returns_NoContent()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = TestUserId,
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = false
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        var updatedHistory = new UserLearningHistory
        {
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };

        // Act
        var result = await _controller.Update(history.Id, updatedHistory);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbHistory = await _context.UserLearningHistories.FindAsync(history.Id);
        dbHistory!.SuccessIndicator.Should().BeTrue();
    }

    [Fact]
    public async Task Update_Other_User_History_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = "other-user",
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        var updatedHistory = new UserLearningHistory
        {
            ContentId = content.Id,
            SuccessIndicator = false
        };

        // Act
        var result = await _controller.Update(history.Id, updatedHistory);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Existing_User_History_Returns_NoContent()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = TestUserId,
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.Delete(history.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbHistory = await _context.UserLearningHistories.FindAsync(history.Id);
        dbHistory.Should().BeNull();
    }

    [Fact]
    public async Task Delete_Other_User_History_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var history = new UserLearningHistory
        {
            UserId = "other-user",
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.Delete(history.Id);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetAll_With_No_UserId_Returns_Unauthorized()
    {
        // Arrange - set up a controller with no user claims
        var controller = new UserLearningHistoriesController(_context, _mockLogger.Object);
        var identity = new ClaimsIdentity(new List<Claim>(), "Test");
        var principal = new ClaimsPrincipal(identity);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Act
        var result = await controller.GetAll(null, null);

        // Assert
        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
