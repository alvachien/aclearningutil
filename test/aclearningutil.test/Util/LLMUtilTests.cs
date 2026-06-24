using FluentAssertions;
using aclearningutil.Util;

namespace aclearningutil.test.Util;

public class LLMUtilTests
{
    [Fact]
    public void LLMUtil_Constants_AreNotEmpty()
    {
        // Assert
        LLMUtil.deepseekModelName.Should().NotBeEmpty();
        LLMUtil.deepseekAPIUrl.Should().NotBeEmpty();
    }

    [Fact]
    public void LLMUtil_DeepseekModelName_HasExpectedValue()
    {
        // Assert
        LLMUtil.deepseekModelName.Should().Be("deepseek-chat");
    }

    [Fact]
    public void LLMUtil_DeepseekAPIUrl_HasExpectedValue()
    {
        // Assert
        LLMUtil.deepseekAPIUrl.Should().Be("https://api.deepseek.com/v1/chat/completions");
    }

    [Fact]
    public void LLMUtil_CanCreateInstance()
    {
        // Note: LLMUtil has static methods, so we're just verifying the class is accessible
        // Act & Assert
        var type = typeof(LLMUtil);
        type.Should().NotBeNull();
        type.IsClass.Should().BeTrue();
    }
}
