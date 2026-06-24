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

public class UserLearningRatingsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<UserLearningRatingsController>> _mockLogger;
    private readonly UserLearningRatingsController _controller;
    private const string TestUserId = "test-user-123";

    public UserLearningRatingsControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _mockLogger = new Mock<ILogger<UserLearningRatingsController>>();
        _controller = new UserLearningRatingsController(_context, _mockLogger.Object);
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
    public async Task GetAll_Returns_Only_Current_User_Ratings()
    {
        // Arrange
        var content = await SeedContentAsync();
        _context.UserLearningRatings.AddRange(
            new UserLearningRating { UserId = TestUserId, ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 5 },
            new UserLearningRating { UserId = "other-user", ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 3 },
            new UserLearningRating { UserId = TestUserId, ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 4 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(null, null);

        // Assert
        var ratings = result.Value;
        ratings.Should().NotBeNull();
        ratings.Should().HaveCount(2);
        ratings!.All(r => r.UserId == TestUserId).Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_With_ContentId_Filter_Returns_Filtered_Ratings()
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

        _context.UserLearningRatings.AddRange(
            new UserLearningRating { UserId = TestUserId, ContentId = content1.Id, ScoreDate = DateTime.Today, Rating = 5 },
            new UserLearningRating { UserId = TestUserId, ContentId = content2.Id, ScoreDate = DateTime.Today, Rating = 3 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(content1.Id, null);

        // Assert
        var ratings = result.Value;
        ratings.Should().NotBeNull();
        ratings.Should().HaveCount(1);
        ratings![0].ContentId.Should().Be(content1.Id);
    }

    [Fact]
    public async Task GetAll_With_ItemId_Filter_Returns_Filtered_Ratings()
    {
        // Arrange
        var content = await SeedContentAsync();
        _context.UserLearningRatings.AddRange(
            new UserLearningRating { UserId = TestUserId, ContentId = content.Id, ItemId = 1, ScoreDate = DateTime.Today, Rating = 5 },
            new UserLearningRating { UserId = TestUserId, ContentId = content.Id, ItemId = 2, ScoreDate = DateTime.Today, Rating = 3 },
            new UserLearningRating { UserId = TestUserId, ContentId = content.Id, ItemId = 1, ScoreDate = DateTime.Today, Rating = 4 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(null, 1);

        // Assert
        var ratings = result.Value;
        ratings.Should().NotBeNull();
        ratings.Should().HaveCount(2);
        ratings!.All(r => r.ItemId == 1).Should().BeTrue();
    }

    [Fact]
    public async Task GetAll_With_ContentId_And_ItemId_Filters_Returns_Filtered_Ratings()
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

        _context.UserLearningRatings.AddRange(
            new UserLearningRating { UserId = TestUserId, ContentId = content1.Id, ItemId = 1, ScoreDate = DateTime.Today, Rating = 5 },
            new UserLearningRating { UserId = TestUserId, ContentId = content1.Id, ItemId = 2, ScoreDate = DateTime.Today, Rating = 3 },
            new UserLearningRating { UserId = TestUserId, ContentId = content2.Id, ItemId = 1, ScoreDate = DateTime.Today, Rating = 4 }
        );
        await _context.SaveChangesAsync();

        // Act - search for content1 + item 1
        var result = await _controller.GetAll(content1.Id, 1);

        // Assert
        var ratings = result.Value;
        ratings.Should().NotBeNull();
        ratings.Should().HaveCount(1);
        ratings![0].ContentId.Should().Be(content1.Id);
        ratings[0].ItemId.Should().Be(1);
    }

    [Fact]
    public async Task GetById_Existing_User_Rating_Returns_Rating()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = TestUserId,
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 5
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(rating.Id);

        // Assert
        result.Value.Should().NotBeNull();
        result.Value!.UserId.Should().Be(TestUserId);
        result.Value.Rating.Should().Be(5);
    }

    [Fact]
    public async Task GetById_Other_User_Rating_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = "other-user",
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 3
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(rating.Id);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Valid_Rating_Returns_CreatedAtAction()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = 4
        };

        // Act
        var result = await _controller.Create(rating);

        // Assert
        var createdAtActionResult = result.Result as CreatedAtActionResult;
        createdAtActionResult.Should().NotBeNull();
        var created = createdAtActionResult!.Value as UserLearningRating;
        created.Should().NotBeNull();
        created!.UserId.Should().Be(TestUserId);
        created.Rating.Should().Be(4);
        created.ScoreDate.Should().Be(DateTime.Today);
    }

    [Fact]
    public async Task Create_Rating_Below_1_Returns_BadRequest()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = 0
        };

        // Act
        var result = await _controller.Create(rating);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Rating_Above_5_Returns_BadRequest()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = 6
        };

        // Act
        var result = await _controller.Create(rating);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Create_Valid_Rating_Values_Are_Accepted(byte ratingValue)
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = ratingValue
        };

        // Act
        var result = await _controller.Create(rating);

        // Assert
        result.Result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task Create_Invalid_ContentId_Returns_BadRequest()
    {
        // Arrange
        var rating = new UserLearningRating
        {
            ContentId = 999,
            Rating = 5
        };

        // Act
        var result = await _controller.Create(rating);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Existing_User_Rating_Returns_NoContent()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = TestUserId,
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 2
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        var updatedRating = new UserLearningRating
        {
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 5
        };

        // Act
        var result = await _controller.Update(rating.Id, updatedRating);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbRating = await _context.UserLearningRatings.FindAsync(rating.Id);
        dbRating!.Rating.Should().Be(5);
    }

    [Fact]
    public async Task Update_Other_User_Rating_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = "other-user",
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 3
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        var updatedRating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = 5
        };

        // Act
        var result = await _controller.Update(rating.Id, updatedRating);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Update_Rating_Below_1_Returns_BadRequest()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = TestUserId,
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 3
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        var updatedRating = new UserLearningRating
        {
            ContentId = content.Id,
            Rating = 0
        };

        // Act
        var result = await _controller.Update(rating.Id, updatedRating);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Delete_Existing_User_Rating_Returns_NoContent()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = TestUserId,
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 5
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.Delete(rating.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbRating = await _context.UserLearningRatings.FindAsync(rating.Id);
        dbRating.Should().BeNull();
    }

    [Fact]
    public async Task Delete_Other_User_Rating_Returns_NotFound()
    {
        // Arrange
        var content = await SeedContentAsync();
        var rating = new UserLearningRating
        {
            UserId = "other-user",
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 3
        };
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.Delete(rating.Id);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetAll_With_No_UserId_Returns_Unauthorized()
    {
        // Arrange - set up a controller with no user claims
        var controller = new UserLearningRatingsController(_context, _mockLogger.Object);
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
