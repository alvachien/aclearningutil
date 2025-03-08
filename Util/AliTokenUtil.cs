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
            string stringToSign = string.Empty;
            try
            {
                StringBuilder strBuilderSign = new StringBuilder();
                strBuilderSign.Append(method);
                strBuilderSign.Append("&");
                strBuilderSign.Append(percentEncode(urlPath));
                strBuilderSign.Append("&");
                strBuilderSign.Append(percentEncode(queryString));
                stringToSign = strBuilderSign.ToString();
            }
            catch (Exception e)
            {
                //System.out.println("UTF-8 encoding is not supported.");
                //e.StackTrace();
            }
            return stringToSign;
        }

        private static string sign(string stringToSign, string accessKeySecret)
        {
            try
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(accessKeySecret);
                byte[] messageBytes = Encoding.UTF8.GetBytes(stringToSign);

                using (HMACSHA1 hmacSHA1 = new HMACSHA1(keyBytes))
                {
                    string encodedString = Convert.ToBase64String(hmacSHA1.ComputeHash(messageBytes));
                    return percentEncode(encodedString);
                }
            }
            catch (Exception e)
            {
            }

            return string.Empty;
        }

        private async Task<string> processGETRequest(string queryString)
        {
            /**
             * 设置HTTP GET请求
             * 1. 使用HTTP协议
             * 2. Token服务域名：nls-meta.cn-shanghai.aliyuncs.com
             * 3. 请求路径：/
             * 4. 设置请求参数
             */
            string url = "http://nls-meta.cn-shanghai.aliyuncs.com";
            url = url + "/";
            url = url + "?" + queryString;

            try
            {
                HttpClient client = new HttpClient();
                var response = client.GetAsync(url).Result;

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e)
            {
            }

            return "";
        }


        public async Task<AliToken> getToken(String accessKey, String accessSecret)
        {
            string queryString = canonicalizedQuery(accessKey);
            Console.WriteLine(queryString);

            string method = "GET";  // 发送请求的 HTTP 方法，GET
            string urlPath = "/";   // 请求路径
            string stringToSign = createStringToSign(method, urlPath, queryString);
            Console.WriteLine(stringToSign);

            string signature = sign(stringToSign, accessSecret + "&");
            Console.WriteLine(signature);

            string queryStringWithSign = "Signature=" + signature + "&" + queryString;
            Console.WriteLine(queryStringWithSign);

            var rst = await processGETRequest(queryStringWithSign);

            return JsonSerializer.Deserialize<AliToken>(rst);
            // Return result looks like: {"ErrMsg":"","Token":{"UserId":"31142045","Id":"22e838c137e044749c9df37248ed5e7d","ExpireTime":1738634286}}
            //return rst.ToString();
        }
    }
}
