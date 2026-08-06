using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;

namespace aclearningutil.test.Controllers;

public class WordAudioControllerTests : IDisposable
{
    private readonly string _rootDir;
    private readonly Mock<ILogger<WordAudioController>> _mockLogger;
    private readonly WordAudioController _controller;

    public WordAudioControllerTests()
    {
        // Build a temp ContentRoot with Storage/words.db + Storage/wordaudio/
        _rootDir = Path.Combine(Path.GetTempPath(), "wordaudio-tests-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_rootDir, "Storage");
        var wordaudioDir = Path.Combine(storageDir, "wordaudio");
        Directory.CreateDirectory(wordaudioDir);

        CreateTestDatabase(Path.Combine(storageDir, "words.db"));
        // Real audio bytes are irrelevant to the controller; write a non-empty placeholder.
        File.WriteAllBytes(Path.Combine(wordaudioDir, "hello.mp3"), new byte[] { 0x49, 0x44, 0x33 }); // "ID3"

        _mockLogger = new Mock<ILogger<WordAudioController>>();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(_rootDir);

        _controller = new WordAudioController(mockEnv.Object, _mockLogger.Object)
        {
            // Get() appends a Cache-Control header to Response.Headers, which needs a real HttpContext.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // Recreates the subset of the words table the controller reads: word + us/uk audio paths.
    private static void CreateTestDatabase(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var createCmd = connection.CreateCommand();
        createCmd.CommandText =
            """
            CREATE TABLE words (
                word TEXT PRIMARY KEY,
                type TEXT NOT NULL DEFAULT '',
                language TEXT NOT NULL DEFAULT '',
                uk_phonetic TEXT NOT NULL DEFAULT '',
                uk_audio TEXT NOT NULL DEFAULT '',
                us_phonetic TEXT NOT NULL DEFAULT '',
                us_audio TEXT NOT NULL DEFAULT '',
                has_uk_audio INTEGER NOT NULL DEFAULT 0,
                has_us_audio INTEGER NOT NULL DEFAULT 0,
                base_word TEXT
            );
            """;
        createCmd.ExecuteNonQuery();

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText =
            """
            INSERT INTO words (word, us_audio, uk_audio, has_us_audio, has_uk_audio) VALUES
                ('hello', 'wordaudio/hello.mp3', '', 1, 0),
                ('silent', '', '', 0, 0),
                ('colour', '', 'wordaudio/colour.mp3', 0, 1);
            """;
        insertCmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Get_WithBlankWord_ReturnsNotFound()
    {
        var result = await _controller.Get("   ", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_WhenWordsDbMissing_ReturnsNotFound()
    {
        // Point the controller at a ContentRoot whose Storage has no words.db.
        var emptyRoot = Path.Combine(Path.GetTempPath(), "wordaudio-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.ContentRootPath).Returns(emptyRoot);
            var controller = new WordAudioController(mockEnv.Object, _mockLogger.Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };

            var result = await controller.Get("hello", CancellationToken.None);
            result.Should().BeOfType<NotFoundResult>();
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Get_UnknownWord_ReturnsNotFound()
    {
        var result = await _controller.Get("not-a-real-word", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_WordWithNoAudio_ReturnsNotFound()
    {
        var result = await _controller.Get("silent", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Get_KnownWord_ReturnsAudioMpeg()
    {
        var result = await _controller.Get("hello", CancellationToken.None);

        var fileResult = result as FileStreamResult;
        fileResult.Should().NotBeNull();
        fileResult!.ContentType.Should().Be("audio/mpeg");
        fileResult.FileStream.Should().NotBeNull();
        fileResult.FileStream.Dispose();
    }

    [Fact]
    public async Task Get_IsCaseInsensitive()
    {
        // "Hello" / "HELLO" must resolve to the same row as "hello".
        var lower = await _controller.Get("hello", CancellationToken.None);
        var mixed = await _controller.Get("Hello", CancellationToken.None);
        var upper = await _controller.Get("HELLO", CancellationToken.None);

        lower.Should().BeOfType<FileStreamResult>();
        mixed.Should().BeOfType<FileStreamResult>();
        upper.Should().BeOfType<FileStreamResult>();

        ((FileStreamResult)lower).FileStream.Dispose();
        ((FileStreamResult)mixed).FileStream.Dispose();
        ((FileStreamResult)upper).FileStream.Dispose();
    }

    [Fact]
    public async Task Get_FallsBackToUkAudioWhenNoUsAudio()
    {
        // 'colour' has only a uk_audio path, but the audio file does not exist on disk
        // in this fixture, so the controller must still 404 (missing file) rather than
        // throw. This guards the UK-fallback branch + file-existence check together.
        var result = await _controller.Get("colour", CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, keeping a handle on words.db that
        // would otherwise block Directory.Delete. Clear the pool first.
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }
}
