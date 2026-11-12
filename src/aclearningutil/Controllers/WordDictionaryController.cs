using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using aclearningutil.Models;

namespace aclearningutil.Controllers;

/// <summary>
/// Serves per-word dictionary detail (Chinese definitions and bilingual
/// example sentences) read from <c>Storage/words.db</c>. Requires authentication.
/// URL pattern: <c>GET /api/WordDictionary?word={word}</c>.
/// Returns <c>404 NotFound</c> when the word is blank, the database is absent,
/// or the word has no row (the UI then hides the sections). A known word always
/// answers <c>200</c>, even when its detail lists are empty.
/// Like <see cref="WordAudioController"/>, this reads only a local file, so it
/// is intentionally not rate-limited (the LLMAndTTS limiter guards external spend).
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class WordDictionaryController : ControllerBase
{
    // Examples per word are capped: the median row carries 5 and the p95 15,
    // while outliers reach 77 - too many for a study card. Truncating in SQL
    // keeps the payload bounded no matter which word is asked.
    private const int MaxExamples = 10;

    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<WordDictionaryController> _logger;

    public WordDictionaryController(IWebHostEnvironment environment, ILogger<WordDictionaryController> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Returns the definitions and example sentences for <paramref name="word"/>.
    /// The lookup is case-insensitive, and spelling variants resolve to their
    /// base word (<c>words.base_word</c>), whose row carries the merged detail.
    /// </summary>
    /// <param name="word">The word or phrase to look up (e.g. "hello", "at present").</param>
    /// <param name="cancellationToken">Request cancellation token.</param>
    /// <response code="200">The dictionary entry as JSON.</response>
    /// <response code="404">The word is blank, words.db is absent, or the word is unknown.</response>
    [HttpGet]
    [ProducesResponseType(typeof(WordDictionaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string word, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return NotFound();
        }

        var dbPath = Path.Combine(_environment.ContentRootPath, "Storage", "words.db");
        if (!System.IO.File.Exists(dbPath))
        {
            _logger.LogWarning("words.db not found at {Path}; cannot serve dictionary for {Word}.", dbPath, word);
            return NotFound();
        }

        var entry = await ResolveEntryAsync(dbPath, word, cancellationToken);
        if (entry is null)
        {
            return NotFound();
        }

        // Same TTL as the recorded audio: the DB content only changes at deploy
        // time, and a long max-age keeps repeat study sessions off the API.
        Response.Headers.Append("Cache-Control", "public, max-age=2592000");
        return Ok(entry);
    }

    // Opens words.db read-only and assembles the entry for the word, or null
    // when the words table has no row for it. Detail rows live under the base
    // spelling (apply_base_word_model in fetch_word.py merges variants into it),
    // so the child queries run against COALESCE(base_word, word); that resolved
    // key matches the stored spelling exactly, hence no COLLATE needed there.
    // SQLite serves concurrent readers and the app never writes this database.
    private static async Task<WordDictionaryDto?> ResolveEntryAsync(
        string dbPath, string word, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        string key;
        await using (var keyCmd = connection.CreateCommand())
        {
            keyCmd.CommandText =
                "SELECT COALESCE(base_word, word) FROM words WHERE word = @word COLLATE NOCASE LIMIT 1";
            keyCmd.Parameters.AddWithValue("@word", word);
            var resolved = await keyCmd.ExecuteScalarAsync(cancellationToken);
            if (resolved is null or DBNull)
            {
                return null;
            }
            key = (string)resolved;
        }

        var definitions = new List<WordDefinitionDto>();
        await using (var defCmd = connection.CreateCommand())
        {
            // ORDER BY seq, rowid: base-word merges stamp duplicate-free rows
            // with seq = -1, so seq alone cannot keep the authored order.
            defCmd.CommandText =
                "SELECT part_of_speech, meaning FROM definitions"
                + " WHERE word = @key ORDER BY seq, rowid";
            defCmd.Parameters.AddWithValue("@key", key);
            await using var reader = await defCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                definitions.Add(new WordDefinitionDto
                {
                    PartOfSpeech = reader.GetString(0),
                    Meaning = reader.GetString(1),
                });
            }
        }

        var examples = new List<WordExampleDto>();
        await using (var exCmd = connection.CreateCommand())
        {
            exCmd.CommandText =
                "SELECT source, translation FROM examples"
                + " WHERE word = @key ORDER BY seq, rowid LIMIT @max";
            exCmd.Parameters.AddWithValue("@key", key);
            exCmd.Parameters.AddWithValue("@max", MaxExamples);
            await using var reader = await exCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                examples.Add(new WordExampleDto
                {
                    Source = reader.GetString(0),
                    Translation = reader.GetString(1),
                });
            }
        }

        return new WordDictionaryDto
        {
            Word = key,
            Definitions = definitions,
            Examples = examples,
        };
    }
}
