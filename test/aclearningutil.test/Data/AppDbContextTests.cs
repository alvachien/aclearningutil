using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.test.Helpers;

namespace aclearningutil.test.Data;

public class AppDbContextTests : IDisposable
{
    private readonly AppDbContext _context;

    public AppDbContextTests()
    {
        _context = TestDbContextFactory.CreateInMemoryDbContext();
    }

    [Fact]
    public async Task Can_Add_TtsMapping()
    {
        // Arrange
        var mapping = new TtsMapping
        {
            Sentence = "Hello world",
            FileName = "hello.wav",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        _context.TtsMappings.Add(mapping);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.TtsMappings.FirstOrDefaultAsync(m => m.Sentence == "Hello world");
        result.Should().NotBeNull();
        result!.FileName.Should().Be("hello.wav");
    }

    [Fact]
    public async Task Can_Query_TtsMapping_By_Sentence()
    {
        // Arrange
        var mapping1 = new TtsMapping { Sentence = "Test 1", FileName = "test1.wav", CreatedAt = DateTime.UtcNow };
        var mapping2 = new TtsMapping { Sentence = "Test 2", FileName = "test2.wav", CreatedAt = DateTime.UtcNow };
        _context.TtsMappings.AddRange(mapping1, mapping2);
        await _context.SaveChangesAsync();

        // Act
        var result = await _context.TtsMappings.FirstOrDefaultAsync(m => m.Sentence == "Test 2");

        // Assert
        result.Should().NotBeNull();
        result!.FileName.Should().Be("test2.wav");
    }

    [Fact(Skip = "InMemory provider doesn't enforce unique constraints - this is tested with SQLite")]
    public async Task Sentence_Should_Be_Unique()
    {
        // Note: This test would work with SQLite but InMemory doesn't enforce unique constraints
        // Arrange
        var mapping1 = new TtsMapping { Sentence = "Unique sentence", FileName = "file1.wav", CreatedAt = DateTime.UtcNow };
        var mapping2 = new TtsMapping { Sentence = "Unique sentence", FileName = "file2.wav", CreatedAt = DateTime.UtcNow };

        _context.TtsMappings.Add(mapping1);
        await _context.SaveChangesAsync();

        // Act & Assert
        _context.TtsMappings.Add(mapping2);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        exception.Should().NotBeNull();
    }

    [Fact]
    public async Task Can_Delete_TtsMapping()
    {
        // Arrange
        var mapping = new TtsMapping { Sentence = "Delete me", FileName = "delete.wav", CreatedAt = DateTime.UtcNow };
        _context.TtsMappings.Add(mapping);
        await _context.SaveChangesAsync();

        // Act
        _context.TtsMappings.Remove(mapping);
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.TtsMappings.FirstOrDefaultAsync(m => m.Sentence == "Delete me");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Can_Update_TtsMapping()
    {
        // Arrange
        var mapping = new TtsMapping { Sentence = "Update me", FileName = "old.wav", CreatedAt = DateTime.UtcNow };
        _context.TtsMappings.Add(mapping);
        await _context.SaveChangesAsync();

        // Act
        mapping.FileName = "new.wav";
        await _context.SaveChangesAsync();

        // Assert
        var result = await _context.TtsMappings.FirstOrDefaultAsync(m => m.Sentence == "Update me");
        result.Should().NotBeNull();
        result!.FileName.Should().Be("new.wav");
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
