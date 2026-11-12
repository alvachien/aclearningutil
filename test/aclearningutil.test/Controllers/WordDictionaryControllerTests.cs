using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;
using aclearningutil.Models;

namespace aclearningutil.test.Controllers;

public class WordDictionaryControllerTests : IDisposable
{
    private readonly string _rootDir;
    private readonly Mock<ILogger<WordDictionaryController>> _mockLogger;
    private readonly WordDictionaryController _controller;

    public WordDictionaryControllerTests()
    {
        // Build a temp ContentRoot with Storage/words.db carrying the subset of
        // the production schema the controller reads (fixture pattern shared
        // with WordAudioControllerTests).
        _rootDir = Path.Combine(Path.GetTempPath(), "worddict-tests-" + Guid.NewGuid().ToString("N"));
        var storageDir = Path.Combine(_rootDir, "Storage");
        Directory.CreateDirectory(storageDir);

        CreateTestDatabase(Path.Combine(storageDir, "words.db"));

        _mockLogger = new Mock<ILogger<WordDictionaryController>>();
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(_rootDir);

        _controller = new WordDictionaryController(mockEnv.Object, _mockLogger.Object)
        {
            // A successful Get appends a Cache-Control header to Response.Headers, which needs a real HttpContext.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

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
                base_word TEXT
            );
            CREATE TABLE definitions (
                word TEXT NOT NULL,
                seq INTEGER NOT NULL,
                part_of_speech TEXT NOT NULL DEFAULT '',
                meaning TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE examples (
                word TEXT NOT NULL,
                seq INTEGER NOT NULL,
                source TEXT NOT NULL DEFAULT '',
                translation TEXT NOT NULL DEFAULT ''
            );
            """;
        createCmd.ExecuteNonQuery();

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText =
            """
            INSERT INTO words (word, type, base_word) VALUES
                ('hello', 'word', NULL),
                ('color', 'word', NULL),
                ('colour', 'word', 'color'),
                ('nostuff', 'word', NULL);

            -- Definitions deliberately inserted out of seq order; the -1 row
            -- mirrors the seq the base-word merge stamps on deduplicated rows.
            INSERT INTO definitions (word, seq, part_of_speech, meaning) VALUES
                ('hello', 1, 'int.', 'greeting'),
                ('hello', 0, 'n.', 'hello; hallo'),
                ('hello', -1, 'n.', 'merged duplicate'),
                ('color', 0, 'n.', 'color; hue');

            INSERT INTO examples (word, seq, source, translation) VALUES
                ('hello', 0, 'Hello, Mary.', 'Hello, Mary.'),
                ('hello', 1, 'Say hello to your mother.', 'Greet your mother.');
            """;
        insertCmd.ExecuteNonQuery();

        // 10 more example rows so the server-side cap (10) is exercised.
        using var bulkCmd = connection.CreateCommand();
        var values = string.Join(
            ",",
            Enumerable.Range(2, 10).Select(i => $"('hello', {i}, 'Filler sentence {i}.', 'Example sentence {i}.')"));
        bulkCmd.CommandText = $"INSERT INTO examples (word, seq, source, translation) VALUES {values}";
        bulkCmd.ExecuteNonQuery();
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
        var emptyRoot = Path.Combine(Path.GetTempPath(), "worddict-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyRoot);
        try
        {
            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.ContentRootPath).Returns(emptyRoot);
            var controller = new WordDictionaryController(mockEnv.Object, _mockLogger.Object)
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
    public async Task Get_KnownWord_ReturnsOrderedDefinitionsAndCappedExamples()
    {
        var result = await _controller.Get("hello", CancellationToken.None);

        var entry = (result as OkObjectResult)?.Value as WordDictionaryDto;
        entry.Should().NotBeNull();
        entry!.Word.Should().Be("hello");
        // ORDER BY seq, rowid must win over insertion order (-1, 0, 1).
        entry.Definitions.Select(d => d.Meaning).Should().Equal("merged duplicate", "hello; hallo", "greeting");
        entry.Examples.Should().HaveCount(10);
        entry.Examples[0].Source.Should().Be("Hello, Mary.");
        // The 30-day cache header is part of the contract (bandwidth design);
        // a regression here would silently double the API traffic.
        _controller.HttpContext.Response.Headers["Cache-Control"].ToString()
            .Should().Be("public, max-age=2592000");
    }

    [Fact]
    public async Task Get_UnknownWord_SendsNoCacheHeader()
    {
        await _controller.Get("not-a-real-word", CancellationToken.None);

        _controller.HttpContext.Response.Headers.ContainsKey("Cache-Control").Should().BeFalse();
    }

    [Fact]
    public async Task Get_ResolvesSpellingVariantToBaseWord()
    {
        // 'colour' has no detail rows of its own; the entry must come from its
        // base 'color' and echo the canonical spelling.
        var result = await _controller.Get("colour", CancellationToken.None);

        var entry = (result as OkObjectResult)?.Value as WordDictionaryDto;
        entry.Should().NotBeNull();
        entry!.Word.Should().Be("color");
        entry.Definitions.Should().ContainSingle(d => d.Meaning == "color; hue");
    }

    [Fact]
    public async Task Get_IsCaseInsensitive()
    {
        var result = await _controller.Get("HELLO", CancellationToken.None);

        var entry = (result as OkObjectResult)?.Value as WordDictionaryDto;
        entry.Should().NotBeNull();
        entry!.Word.Should().Be("hello");
    }

    [Fact]
    public async Task Get_KnownWordWithoutDetail_ReturnsEmptyLists()
    {
        // A row can exist with no children (phonetics-only entries, stubs);
        // that is a 200 with empty lists, not a 404.
        var result = await _controller.Get("nostuff", CancellationToken.None);

        var entry = (result as OkObjectResult)?.Value as WordDictionaryDto;
        entry.Should().NotBeNull();
        entry!.Definitions.Should().BeEmpty();
        entry.Examples.Should().BeEmpty();
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, keeping a handle on words.db
        // that would otherwise block Directory.Delete. Clear the pool first.
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_rootDir))
        {
            Directory.Delete(_rootDir, recursive: true);
        }
    }
}
