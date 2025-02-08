using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using aclearningutil.Util;
using System.Net;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class TTSController : ControllerBase
    {
        public class SentenceJsonMap
        {
            public string Sentence { get; set; }
            public string FileName { get; set; }
        }

        public class AudioFile
        {
            public string AudioFileUrl { get; set; }
        }

        private string subfolder = "";
        private const string jsonMapFile = "tts_map.json";
        private readonly IConfiguration Configuration;

        public TTSController(IConfiguration configuration) {
            Configuration = configuration;

            subfolder = System.IO.Directory.GetCurrentDirectory() + "\\AudioFiles\\";
            if (!System.IO.File.Exists(subfolder + jsonMapFile))
            {
                var maps = new List<SentenceJsonMap>();
                string json = JsonSerializer.Serialize(maps);
                System.IO.File.WriteAllText(subfolder + jsonMapFile, json);
            }
        }


        [HttpGet("details")]
        public async Task<ActionResult<AudioFile>> GetTTS(string sentence)
        {
            if (String.IsNullOrEmpty(sentence))
            {
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Sentence is mandatory.",
                    ContentType = "text/plain"
                };
            }

            // Check file exist already
            bool isfileexist = false;
            var filecontent = System.IO.File.ReadAllText(subfolder + jsonMapFile);
            var allfiles = JsonSerializer.Deserialize<List<SentenceJsonMap>>(filecontent);
            string wavfile = string.Empty;
            foreach(var file in allfiles)
            {
                if (string.CompareOrdinal(sentence, file.Sentence) == 0)
                {
                    isfileexist = true;
                    wavfile = file.FileName;
                }
            }

            if (!isfileexist)
            {
                var appKey = Configuration["Aliyun:TTSAPIKey"];
                var appAccessKey = Configuration["Aliyun:TTSAccessKey"];
                var appAccessSecret = Configuration["Aliyun:TTSAccessSecret"];
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
                HttpClient client = new HttpClient();
                HttpResponseMessage response = null;
                response = client.GetAsync(url).Result;
                string contentType = null;

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

                    byte[] audioBuff = response.Content.ReadAsByteArrayAsync().Result;
                    FileStream fs = new FileStream(subfolder + wavfile, FileMode.Create);
                    fs.Write(audioBuff, 0, audioBuff.Length);
                    fs.Flush();
                    fs.Close();
                    System.Console.WriteLine("The GET request succeed!");

                    try
                    {
                        allfiles.Add(new SentenceJsonMap()
                        {
                            Sentence = sentence,
                            FileName = wavfile,
                        });
                        string json = JsonSerializer.Serialize(allfiles);
                        System.IO.File.WriteAllText(subfolder + jsonMapFile, json);
                    }
                    catch (Exception ex)
                    {
                        return new ContentResult
                        {
                            StatusCode = (int)HttpStatusCode.InternalServerError,
                            Content = "Update JSON file failed: " + ex.Message,
                            ContentType = "text/plain"
                        };
                    }
                }
                else
                {
                    Console.WriteLine("Response status code and reason phrase: " +
                        response.StatusCode + " " + response.ReasonPhrase);
                    string responseBodyAsText = response.Content.ReadAsStringAsync().Result;
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
