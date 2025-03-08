using System.Net.Http.Headers;
using System.Text;

namespace aclearningutil.Util
{
    public class LLMUtil
    {
        private static HttpClient httpClient = new HttpClient();

        // Aliyun Model
        public const string qwenModelName = "qwen-plus";
        public const string qwenAPIUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

        // Deepseek model
        public const string deepseekModelName = "deepseek-chat";
        //private const string modelName = "deepseek-reasoner";
        public const string deepseekAPIUrl = "https://api.deepseek.com/v1/chat/completions";

        public static async Task<string> SendPostRequestAsync(string url, string jsonContent, string apiKey)
        {
            using (var content = new StringContent(jsonContent, Encoding.UTF8, "application/json"))
            {
                // Header
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // Sent and get reply
                HttpResponseMessage response = await httpClient.PostAsync(url, content);

                // Process reply
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
