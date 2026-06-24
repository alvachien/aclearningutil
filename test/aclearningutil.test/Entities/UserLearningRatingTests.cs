using FluentAssertions;
using aclearningutil.Data.Entities;

namespace aclearningutil.test.Entities;

public class UserLearningRatingTests
{
    [Fact]
    public void Default_Constructor_Initializes_Empty_UserId()
    {
        // Act
        var rating = new UserLearningRating();

        // Assert
        rating.UserId.Should().BeEmpty();
    }

    [Fact]
    public void Can_Set_Properties()
    {
        // Arrange & Act
        var rating = new UserLearningRating
        {
            Id = 1,
            UserId = "user123",
            ContentId = 5,
            ItemId = 10,
            ScoreDate = DateTime.Today,
            Rating = 5
        };

        // Assert
        rating.Id.Should().Be(1);
        rating.UserId.Should().Be("user123");
        rating.ContentId.Should().Be(5);
        rating.ItemId.Should().Be(10);
        rating.ScoreDate.Should().Be(DateTime.Today);
        rating.Rating.Should().Be(5);
    }

    [Fact]
    public void ItemId_Is_Nullable()
    {
        // Arrange & Act
        var rating = new UserLearningRating
        {
            UserId = "user123",
            ContentId = 5,
            ItemId = null
        };

        // Assert
        rating.ItemId.Should().BeNull();
    }

    [Fact]
    public void Rating_Is_Byte_Type()
    {
        // Arrange & Act
        var rating = new UserLearningRating
        {
            Rating = 4
        };

        // Assert
        rating.Rating.Should().Be(4);
        ((object)rating.Rating).Should().BeOfType<byte>();
    }

    [Fact]
    public void Content_Navigation_Property_Defaults_To_Null()
    {
        // Act
        var rating = new UserLearningRating();

        // Assert
        rating.Content.Should().BeNull();
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
        var rating = new UserLearningRating
        {
            Content = content
        };

        // Assert
        rating.Content.Should().NotBeNull();
        rating.Content!.NameEnglish.Should().Be("Test");
    }
}
