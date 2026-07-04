using FluentAssertions;
using aclearningutil.Data.Entities;

namespace aclearningutil.test.Entities;

public class UserLearningHistoryTests
{
    [Fact]
    public void Default_Constructor_Initializes_Empty_UserId()
    {
        // Act
        var history = new UserLearningHistory();

        // Assert
        history.UserId.Should().BeEmpty();
    }

    [Fact]
    public void Can_Set_Properties()
    {
        // Arrange & Act
        var history = new UserLearningHistory
        {
            Id = 1,
            UserId = "user123",
            ContentId = 5,
            ItemId = 10,
            LearnDate = DateTime.Today,
            SuccessIndicator = true
        };

        // Assert
        history.Id.Should().Be(1);
        history.UserId.Should().Be("user123");
        history.ContentId.Should().Be(5);
        history.ItemId.Should().Be(10);
        history.LearnDate.Should().Be(DateTime.Today);
        history.SuccessIndicator.Should().BeTrue();
    }

    [Fact]
    public void ItemId_Is_Nullable()
    {
        // Arrange & Act
        var history = new UserLearningHistory
        {
            UserId = "user123",
            ContentId = 5,
            ItemId = null
        };

        // Assert
        history.ItemId.Should().BeNull();
    }

    [Fact]
    public void Content_Navigation_Property_Defaults_To_Null()
    {
        // Act
        var history = new UserLearningHistory();

        // Assert
        history.Content.Should().BeNull();
    }

    [Fact]
    public void Can_Set_Content_Navigation_Property()
    {
        // Arrange
        var content = new LearningContent
        {
            Id = 1,
            NameChinese = "测试",
            NameEnglish = "Test"
        };

        // Act
        var history = new UserLearningHistory
        {
            Content = content
        };

        // Assert
        history.Content.Should().NotBeNull();
        history.Content!.NameEnglish.Should().Be("Test");
    }
}
