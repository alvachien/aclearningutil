using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Controllers;

public class TTSControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<IConfiguration> _mockConfig;
    private readonly Mock<ILogger<TTSController>> _mockLogger;
    private readonly TTSController _controller;

    public TTSControllerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
        _mockConfig = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<TTSController>>();
        _controller = new TTSController(_mockConfig.Object, _mockLogger.Object, _context);
    }

    [Fact]
    public async Task GetTTS_WithEmptySentence_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetTTS("");

        // Assert
        var contentResult = result.Result as ContentResult;
        contentResult.Should().NotBeNull();
        contentResult!.StatusCode.Should().Be(500);
        contentResult.Content.Should().Be("Sentence is mandatory.");
    }

    [Fact]
    public async Task GetTTS_WithNullSentence_ReturnsBadRequest()
    {
        // Act
        var result = await _controller.GetTTS(null!);

        // Assert
        var contentResult = result.Result as ContentResult;
        contentResult.Should().NotBeNull();
        contentResult!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetTTS_WithExistingMapping_ReturnsCachedFile()
    {
        // Arrange
        var sentence = "Cached sentence";
        var mapping = new TtsMapping
        {
            Sentence = sentence,
            FileName = "cached.wav",
            CreatedAt = DateTime.UtcNow
        };
        _context.TtsMappings.Add(mapping);
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetTTS(sentence);

        // Assert
        var audioFile = result.Value as AudioFile;
        audioFile.Should().NotBeNull();
        audioFile!.AudioFileUrl.Should().Be("audio/cached.wav");
    }

    [Fact(Skip = "Requires HTTP client mocking - integration test scenario")]
    public async Task GetTTS_WithNonExistingMapping_ReturnsNullAudioFile()
    {
        // Arrange
        var sentence = "New sentence";
        _mockConfig.Setup(c => c["Aliyun:TTSAPIKey"]).Returns("test-key");
        _mockConfig.Setup(c => c["Aliyun:TTSAccessKey"]).Returns("test-access-key");
        _mockConfig.Setup(c => c["Aliyun:TTSAccessSecret"]).Returns("test-access-secret");

        // Act
        var result = await _controller.GetTTS(sentence);

        // Assert
        // Since we're not mocking the HTTP calls, this will fail to get a token
        // and return an error. In a real test, you'd mock the HTTP client.
        var contentResult = result.Result as ContentResult;
        contentResult.Should().NotBeNull();
        contentResult!.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task Multiple_GetTTS_Calls_ShouldUseSameMapping()
    {
        // Arrange
        var sentence = "Repeated sentence";
        var mapping = new TtsMapping
        {
            Sentence = sentence,
            FileName = "repeated.wav",
            CreatedAt = DateTime.UtcNow
        };
        _context.TtsMappings.Add(mapping);
        await _context.SaveChangesAsync();

        // Act
        var result1 = await _controller.GetTTS(sentence);
        var result2 = await _controller.GetTTS(sentence);

        // Assert
        var audioFile1 = result1.Value as AudioFile;
        var audioFile2 = result2.Value as AudioFile;
        audioFile1.Should().NotBeNull();
        audioFile2.Should().NotBeNull();
        audioFile1!.AudioFileUrl.Should().Be(audioFile2!.AudioFileUrl);
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
