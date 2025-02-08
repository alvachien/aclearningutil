using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;

namespace aclearningutil.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class EnglishLLMController : ControllerBase
    {
        // Aliyun Model
        //private const string modelName = "qwen-plus";
        //private const string url = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

        // Deepseek model
        private const string modelName = "deepseek-chat";
        //private const string modelName = "deepseek-reasoner";
        private const string url = "https://api.deepseek.com/v1/chat/completions";

        public class ReplyContent
        {
            public string Content { get; set; }
        }

        public class LLMConversationMessage
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        public class LLMConversation
        {
            public string model { get; set; }
            public LLMConversationMessage[] messages { get; set; }
        }
        private LLMConversation conv = new LLMConversation();
        private List<LLMConversationMessage> listMessages = new List<LLMConversationMessage>();
        private static HttpClient httpClient = new HttpClient();
        private readonly IConfiguration Configuration;

        public EnglishLLMController(IConfiguration configuration) {
            Configuration = configuration;

            conv.model = modelName;
            listMessages.Add(new LLMConversationMessage()
                {
                    role = "system",
                    content =  "You are a English teacher for Junior and Senior High School students."
                }
            );
        }

        [HttpGet("details")]
        public async Task<ActionResult<ReplyContent>> GetLLMReply(string context)
        {
            if (String.IsNullOrEmpty(context)) {
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
            conv.messages = tmpmsgs.ToArray();
            var jsonContent = JsonSerializer.Serialize(conv);

            // 发送请求并获取响应
            var apiKey = Configuration["DeepSeek:APIKey"];
            string result = await SendPostRequestAsync(url, jsonContent, apiKey);
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
                return new ReplyContent()
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

        private static async Task<string> SendPostRequestAsync(string url, string jsonContent, string apiKey)
        {
            using (var content = new StringContent(jsonContent, Encoding.UTF8, "application/json"))
            {
                // 设置请求头
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // 发送请求并获取响应
                HttpResponseMessage response = await httpClient.PostAsync(url, content);

                // 处理响应
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }
                else
                {
                    return $"ERROR: {response.StatusCode}";
                }
            }
        }
    }
}


