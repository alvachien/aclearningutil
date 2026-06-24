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

public class LearningContentsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<LearningContentsController>> _mockLogger;
    private readonly LearningContentsController _controller;

    public LearningContentsControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _mockLogger = new Mock<ILogger<LearningContentsController>>();
        _controller = new LearningContentsController(_context, _mockLogger.Object);
    }

    // Use IDs >= 100 to avoid conflicts with seed data (IDs 1-6)
    private async Task<LearningContentCategory> SeedCategoryAsync(int id = 100)
    {
        var category = new LearningContentCategory { Id = id, NameChinese = "词汇", NameEnglish = "Vocabulary" };
        _context.LearningContentCategories.Add(category);
        await _context.SaveChangesAsync();
        return category;
    }

    [Fact]
    public async Task GetAll_Returns_All_Contents()
    {
        // Arrange
        await SeedCategoryAsync();
        _context.LearningContents.AddRange(
            new LearningContent { CategoryId = 100, NameChinese = "内容1", NameEnglish = "Content1", FileUrl = "url1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new LearningContent { CategoryId = 100, NameChinese = "内容2", NameEnglish = "Content2", FileUrl = "url2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(null);

        // Assert
        var contents = result.Value;
        contents.Should().NotBeNull();
        contents.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAll_With_CategoryId_Filter_Returns_Filtered_Contents()
    {
        // Arrange
        await SeedCategoryAsync(100);
        var cat2 = new LearningContentCategory { Id = 101, NameChinese = "句子", NameEnglish = "Sentences" };
        _context.LearningContentCategories.Add(cat2);
        await _context.SaveChangesAsync();

        _context.LearningContents.AddRange(
            new LearningContent { CategoryId = 100, NameChinese = "内容1", NameEnglish = "Content1", FileUrl = "url1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new LearningContent { CategoryId = 101, NameChinese = "内容2", NameEnglish = "Content2", FileUrl = "url2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetAll(100);

        // Assert
        var contents = result.Value;
        contents.Should().NotBeNull();
        contents.Should().HaveCount(1);
        contents![0].CategoryId.Should().Be(100);
    }

    [Fact]
    public async Task GetById_Existing_Content_Returns_Content()
    {
        // Arrange
        await SeedCategoryAsync();
        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "测试内容",
            NameEnglish = "Test Content",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetById(content.Id);

        // Assert
        result.Value.Should().NotBeNull();
        result.Value!.NameChinese.Should().Be("测试内容");
        result.Value.Category.Should().NotBeNull();
    }

    [Fact]
    public async Task GetById_NonExisting_Content_Returns_NotFound()
    {
        // Act
        var result = await _controller.GetById(999);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_Valid_Content_Returns_CreatedAtAction()
    {
        // Arrange
        await SeedCategoryAsync();
        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "新内容",
            NameEnglish = "New Content",
            FileUrl = "url"
        };

        // Act
        var result = await _controller.Create(content);

        // Assert
        var createdAtActionResult = result.Result as CreatedAtActionResult;
        createdAtActionResult.Should().NotBeNull();
        var created = createdAtActionResult!.Value as LearningContent;
        created.Should().NotBeNull();
        created!.NameChinese.Should().Be("新内容");
        created.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        created.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Create_Empty_NameChinese_Returns_BadRequest()
    {
        // Arrange
        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "",
            NameEnglish = "Test",
            FileUrl = "url"
        };

        // Act
        var result = await _controller.Create(content);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Create_Invalid_CategoryId_Returns_BadRequest()
    {
        // Arrange
        var content = new LearningContent
        {
            CategoryId = 999,
            NameChinese = "测试",
            NameEnglish = "Test",
            FileUrl = "url"
        };

        // Act
        var result = await _controller.Create(content);

        // Assert
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Update_Existing_Content_Returns_NoContent()
    {
        // Arrange
        await SeedCategoryAsync();
        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "旧内容",
            NameEnglish = "Old Content",
            FileUrl = "old_url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        var updatedContent = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "新内容",
            NameEnglish = "New Content",
            FileUrl = "new_url"
        };

        // Act
        var result = await _controller.Update(content.Id, updatedContent);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbContent = await _context.LearningContents.FindAsync(content.Id);
        dbContent!.NameChinese.Should().Be("新内容");
        dbContent.NameEnglish.Should().Be("New Content");
        dbContent.FileUrl.Should().Be("new_url");
    }

    [Fact]
    public async Task Update_NonExisting_Content_Returns_NotFound()
    {
        // Arrange
        var updatedContent = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "新内容",
            NameEnglish = "New Content",
            FileUrl = "url"
        };

        // Act
        var result = await _controller.Update(999, updatedContent);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Existing_Content_Returns_NoContent()
    {
        // Arrange
        await SeedCategoryAsync();
        var content = new LearningContent
        {
            CategoryId = 100,
            NameChinese = "删除",
            NameEnglish = "Delete",
            FileUrl = "url",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.LearningContents.Add(content);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.Delete(content.Id);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var dbContent = await _context.LearningContents.FindAsync(content.Id);
        dbContent.Should().BeNull();
    }

    [Fact]
    public async Task Delete_NonExisting_Content_Returns_NotFound()
    {
        // Act
        var result = await _controller.Delete(999);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
