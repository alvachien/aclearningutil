using FluentAssertions;
using aclearningutil.Utility;

namespace aclearningutil.test.Utility;

public class AliTokenUtilTests
{
    [Fact]
    public void AliToken_DefaultConstructor_InitializesWithEmptyValues()
    {
        // Act
        var token = new AliToken();

        // Assert
        token.ErrMsg.Should().BeEmpty();
        token.Token.Should().NotBeNull();
        token.Token.UserId.Should().BeEmpty();
        token.Token.Id.Should().BeEmpty();
        token.Token.ExpireTime.Should().Be(0);
    }

    [Fact]
    public void AliTokenDetail_DefaultConstructor_InitializesWithEmptyValues()
    {
        // Act
        var detail = new AliTokenDetail();

        // Assert
        detail.UserId.Should().BeEmpty();
        detail.Id.Should().BeEmpty();
        detail.ExpireTime.Should().Be(0);
    }

    [Fact]
    public void AliToken_CanSetProperties()
    {
        // Arrange & Act
        var token = new AliToken
        {
            ErrMsg = "Error message",
            Token = new AliTokenDetail
            {
                UserId = "user123",
                Id = "token456",
                ExpireTime = 3600
            }
        };

        // Assert
        token.ErrMsg.Should().Be("Error message");
        token.Token.UserId.Should().Be("user123");
        token.Token.Id.Should().Be("token456");
        token.Token.ExpireTime.Should().Be(3600);
    }

    [Fact]
    public void AliTokenUtil_CanCreateInstance()
    {
        // Act
        var util = new AliTokenUtil();

        // Assert
        util.Should().NotBeNull();
    }
}
