using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using aclearningutil.Util;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using System.Net;
using aclearningutil.Models;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [EnableRateLimiting("LLMAndTTS")]
    public class TTSController : ControllerBase
    {
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        // Per-sentence semaphore to prevent race conditions on concurrent TTS requests for the same sentence
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _sentenceLocks = new();

        private const int MaxSentenceLength = 1000;

        private readonly string _audioFolder;
        private readonly IConfiguration Configuration;
        private readonly ILogger<TTSController> _logger;
        private readonly AppDbContext _dbContext;

        public TTSController(IConfiguration configuration, ILogger<TTSController> logger, AppDbContext dbContext, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            _logger = logger;
            _dbContext = dbContext;

            _audioFolder = Path.Combine(environment.ContentRootPath, "AudioFiles");
            Directory.CreateDirectory(_audioFolder);
        }

        [HttpGet("details")]
        public async Task<ActionResult<AudioFile>> GetTTS(string sentence, CancellationToken cancellationToken)
        {
            if (String.IsNullOrEmpty(sentence))
            {
                _logger.LogError("Sentence is empty!");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = "Sentence is mandatory.",
                    ContentType = "text/plain"
                };
            }

            if (sentence.Length > MaxSentenceLength)
            {
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = $"Sentence is too long (max {MaxSentenceLength} characters).",
                    ContentType = "text/plain"
                };
            }

            // Acquire per-sentence lock to prevent race conditions
            var sentenceLock = _sentenceLocks.GetOrAdd(sentence, _ => new SemaphoreSlim(1, 1));
            await sentenceLock.WaitAsync(cancellationToken);
            try
            {
                // Check file exist already (inside lock)
                var mapping = await _dbContext.TtsMappings
                    .FirstOrDefaultAsync(m => m.Sentence == sentence, cancellationToken);

                bool isfileexist = mapping != null;
                string wavfile = mapping?.FileName ?? string.Empty;

                if (!isfileexist)
                {
                    _logger.LogInformation($"File for {sentence} not found!");

                    var appKey = Configuration["Aliyun:TTSAPIKey"];
                    var appAccessKey = Configuration["Aliyun:TTSAccessKey"] ?? string.Empty;
                    var appAccessSecret = Configuration["Aliyun:TTSAccessSecret"] ?? string.Empty;
                    string url = "https://nls-gateway.aliyuncs.com/stream/v1/tts";
                    url = url + "?appkey=" + appKey;
                    var util = new AliTokenUtil();
                    var newtoken = await util.getToken(appAccessKey, appAccessSecret);

                    url = url + "&token=" + newtoken.Token.Id;
                    url = url + "&text=" + Uri.EscapeDataString(sentence);
                    url = url + "&format=wav";
                    url = url + "&sample_rate=16000";
                    // voice 发音人，可选，默认是xiaoyun。
                    // url = url + "&voice=" + "xiaoyun";
                    // volume 音量，范围是0~100，可选，默认50。
                    // url = url + "&volume=" + 50;
                    // speech_rate 语速，范围是-500~500，可选，默认是0。
                    // url = url + "&speech_rate=" + 0;
                    // pitch_rate 语调，范围是-500~500，可选，默认是0。
                    // url = url + "&pitch_rate=" + 0;

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    HttpResponseMessage response = await SharedHttpClient.SendAsync(request, cancellationToken);
                    string contentType = string.Empty;

                    if (response.IsSuccessStatusCode)
                    {
                        if (response.Content.Headers.TryGetValues("Content-Type", out var typesValues))
                        {
                            string[] typesArray = typesValues.ToArray();
                            if (typesArray.Length > 0)
                            {
                                contentType = typesArray.First();
                            }
                        }
                    }

                    if ("audio/mpeg".Equals(contentType))
                    {
                        wavfile = Guid.NewGuid().ToString() + ".wav";

                        byte[] audioBuff = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                        await using FileStream fs = new(Path.Combine(_audioFolder, wavfile), FileMode.Create);
                        await fs.WriteAsync(audioBuff, cancellationToken);

                        try
                        {
                            var newMapping = new TtsMapping
                            {
                                Sentence = sentence,
                                FileName = wavfile,
                                CreatedAt = DateTime.UtcNow
                            };
                            _dbContext.TtsMappings.Add(newMapping);
                            await _dbContext.SaveChangesAsync(cancellationToken);
                        }
                        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 19)
                        {
                            // Unique constraint violation — another request already cached this sentence.
                            // Clean up the orphaned file and return the existing mapping.
                            _logger.LogWarning("Duplicate TTS mapping for sentence (race condition handled): {Sentence}", sentence);
                            try { System.IO.File.Delete(Path.Combine(_audioFolder, wavfile)); } catch { /* ignore cleanup failure */ }
                            var existingMapping = await _dbContext.TtsMappings
                                .FirstOrDefaultAsync(m => m.Sentence == sentence, cancellationToken);
                            wavfile = existingMapping?.FileName ?? wavfile;
                        }
                        catch (Exception ex)
                        {
                            // Clean up orphaned file on unexpected DB failure
                            try { System.IO.File.Delete(Path.Combine(_audioFolder, wavfile)); } catch { /* ignore cleanup failure */ }
                            return new ContentResult
                            {
                                StatusCode = (int)HttpStatusCode.InternalServerError,
                                Content = "Save to database failed: " + ex.Message,
                                ContentType = "text/plain"
                            };
                        }
                    }
                    else
                    {
                        _logger.LogWarning("TTS request failed: {StatusCode} {ReasonPhrase}",
                            response.StatusCode, response.ReasonPhrase);
                        string responseBodyAsText = await response.Content.ReadAsStringAsync(cancellationToken);

                        return new ContentResult
                        {
                            StatusCode = (int)HttpStatusCode.BadGateway,
                            Content = "The GET request failed: " + responseBodyAsText,
                            ContentType = "text/plain"
                        };
                    }
                }

                // Then streaming it out
                if (!string.IsNullOrEmpty(wavfile))
                    return new AudioFile()
                    {
                        AudioFileUrl = "audio/" + wavfile
                    };

                _logger.LogError($"Wave file for {wavfile} not found");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Wav file cannot found",
                    ContentType = "text/plain"
                };
            }
            finally
            {
                sentenceLock.Release();
            }
        }
    }
}
