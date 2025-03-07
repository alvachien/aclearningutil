using aclearningutil.Util;
using Microsoft.Extensions.FileProviders;
using Serilog;
using Serilog.Events;

// Creation
var builder = WebApplication.CreateBuilder(args);
// Logs
builder.Host.UseSerilog((context, config) =>
{
    var environment = context.HostingEnvironment;
    var outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}";

    config.MinimumLevel.Is(environment.IsDevelopment() ? LogEventLevel.Information : LogEventLevel.Warning)
         .Enrich.FromLogContext()
         .WriteTo.File(
             path: "../Logs/aclearningutil/log-.txt",
             rollingInterval: RollingInterval.Day, // 按天滚动
             outputTemplate: outputTemplate,
             retainedFileCountLimit: 14 // 保留最近7天日志
         );
});
// CORS support
var allowOrigin = "";
if (builder.Environment.IsDevelopment())
{
    allowOrigin = "http://localhost:4200";
}
else if (builder.Environment.IsProduction())
{
    allowOrigin = "https://www.alvachien.com/learning/*";
}
var MyAllowSpecificOrigins = "MyAllowSpecificOrigins";
builder.Services.AddCors(options =>
{    
    options.AddPolicy(name: MyAllowSpecificOrigins,
                      policy =>
                      {
                          policy.WithOrigins(allowOrigin)
                                .AllowAnyHeader()
                                .AllowAnyMethod();
                      });
});
// Controller
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => // UseSwaggerUI is called only in Development.
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
        options.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();
var cacheMaxAgeOneWeek = (60 * 60 * 24 * 7).ToString();

app.UseCors(MyAllowSpecificOrigins);
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", $"public, max-age={cacheMaxAgeOneWeek}");
    },
    FileProvider = new PhysicalFileProvider(Path.Combine(builder.Environment.ContentRootPath, "AudioFiles")),
    RequestPath = "/audio"
});

app.UseAuthorization();

app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), appBuilder =>
{
    appBuilder.UseMiddleware<ExtractCustomHeaderMiddleware>();
});

app.MapControllers();

app.Run();
