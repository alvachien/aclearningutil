using System.Net.Http.Headers;
using System.Text;

namespace aclearningutil.Utility
{
    public class LLMUtil
    {
        private static readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

        // Aliyun Model
        public const string qwenModelName = "qwen-plus";
        public const string qwenAPIUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

        // Deepseek model
        public const string deepseekModelName = "deepseek-chat";
        //private const string modelName = "deepseek-reasoner";
        public const string deepseekAPIUrl = "https://api.deepseek.com/v1/chat/completions";

        public static async Task<string> SendPostRequestAsync(string url, string jsonContent, string apiKey, CancellationToken cancellationToken = default)
        {
            using var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Build per-request message to avoid mutating shared DefaultRequestHeaders
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = content;

            // Sent and get reply
            HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);

            // Process reply
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return $"ERROR: {response.StatusCode}: {errorBody}";
            }
        }
    }
}
