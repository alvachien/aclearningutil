using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using aclearningutil.Controllers;

namespace aclearningutil.test.Controllers;

/// <summary>
/// The full path-validation surface of <see cref="StorageController"/> — the only
/// endpoint that turns a caller-controlled route segment into a filesystem path.
/// Covers the sub-folder allow-list, the extension allow-list, traversal rejection
/// (including the nested sub-path contract used by the listening audio folders),
/// content types, caching, and the 404 for missing files.
/// </summary>
public class StorageControllerTests : IDisposable
{
    private readonly string _rootDir;
    private readonly StorageController _controller;

    public StorageControllerTests()
    {
        // Temp ContentRoot; per-test files are created on demand below.
        _rootDir = Path.Combine(Path.GetTempPath(), "storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_rootDir, "Storage"));

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(_rootDir);

        _controller = new StorageController(mockEnv.Object, new Mock<ILogger<StorageController>>().Object)
        {
            // GetFile appends Cache-Control to Response.Headers — needs a real HttpContext.
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    /// <summary>Writes a file under Storage/ and returns its route-facing path.</summary>
    private string CreateFile(string relativePath, string content = "{}")
    {
        var fullPath = Path.Combine(_rootDir, "Storage", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);
        return relativePath;
    }

    /// <summary>
    /// Calls the action and disposes the returned stream immediately — a leaked
    /// handle would break the temp-directory cleanup on Windows.
    /// </summary>
    private IActionResult GetFileAndDrain(string filepath)
    {
        var result = _controller.GetFile(filepath);
        if (result is FileStreamResult fsr)
        {
            fsr.FileStream.Dispose();
        }
        return result;
    }

    // ── happy path ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("learnenglish/words.json", "application/json")]
    [InlineData("learnchinese/data.json", "application/json")]
    [InlineData("knowledge-exercises/data.json", "application/json")]
    [InlineData("formula/formula.json", "application/json")]
    [InlineData("englishlistening/data.json", "application/json")]
    [InlineData("learnenglish/figure.png", "image/png")]
    [InlineData("learnenglish/photo.jpg", "image/jpeg")]
    [InlineData("learnenglish/photo.jpeg", "image/jpeg")]
    [InlineData("englishlistening/clip.mp3", "audio/mpeg")]
    public void ServesAllowedSubfolderWithCorrectContentType(string relativePath, string expectedContentType)
    {
        CreateFile(relativePath);

        var result = GetFileAndDrain(relativePath);

        // The action returns File(stream, contentType) — MVC wraps it as FileStreamResult in-proc.
        var fileResult = result.Should().BeAssignableTo<FileResult>().Subject;
        fileResult.ContentType.Should().Be(expectedContentType);
    }

    [Fact]
    public void SetsPublicOneHourCachingOnServedFiles()
    {
        CreateFile("learnenglish/words.json");

        GetFileAndDrain("learnenglish/words.json");

        _controller.HttpContext.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=3600");
    }

    [Fact]
    public void NestedSubPathInsideAnAllowedSubfolderIsServed()
    {
        // The contract the listening audio relies on: only the FIRST segment is
        // allow-listed; the filename part may carry further (storage-internal) slashes.
        var relative = CreateFile("englishlistening/ltt1_mp3/unit01.mp3");

        var result = GetFileAndDrain(relative);

        result.Should().BeAssignableTo<FileResult>();
    }

    [Theory]
    [InlineData("LEARNENGLISH/Words.JSON")] // both allow-lists are OrdinalIgnoreCase
    [InlineData("learnenglish\\words.json")] // backslashes are normalized to slashes
    [InlineData("/learnenglish/words.json/")] // leading/trailing slashes are trimmed
    public void CaseAndSlashNormalizationStillResolvesWithinStorage(string filepath)
    {
        CreateFile("learnenglish/words.json");

        var result = GetFileAndDrain(filepath);

        // Reaching a BadRequest here means a normalization regression for this exact input.
        result.Should().BeAssignableTo<FileResult>();
    }

    // ── traversal rejection ────────────────────────────────────────────────

    [Theory]
    [InlineData("../Storage/learnenglish/words.json")]
    [InlineData("learnenglish/../../appsettings.json")]
    [InlineData("learnenglish/../learnchinese/data.json")] // .. anywhere, even between real folders
    [InlineData("..")]
    [InlineData("learnenglish/....//words.json")] // "...." still contains ".."
    [InlineData("learnenglish/..%2e/words.json")] // encoded traversal arrives decoded as ".."
    public void DirectoryTraversalIsRejectedWithBadRequest(string filepath)
    {
        var result = GetFileAndDrain(filepath);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void DoubleSlashIsRejectedBecauseItNeverNamesAStoragePath()
    {
        CreateFile("learnenglish/words.json");

        var result = GetFileAndDrain("learnenglish//words.json");

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void PathOutsideStorageWithOnlyValidSegmentNamesIsRejectedAtTheContainmentCheck()
    {
        // "Storage" is not an allowed subfolder; "appsettings.json" has no allowed extension.
        // Neither reaches the containment check, but both must not 200:
        var wrongFolder = GetFileAndDrain("Storage/appsettings.json");
        var wrongExt = GetFileAndDrain("learnenglish/appsettings.exe");

        wrongFolder.Should().BeOfType<BadRequestObjectResult>(); // invalid subfolder (wordaudio-style sibling)
        wrongExt.Should().BeOfType<BadRequestObjectResult>();    // invalid extension
    }

    // ── allow-list negatives ───────────────────────────────────────────────

    [Theory]
    [InlineData("wordaudio/hello.mp3")] // real folder inside Storage/, but NOT a served subfolder
    [InlineData("anything/words.json")]
    [InlineData("learnenglish2/words.json")] // prefix match must not pass the exact-name set
    public void DisallowedSubfolderIsRejected(string filepath)
    {
        var result = GetFileAndDrain(filepath);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("learnenglish/evil.exe")]
    [InlineData("learnenglish/script.sh")]
    [InlineData("learnenglish/noextension")]
    [InlineData("learnenglish/words.json.exe")] // extension is computed on the last dot only
    public void DisallowedExtensionIsRejected(string filepath)
    {
        CreateFile("learnenglish/words.json"); // sibling must exist to rule out accidental 404

        var result = GetFileAndDrain(filepath);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── shape and existence errors ─────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void BlankPathIsBadRequest(string filepath)
    {
        var result = GetFileAndDrain(filepath);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("words.json")] // no subfolder at all
    [InlineData("learnenglish/")] // filename segment missing
    [InlineData("/learnenglish")] // empty subfolder
    public void MissingSubfolderOrFilenameIsBadRequest(string filepath)
    {
        var result = GetFileAndDrain(filepath);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void MissingFileInsideAValidPathReturns404()
    {
        var result = GetFileAndDrain("learnenglish/does-not-exist.json");

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void EmptyFileStillServes200WithItsContentType()
    {
        // The SPA treats an empty-but-present content file as valid; the controller
        // must not special-case zero length.
        CreateFile("learnenglish/empty.json", string.Empty);

        var result = GetFileAndDrain("learnenglish/empty.json");

        result.Should().BeAssignableTo<FileResult>().Which.ContentType.Should().Be("application/json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootDir, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle must not fail the test run; the OS temp dir is fair game.
        }
    }
}
