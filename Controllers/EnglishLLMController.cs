using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using aclearningutil.Models;
using aclearningutil.Util;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EnglishLLMController : ControllerBase
    {
        private readonly LLMConversation conv = new();
        private readonly List<LLMConversationMessage> listMessages = [];
        private readonly IConfiguration Configuration;
        private readonly ILogger<EnglishLLMController> _logger;

        public EnglishLLMController(IConfiguration configuration, ILogger<EnglishLLMController> logger) {
            Configuration = configuration;
            _logger = logger;

            conv.model = LLMUtil.deepseekModelName;
            listMessages.Add(new LLMConversationMessage()
                {
                    role = "system",
                    content =  "You are a English teacher for Junior and Senior High School students."
                }
            );
        }

        [HttpGet("details")]
        public async Task<ActionResult<LLMReplyContent>> GetLLMReply(string context)
        {
            if (String.IsNullOrEmpty(context)) {
                _logger.LogWarning("Context is mandatory for get LLM reply");
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Context is mandatory.",
                    ContentType = "text/plain"
                };
            }

            List<LLMConversationMessage> tmpmsgs = new List<LLMConversationMessage>();
            foreach(var msg in listMessages)
            {
                tmpmsgs.Add(msg);
            }
            tmpmsgs.Add(new LLMConversationMessage()
            {
                role = "user",
                content = context
            });
            conv.messages = [.. tmpmsgs];
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
            if(String.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("Failed to get API key");
            }
            else
            {
                string result = await LLMUtil.SendPostRequestAsync(LLMUtil.deepseekAPIUrl, jsonContent, apiKey);
                if (!result.StartsWith("ERROR: "))
                {
                    JsonObject jsonresult = (JsonObject)JsonObject.Parse(result);
                    var rstmsg = jsonresult["choices"][0]["message"];
                    //listMessages.Add(new LLMConversationMessage()
                    //{
                    //    role = (string)rstmsg["role"],
                    //    content = (string)rstmsg["content"]
                    //});

                    // 输出结果
                    return new LLMReplyContent()
                    {
                        Content = (string)rstmsg["content"]
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


