using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using aclearningutil.Models;
using aclearningutil.Utility;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [EnableRateLimiting("LLMAndTTS")]
    public class EnglishLLMController : ControllerBase
    {
        private const int MaxContextLength = 10000;

        private readonly IConfiguration Configuration;
        private readonly ILogger<EnglishLLMController> _logger;

        public EnglishLLMController(IConfiguration configuration, ILogger<EnglishLLMController> logger) {
            Configuration = configuration;
            _logger = logger;
        }

        [HttpGet("details")]
        public async Task<ActionResult<LLMReplyContent>> GetLLMReply(string context, CancellationToken cancellationToken)
        {
            if (String.IsNullOrEmpty(context)) {
                _logger.LogWarning("Context is mandatory for get LLM reply");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = "Context is mandatory.",
                    ContentType = "text/plain"
                };
            }

            if (context.Length > MaxContextLength)
            {
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Content = $"Context is too long (max {MaxContextLength} characters).",
                    ContentType = "text/plain"
                };
            }

            // Build conversation locally (avoid instance fields that could leak across requests)
            var conv = new LLMConversation { model = LLMUtil.deepseekModelName };
            var listMessages = new List<LLMConversationMessage>
            {
                new() { role = "system", content = "You are a English teacher for Junior and Senior High School students." },
                new() { role = "user", content = context }
            };
            conv.messages = [.. listMessages];
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
            if(String.IsNullOrEmpty(apiKey))
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


