using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace aclearningutil.Util
{
    public class AliTokenDetail
    {
        public AliTokenDetail()
        {
            UserId = string.Empty;
            Id = string.Empty;
            ExpireTime = 0;
        }

        public string UserId { get; set; }
        public string Id { get; set; }
        public int ExpireTime { get; set; }
    }

    public class AliToken
    {
        public AliToken()
        {
            ErrMsg = string.Empty;
            Token = new AliTokenDetail();
        }

        public string ErrMsg { get; set; }
        public AliTokenDetail Token { get; set; }
    }

    public class AliTokenUtil
    {
        private static readonly HttpClient SharedTokenHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        // Simple in-memory token cache
        private static AliToken? _cachedToken;
        private static DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
        private static readonly SemaphoreSlim _tokenLock = new(1, 1);
        private static string percentEncode(string value)
        {
            return WebUtility.UrlEncode(value)
                    .Replace("+", "%20")
                    .Replace("*", "%2A")
                    .Replace("%7E", "~");
        }

        private static string getISO8601Time(DateTime date)
        {
            return date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        }

        private static string getUniqueNonce()
        {
            return Guid.NewGuid().ToString();
        }

        private static string canonicalizedQuery(string accessKeyId)
        {
            // 所有请求参数
            DateTime date = DateTime.UtcNow;
            Dictionary<string, string> queryParamsMap = new()
            {
                { "AccessKeyId", accessKeyId },
                { "Action", "CreateToken" },
                { "Version", "2019-02-28" },
                { "Timestamp", getISO8601Time(date) },
                { "Format", "JSON" },
                { "RegionId", "cn-shanghai" },
                { "SignatureMethod", "HMAC-SHA1" },
                { "SignatureVersion", "1.0" },
                { "SignatureNonce", getUniqueNonce() }
            };

            // 对参数Key排序
            string[] sortedKeys = queryParamsMap.Keys.ToArray();
            Array.Sort(sortedKeys);

            // 对排序的参数进行编码、拼接
            StringBuilder canonicalizedQueryString = new StringBuilder();
            foreach (string key in sortedKeys)
            {
                canonicalizedQueryString.Append("&")
                                        .Append(percentEncode(key))
                                        .Append("=")
                                        .Append(percentEncode(queryParamsMap[key]));
            }
            return canonicalizedQueryString.ToString().Substring(1);
        }

        private static string createStringToSign(string method, string urlPath, string queryString)
        {
            StringBuilder strBuilderSign = new StringBuilder();
            strBuilderSign.Append(method);
            strBuilderSign.Append("&");
            strBuilderSign.Append(percentEncode(urlPath));
            strBuilderSign.Append("&");
            strBuilderSign.Append(percentEncode(queryString));
            return strBuilderSign.ToString();
        }

        private static string sign(string stringToSign, string accessKeySecret)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(accessKeySecret);
            byte[] messageBytes = Encoding.UTF8.GetBytes(stringToSign);

            using (HMACSHA1 hmacSHA1 = new HMACSHA1(keyBytes))
            {
                string encodedString = Convert.ToBase64String(hmacSHA1.ComputeHash(messageBytes));
                return percentEncode(encodedString);
            }
        }

        private static async Task<string> processGETRequest(string queryString)
        {
            /**
             * 设置HTTP GET请求
             * 1. 使用HTTPS协议
             * 2. Token服务域名：nls-meta.cn-shanghai.aliyuncs.com
             * 3. 请求路径：/
             * 4. 设置请求参数
             */
            string url = "https://nls-meta.cn-shanghai.aliyuncs.com";
            url = url + "/";
            url = url + "?" + queryString;

            var response = await SharedTokenHttpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }


        public async Task<AliToken> getToken(String accessKey, String accessSecret)
        {
            // Check cache first (thread-safe)
            await _tokenLock.WaitAsync();
            try
            {
                if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry)
                {
                    return _cachedToken;
                }
            }
            finally
            {
                _tokenLock.Release();
            }

            string queryString = canonicalizedQuery(accessKey);

            string method = "GET";  // 发送请求的 HTTP 方法，GET
            string urlPath = "/";   // 请求路径
            string stringToSign = createStringToSign(method, urlPath, queryString);

            string signature = sign(stringToSign, accessSecret + "&");

            string queryStringWithSign = "Signature=" + signature + "&" + queryString;

            var rst = await processGETRequest(queryStringWithSign);

            var token = JsonSerializer.Deserialize<AliToken>(rst) ?? new AliToken();

            // Cache token with expiry (expire 60s before actual expiry for safety)
            if (!string.IsNullOrEmpty(token.Token.Id) && token.Token.ExpireTime > 0)
            {
                await _tokenLock.WaitAsync();
                try
                {
                    _cachedToken = token;
                    _tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(token.Token.ExpireTime).AddSeconds(-60);
                }
                finally
                {
                    _tokenLock.Release();
                }
            }

            return token;
        }
    }
}
