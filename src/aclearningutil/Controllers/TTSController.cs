using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public class TTSController : ControllerBase
    {
        private string subfolder = "";
        private readonly IConfiguration Configuration;
        private readonly ILogger<TTSController> _logger;
        private readonly AppDbContext _dbContext;

        public TTSController(IConfiguration configuration, ILogger<TTSController> logger, AppDbContext dbContext)
        {
            Configuration = configuration;
            _logger = logger;
            _dbContext = dbContext;

            subfolder = Path.Combine(Directory.GetCurrentDirectory(), "AudioFiles");
            Directory.CreateDirectory(subfolder);
        }

        [HttpGet("details")]
        public async Task<ActionResult<AudioFile>> GetTTS(string sentence)
        {
            if (String.IsNullOrEmpty(sentence))
            {
                _logger.LogError("Sentence is empty!");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Sentence is mandatory.",
                    ContentType = "text/plain"
                };
            }

            // Check file exist already
            var mapping = await _dbContext.TtsMappings
                .FirstOrDefaultAsync(m => m.Sentence == sentence);

            bool isfileexist = mapping != null;
            string wavfile = mapping?.FileName ?? string.Empty;

            if (!isfileexist)
            {
                _logger.LogInformation($"File for {sentence} not found!");

                var appKey = Configuration["Aliyun:TTSAPIKey"];
                var appAccessKey = Configuration["Aliyun:TTSAccessKey"] ?? string.Empty;
                var appAccessSecret = Configuration["Aliyun:TTSAccessSecret"] ?? string.Empty;
                string url = "http://nls-gateway.aliyuncs.com/stream/v1/tts";
                url = url + "?appkey=" + appKey;
                var util = new AliTokenUtil();
                var newtoken = await util.getToken(appAccessKey, appAccessSecret);

                url = url + "&token=" + newtoken.Token.Id;
                url = url + "&text=" + sentence;
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
                Console.WriteLine(url);
                /**
                 * 发送HTTPS GET请求，处理服务端的响应。
                 */
                using HttpClient client = new();
                HttpResponseMessage response = await client.GetAsync(url);
                string contentType = string.Empty;

                if (response.IsSuccessStatusCode)
                {
                    string[] typesArray = response.Content.Headers.GetValues("Content-Type").ToArray();
                    if (typesArray.Length > 0)
                    {
                        contentType = typesArray.First();
                    }
                }

                if ("audio/mpeg".Equals(contentType))
                {
                    wavfile = Guid.NewGuid().ToString() + ".wav";

                    byte[] audioBuff = await response.Content.ReadAsByteArrayAsync();
                    await using FileStream fs = new(subfolder + wavfile, FileMode.Create);
                    await fs.WriteAsync(audioBuff);

                    try
                    {
                        var newMapping = new TtsMapping
                        {
                            Sentence = sentence,
                            FileName = wavfile,
                            CreatedAt = DateTime.UtcNow
                        };
                        _dbContext.TtsMappings.Add(newMapping);
                        await _dbContext.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
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
                    Console.WriteLine("Response status code and reason phrase: " +
                        response.StatusCode + " " + response.ReasonPhrase);
                    string responseBodyAsText = await response.Content.ReadAsStringAsync();
                    Console.WriteLine("The GET request failed: " + responseBodyAsText);

                    return new ContentResult
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
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

            //{
            //    this.Response.StatusCode = 200;
            //    this.Response.Headers.Add(HeaderNames.ContentDisposition, $"attachment; filename=\"{Path.GetFileName(subfolder + wavfile)}\"");
            //    this.Response.Headers.Add(HeaderNames.ContentType, "application/octet-stream");
            //    var inputStream = new FileStream(subfolder + wavfile, FileMode.Open, FileAccess.Read);
            //    var outputStream = this.Response.Body;
            //    const int bufferSize = 1 << 10;
            //    var buffer = new byte[bufferSize];
            //    while (true)
            //    {
            //        var bytesRead = await inputStream.ReadAsync(buffer, 0, bufferSize);
            //        if (bytesRead == 0) break;
            //        await outputStream.WriteAsync(buffer, 0, bytesRead);
            //    }
            //    await outputStream.FlushAsync();
            //}
        }
    }
}
