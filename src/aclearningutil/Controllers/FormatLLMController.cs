using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using aclearningutil.Models;
using aclearningutil.Util;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [EnableRateLimiting("LLMAndTTS")]
    public class FormatLLMController : ControllerBase
    {
        private const int MaxContextLength = 10000;

        private readonly IConfiguration Configuration;
        private readonly ILogger<FormatLLMController> _logger;

        public FormatLLMController(IConfiguration configuration, ILogger<FormatLLMController> logger)
        {
            Configuration = configuration;
            _logger = logger;
        }

        [Route("[action]")]
        [HttpPost]
        public async Task<ActionResult<LLMReplyContent>> AskAnything([FromBody] FormatLLMInput input, CancellationToken cancellationToken)
        {
            if (String.IsNullOrEmpty(input.Context))
            {
                _logger.LogWarning("Context is empty for get LLM reply");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = "Context is mandatory.",
                    ContentType = "text/plain"
                };
            }

            if (input.Context.Length > MaxContextLength)
            {
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = $"Context is too long (max {MaxContextLength} characters).",
                    ContentType = "text/plain"
                };
            }

            string initcontent = input.FormatType switch
            {
                "math" => "你是经验丰富的数学老师。",
                "physics" => "你是经验丰富的物理老师。",
                "chemistry" => "你是经验丰富的化学老师。",
                _ => string.Empty
            };

            if (string.IsNullOrEmpty(initcontent))
            {
                return BadRequest("FormatType must be 'math', 'physics', or 'chemistry'.");
            }

            var conv = new LLMConversation { model = LLMUtil.deepseekModelName };
            List<LLMConversationMessage> tmpmsgs =
            [
                new() { role = "system", content = initcontent },
                new() { role = "user", content = input.Context },
            ];
            conv.messages = [.. tmpmsgs];
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
            if (String.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("DeepSeek API key is not configured.");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "LLM service is not configured.",
                    ContentType = "text/plain"
                };
            }

            string result = await LLMUtil.SendPostRequestAsync(LLMUtil.deepseekAPIUrl, jsonContent, apiKey, cancellationToken);
            if (!result.StartsWith("ERROR: ", StringComparison.Ordinal))
            {
                JsonObject? jsonresult = JsonObject.Parse(result)?.AsObject();
                var rstmsg = jsonresult?["choices"]?[0]?["message"];

                return new LLMReplyContent()
                {
                    Content = rstmsg?["content"]?.GetValue<string>() ?? string.Empty
                };
            }

            _logger.LogWarning("LLM call failed: {Result}", result);
            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.BadGateway,
                Content = "LLM service returned an error.",
                ContentType = "text/plain"
            };
        }
    }
}
