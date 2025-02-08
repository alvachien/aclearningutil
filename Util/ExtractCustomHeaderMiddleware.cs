using Microsoft.Extensions.Primitives;
using System.Text;

namespace aclearningutil.Util
{
    public class ExtractCustomHeaderMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IConfiguration _conf;
        private static readonly Dictionary<string, string> dictCodes = new Dictionary<string, string>();

        public ExtractCustomHeaderMiddleware(RequestDelegate next, IConfiguration conf)
        {
            _next = next; 
            _conf = conf;

            var allowedCodes = _conf["AllowedUserCodes"];
            if (allowedCodes != null)
            {
                var allcodes = allowedCodes.Split(";");
                foreach (var code in allcodes)
                {
                    var codecode = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
                    Console.WriteLine($"{code}: {codecode}");
                    dictCodes[codecode] = code;
                }
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            const string HeaderKeyName = "ACAPIKey";
            context.Request.Headers.TryGetValue(HeaderKeyName, out StringValues headerValue);

            if (String.IsNullOrEmpty(headerValue))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync($"Missing required header: {HeaderKeyName}");
                return;
            }
            else
            {
                // Verify the value
                if (!dictCodes.ContainsKey(headerValue))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync($"Wrong user code: {headerValue}");
                    return;
                }
            }

            await _next(context);
        }
    }
}
