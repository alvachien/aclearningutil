using FluentAssertions;
using aclearningutil.Data.Entities;

namespace aclearningutil.test.Entities;

public class LearningContentTests
{
    [Fact]
    public void Default_Constructor_Initializes_Empty_Strings()
    {
        // Act
        var content = new LearningContent();

        // Assert
        content.NameChinese.Should().BeEmpty();
        content.NameEnglish.Should().BeEmpty();
        content.FileUrl.Should().BeEmpty();
        content.Version.Should().BeNull();
    }

    [Fact]
    public void Can_Set_Properties()
    {
        // Arrange & Act
        var content = new LearningContent
        {
            Id = 1,
            CategoryId = 1,
            NameChinese = "测试内容",
            NameEnglish = "Test Content",
            FileUrl = "https://example.com/file.mp3",
            Version = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Assert
        content.Id.Should().Be(1);
        content.CategoryId.Should().Be(1);
        content.NameChinese.Should().Be("测试内容");
        content.NameEnglish.Should().Be("Test Content");
        content.FileUrl.Should().Be("https://example.com/file.mp3");
        content.Version.Should().Be((byte)2);
    }

    [Fact]
    public void Category_Navigation_Property_Defaults_To_Null()
    {
        // Act
        var content = new LearningContent();

        // Assert
        content.Category.Should().BeNull();
    }

    [Fact]
    public void Can_Set_Category_Navigation_Property()
    {
        // Arrange
        var category = new LearningContentCategory
        {
            Id = 1,
            NameChinese = "词汇",
            NameEnglish = "Vocabulary"
        };

        // Act
        var content = new LearningContent
        {
            Category = category
        };

        // Assert
        content.Category.Should().NotBeNull();
        content.Category!.NameEnglish.Should().Be("Vocabulary");
    }
}
