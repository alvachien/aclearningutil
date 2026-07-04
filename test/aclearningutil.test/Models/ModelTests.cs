using FluentAssertions;
using aclearningutil.Models;

namespace aclearningutil.test.Models;

public class ModelTests
{
    [Fact]
    public void SentenceJsonMap_DefaultConstructor_InitializesEmptyStrings()
    {
        // Act
        var map = new SentenceJsonMap();

        // Assert
        map.Sentence.Should().BeEmpty();
        map.FileName.Should().BeEmpty();
    }

    [Fact]
    public void SentenceJsonMap_CanSetProperties()
    {
        // Arrange & Act
        var map = new SentenceJsonMap
        {
            Sentence = "Test sentence",
            FileName = "test.wav"
        };

        // Assert
        map.Sentence.Should().Be("Test sentence");
        map.FileName.Should().Be("test.wav");
    }

    [Fact]
    public void AudioFile_DefaultConstructor_InitializesEmptyString()
    {
        // Act
        var audioFile = new AudioFile();

        // Assert
        audioFile.AudioFileUrl.Should().BeEmpty();
    }

    [Fact]
    public void AudioFile_CanSetAudioFileUrl()
    {
        // Arrange & Act
        var audioFile = new AudioFile
        {
            AudioFileUrl = "audio/test.wav"
        };

        // Assert
        audioFile.AudioFileUrl.Should().Be("audio/test.wav");
    }

    [Fact]
    public void FormatLLMInput_DefaultConstructor_SetsDefaultValues()
    {
        // Act
        var input = new FormatLLMInput();

        // Assert
        input.FormatType.Should().Be("math");
        input.Context.Should().BeEmpty();
    }

    [Fact]
    public void LLMReplyContent_DefaultConstructor_InitializesEmptyString()
    {
        // Act
        var reply = new LLMReplyContent();

        // Assert
        reply.Content.Should().BeEmpty();
    }

    [Fact]
    public void LLMConversationMessage_DefaultConstructor_InitializesEmptyStrings()
    {
        // Act
        var message = new LLMConversationMessage();

        // Assert
        message.role.Should().BeEmpty();
        message.content.Should().BeEmpty();
    }

    [Fact]
    public void LLMConversation_DefaultConstructor_InitializesEmptyModelAndEmptyMessages()
    {
        // Act
        var conversation = new LLMConversation();

        // Assert
        conversation.model.Should().BeEmpty();
        conversation.messages.Should().NotBeNull();
        conversation.messages.Should().BeEmpty();
    }
}
