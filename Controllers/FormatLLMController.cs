using aclearningutil.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class FormatLLMController : ControllerBase
    {
        private LLMConversation conv = new LLMConversation();
        private readonly IConfiguration Configuration;

        public FormatLLMController(IConfiguration configuration)
        {
            Configuration = configuration;

            conv.model = LLMUtil.deepseekModelName;
        }

        [Route("[action]")]
        [HttpPost]
        public async Task<ActionResult<LLMReplyContent>> AskAnything([FromBody] FormatLLMInput input)
        {
            if (String.IsNullOrEmpty(input.context))
            {
                return new ContentResult
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Content = "Context is mandatory.",
                    ContentType = "text/plain"
                };
            }

            string initcontent = string.Empty;
            if (input.formattype == "math")
            {
                initcontent = "你是经验丰富的数学老师。";
            }
            else if(input.formattype == "physics")
            {
                initcontent = "你是经验丰富的物理老师。";
            }
            else if(input.formattype == "chemistry")
            {
                initcontent = "你是经验丰富的化学老师。";
            }
            List<LLMConversationMessage> tmpmsgs = new List<LLMConversationMessage>();
            tmpmsgs.Add(new LLMConversationMessage()
            {
                role = "system",
                content = initcontent
            });
            tmpmsgs.Add(new LLMConversationMessage()
            {
                role = "user",
                content = input.context
            });
            conv.messages = tmpmsgs.ToArray();
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
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

            return new ContentResult
            {
                StatusCode = (int)HttpStatusCode.BadRequest,
                Content = "No returns from LLM.",
                ContentType = "text/plain"
            };
        }
    }

    public class FormatLLMInput
    {
        public required string formattype { get; set; }
        public required string context { get; set; }
    }

}
