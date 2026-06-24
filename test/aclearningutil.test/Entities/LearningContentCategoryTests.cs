using FluentAssertions;
using aclearningutil.Data.Entities;

namespace aclearningutil.test.Entities;

public class LearningContentCategoryTests
{
    [Fact]
    public void Default_Constructor_Initializes_Empty_Strings()
    {
        // Act
        var category = new LearningContentCategory();

        // Assert
        category.NameChinese.Should().BeEmpty();
        category.NameEnglish.Should().BeEmpty();
    }

    [Fact]
    public void Can_Set_Properties()
    {
        // Arrange & Act
        var category = new LearningContentCategory
        {
            Id = 1,
            NameChinese = "词汇",
            NameEnglish = "Vocabulary"
        };

        // Assert
        category.Id.Should().Be(1);
        category.NameChinese.Should().Be("词汇");
        category.NameEnglish.Should().Be("Vocabulary");
    }
}
