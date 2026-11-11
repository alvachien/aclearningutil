using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace aclearningutil.Controllers;

/// <summary>
/// Serves per-word pronunciation audio looked up in <c>Storage/words.db</c> and
/// streamed from <c>Storage/wordaudio/</c>. Requires authentication.
/// URL pattern: <c>GET /api/WordAudio?word={word}</c>.
/// Returns <c>404 NotFound</c> when the word is unknown or has no audio file.
/// Unlike <see cref="TTSController"/>, this endpoint reads only local files, so it
/// is intentionally not rate-limited (the LLMAndTTS limiter guards external spend).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class WordAudioController : ControllerBase
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<WordAudioController> _logger;

    public WordAudioController(IWebHostEnvironment environment, ILogger<WordAudioController> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Returns the US pronunciation MP3 for <paramref name="word"/>, falling back to
    /// the UK pronunciation when no US audio is available. The lookup is
    /// case-insensitive (e.g. "Africa" and "africa" resolve to the same row).
    /// </summary>
    /// <param name="word">The word or phrase to look up (e.g. "hello", "add ... to ...").</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <response code="200">The MP3 audio as <c>audio/mpeg</c>.</response>
    /// <response code="404">The word is missing, unknown, has no audio, or the audio file is absent.</response>
    [HttpGet]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string word, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return NotFound();
        }

        var storageFolder = Path.Combine(_environment.ContentRootPath, "Storage");
        var dbPath = Path.Combine(storageFolder, "words.db");
        if (!System.IO.File.Exists(dbPath))
        {
            _logger.LogWarning("words.db not found at {Path}; cannot serve audio for {Word}.", dbPath, word);
            return NotFound();
        }

        var audioRelPath = await ResolveAudioPathAsync(dbPath, word, cancellationToken);
        if (string.IsNullOrWhiteSpace(audioRelPath))
        {
            return NotFound();
        }

        // audioRelPath is like "wordaudio/France.f5cd26.mp3". Use only the basename
        // (defense in depth - ignore any folder the DB row claims) and require .mp3.
        var basename = Path.GetFileName(audioRelPath.Replace('\\', '/'));
        if (string.IsNullOrEmpty(basename) || !basename.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Audio path for {Word} is not an mp3: {Path}", word, audioRelPath);
            return NotFound();
        }

        var wordaudioFolder = Path.Combine(storageFolder, "wordaudio");
        var fullRoot = Path.GetFullPath(wordaudioFolder);
        var fullPath = Path.GetFullPath(Path.Combine(wordaudioFolder, basename));
        // Containment check prevents a crafted basename from escaping the folder.
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Resolved audio path outside wordaudio folder: {Path}", fullPath);
            return NotFound();
        }

        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        // 30 days: the MP3 library is replaced only at deploy time, so a long TTL
        // keeps repeat study sessions off the API. Trade-off: a replaced file is
        // picked up at most 30 days after the client first cached it.
        Response.Headers.Append("Cache-Control", "public, max-age=2592000");
        return File(stream, "audio/mpeg");
    }

    // Opens words.db read-only and returns the US (or UK fallback) audio path for the
    // word, or null when the word is absent or has no audio. SQLite handles concurrent
    // readers; the app never writes this database (it is authored by fetch_word.py).
    private static async Task<string?> ResolveAudioPathAsync(string dbPath, string word, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT us_audio, uk_audio FROM words WHERE word = @word COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("@word", word);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var usAudio = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (!string.IsNullOrWhiteSpace(usAudio))
        {
            return usAudio;
        }

        var ukAudio = reader.IsDBNull(1) ? null : reader.GetString(1);
        return string.IsNullOrWhiteSpace(ukAudio) ? null : ukAudio;
    }
}
