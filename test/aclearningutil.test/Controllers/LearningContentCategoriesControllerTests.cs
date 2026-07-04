using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Controllers;

public class LearningContentCategoriesControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<LearningContentCategoriesController>> _mockLogger;
    private readonly LearningContentCategoriesController _controller;

    public LearningContentCategoriesControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _mockLogger = new Mock<ILogger<LearningContentCategoriesController>>();
        _controller = new LearningContentCategoriesController(_context, _mockLogger.Object);
    }

    // Note: In-memory DB is seeded with 6 categories (IDs 1-6) via HasData.
    // The controller is read-only (GET only, no create/update/delete).

    [Fact]
    public async Task GetAll_Returns_All_Categories_Including_Seed_Data()
    {
        // Arrange - add 2 extra categories beyond the 6 seeded ones
        _context.LearningContentCategories.AddRange(
            new LearningContentCategory { Id = 100, NameChinese = "额外分类", NameEnglish = "Extra Category" },
            new LearningContentCategory { Id = 101, NameChinese = "另一个分类", NameEnglish = "Another Category" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(CancellationToken.None);

        // Assert - 6 seed + 2 test = 8
        var categories = result.Value;
        categories.Should().NotBeNull();
        categories.Should().HaveCount(8);
    }

    [Fact]
    public async Task GetAll_Returns_Categories_Ordered_By_Id()
    {
        // Arrange - add extra categories
        _context.LearningContentCategories.Add(
            new LearningContentCategory { Id = 100, NameChinese = "额外", NameEnglish = "Extra" }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(CancellationToken.None);

        // Assert - verify ordering
        var categories = result.Value;
        categories.Should().NotBeNull();
        for (int i = 1; i < categories!.Count - 1; i++)
        {
            categories[i].Id.Should().BeGreaterThan(categories[i - 1].Id);
        }
    }

    [Fact]
    public async Task GetById_Existing_Seed_Category_Returns_Category()
    {
        // Act - use seed data category (ID 1 = 词汇/Vocabulary)
        var result = await _controller.GetById(1, CancellationToken.None);

        // Assert
        result.Value.Should().NotBeNull();
        result.Value!.NameChinese.Should().Be("词汇");
        result.Value.NameEnglish.Should().Be("Vocabulary");
    }

    [Fact]
    public async Task GetById_Test_Category_Returns_Category()
    {
        // Arrange
        var category = new LearningContentCategory { Id = 100, NameChinese = "测试", NameEnglish = "Test" };
        _context.LearningContentCategories.Add(category);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(100, CancellationToken.None);

        // Assert
        result.Value.Should().NotBeNull();
        result.Value!.NameChinese.Should().Be("测试");
        result.Value.NameEnglish.Should().Be("Test");
    }

    [Fact]
    public async Task GetById_NonExisting_Category_Returns_NotFound()
    {
        // Act
        var result = await _controller.GetById(999, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
