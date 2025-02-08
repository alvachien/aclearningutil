using aclearningutil.Util;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
var cacheMaxAgeOneWeek = (60 * 60 * 24 * 7).ToString();
//Console.WriteLine(builder.Environment.ContentRootPath);

app.UseCors(MyAllowSpecificOrigins);

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append(
             "Cache-Control", $"public, max-age={cacheMaxAgeOneWeek}");
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
