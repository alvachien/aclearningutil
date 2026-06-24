using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using aclearningutil.Models;
using aclearningutil.Util;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FormatLLMController : ControllerBase
    {
        private readonly LLMConversation conv = new();
        private readonly IConfiguration Configuration;
        private readonly ILogger<FormatLLMController> _logger;

        public FormatLLMController(IConfiguration configuration, ILogger<FormatLLMController> logger)
        {
            Configuration = configuration;
            _logger = logger;
            // Default LLM
            conv.model = LLMUtil.deepseekModelName;
        }

        [Route("[action]")]
        [HttpPost]
        public async Task<ActionResult<LLMReplyContent>> AskAnything([FromBody] FormatLLMInput input)
        {
            if (String.IsNullOrEmpty(input.Context))
            {
                _logger.LogWarning("Context is empty for get LLM reply");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Context is mandatory.",
                    ContentType = "text/plain"
                };
            }

            string initcontent = string.Empty;
            if (input.FormatType == "math")
            {
                initcontent = "你是经验丰富的数学老师。";
            }
            else if(input.FormatType == "physics")
            {
                initcontent = "你是经验丰富的物理老师。";
            }
            else if(input.FormatType == "chemistry")
            {
                initcontent = "你是经验丰富的化学老师。";
            }
            List<LLMConversationMessage> tmpmsgs =
            [
                new LLMConversationMessage()
                {
                    role = "system",
                    content = initcontent
                },
                new LLMConversationMessage()
                {
                    role = "user",
                    content = input.Context
                },
            ];
            conv.messages = [.. tmpmsgs];
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
            if (String.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("Failed to get API key");
            }
            else
            {
                string result = await LLMUtil.SendPostRequestAsync(LLMUtil.deepseekAPIUrl, jsonContent, apiKey);
                if (!result.StartsWith("ERROR: "))
                {
                    JsonObject? jsonresult = JsonObject.Parse(result)?.AsObject();
                    var rstmsg = jsonresult?["choices"]?[0]?["message"];
                    //listMessages.Add(new LLMConversationMessage()
                    //{
                    //    role = (string)rstmsg["role"],
                    //    content = (string)rstmsg["content"]
                    //});

                    // 输出结果
                    return new LLMReplyContent()
                    {
                        Content = rstmsg?["content"]?.GetValue<string>() ?? string.Empty
                    };
                }
            }

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.BadRequest,
                Content = "No returns from LLM.",
                ContentType = "text/plain"
            };
        }
    }
}
