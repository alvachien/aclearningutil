using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Data;

public class AppDbContextNewEntityTests : IDisposable
{
    private readonly AppDbContext _context;

    public AppDbContextNewEntityTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
    }

    [Fact]
    public async Task Can_Add_LearningContentCategory()
    {
        // Arrange
        var category = new LearningContentCategory
        {
            Id = 100,
            NameChinese = "测试",
            NameEnglish = "Test"
        };

        // Act
        _context.LearningContentCategories.Add(category);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.LearningContentCategories.FindAsync(100);
        result.Should().NotBeNull();
        result!.NameChinese.Should().Be("测试");
        result.NameEnglish.Should().Be("Test");
    }

    [Fact]
    public async Task Can_Add_LearningContent_With_Category()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        _context.LearningContentCategories.Add(category);
        await _context.SaveChangesAsync();

        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "测试内容",
            NameEnglish = "Test Content",
            FileUrl = "https://example.com/file.mp3",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.LearningContents
            .Include(c => c.Category)
            .FirstOrDefaultAsync(c => c.NameEnglish == "Test Content");
        result.Should().NotBeNull();
        result!.Category.Should().NotBeNull();
        result.Category!.NameEnglish.Should().Be("Vocabulary");
    }

    [Fact]
    public async Task Can_Add_UserLearningHistory()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Category = category,
            NameChinese = "内容",
            NameEnglish = "Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        var history = new UserLearningHistory
        {
            UserId = "user1",
            ContentId = content.Id,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };

        // Act
        _context.UserLearningHistories.Add(history);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.UserLearningHistories
            .Include(h => h.Content)
            .FirstOrDefaultAsync(h => h.UserId == "user1");
        result.Should().NotBeNull();
        result!.Content.Should().NotBeNull();
        result.Content!.NameEnglish.Should().Be("Content");
        result.SuccessIndicator.Should().BeTrue();
    }

    [Fact]
    public async Task Can_Add_UserLearningRating()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Category = category,
            NameChinese = "内容",
            NameEnglish = "Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        var rating = new UserLearningRating
        {
            UserId = "user1",
            ContentId = content.Id,
            ScoreDate = DateTime.Today,
            Rating = 5
        };

        // Act
        _context.UserLearningRatings.Add(rating);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.UserLearningRatings
            .Include(r => r.Content)
            .FirstOrDefaultAsync(r => r.UserId == "user1");
        result.Should().NotBeNull();
        result!.Content.Should().NotBeNull();
        result.Content!.NameEnglish.Should().Be("Content");
        result.Rating.Should().Be(5);
    }

    [Fact]
    public async Task Can_Query_UserLearningHistories_By_UserId()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Category = category,
            NameChinese = "内容",
            NameEnglish = "Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        _context.UserLearningHistories.AddRange(
            new UserLearningHistory { UserId = "user1", ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = true },
            new UserLearningHistory { UserId = "user2", ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = false },
            new UserLearningHistory { UserId = "user1", ContentId = content.Id, LearnDate = DateTime.Today, SuccessIndicator = true }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _context.UserLearningHistories
            .Where(h => h.UserId == "user1")
            .ToListAsync();

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Can_Query_UserLearningRatings_By_UserId()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Category = category,
            NameChinese = "内容",
            NameEnglish = "Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        _context.UserLearningRatings.AddRange(
            new UserLearningRating { UserId = "user1", ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 5 },
            new UserLearningRating { UserId = "user2", ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 3 },
            new UserLearningRating { UserId = "user1", ContentId = content.Id, ScoreDate = DateTime.Today, Rating = 4 }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _context.UserLearningRatings
            .Where(r => r.UserId == "user1")
            .ToListAsync();

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task Can_Delete_LearningContentCategory()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "删除", NameEnglish = "Delete" };
        _context.LearningContentCategories.Add(category);
        await _context.SaveChangesAsync();

        // Act
        _context.LearningContentCategories.Remove(category);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.LearningContentCategories.FindAsync(100);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Can_Update_LearningContent()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        var content = new LearningContent
        {
            Category = category,
            NameChinese = "旧内容",
            NameEnglish = "Old Content",
            FileUrl = "old_url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        // Act
        content.NameChinese = "新内容";
        content.NameEnglish = "New Content";
        content.FileUrl = "new_url";
        content.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.LearningContents.FindAsync(content.Id);
        result.Should().NotBeNull();
        result!.NameChinese.Should().Be("新内容");
        result.NameEnglish.Should().Be("New Content");
        result.FileUrl.Should().Be("new_url");
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
