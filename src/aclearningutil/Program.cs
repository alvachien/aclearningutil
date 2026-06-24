using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using System.Text.Json;
using aclearningutil.Data;
using aclearningutil.Data.Entities;
using aclearningutil.Models;

// Creation
var builder = WebApplication.CreateBuilder(args);
// Logs
if(builder.Environment.IsDevelopment())
{
    builder.Host.UseSerilog((context, config) =>
    {
        config.MinimumLevel.Is(LogEventLevel.Information)
             .Enrich.FromLogContext()
             .WriteTo.Console();
    });
}
else if(builder.Environment.IsProduction())
{
    builder.Host.UseSerilog((context, config) =>
    {
        var outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}{NewLine}{Exception}";

        config.MinimumLevel.Is(LogEventLevel.Warning)
             .Enrich.FromLogContext()
             .WriteTo.File(
                 path: "../Logs/aclearningutil/log-.txt",
                 rollingInterval: RollingInterval.Day, // �������
                 outputTemplate: outputTemplate,
                 retainedFileCountLimit: 14 // �������7����־
             );
    });
}
// CORS support
var allowedOrigins = new string[] { };
if (builder.Environment.IsDevelopment())
{
    allowedOrigins = new[] { "http://localhost:4200", "http://localhost:29800" };
}
else if (builder.Environment.IsProduction())
{
    allowedOrigins = new[] { "https://www.alvachien.com" };
}
var MyAllowSpecificOrigins = "MyAllowSpecificOrigins";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
                      policy =>
                      {
                          policy.WithOrigins(allowedOrigins)
                                .AllowAnyHeader()
                                .AllowAnyMethod()
                                .AllowCredentials();
                      });
});
// Controller
builder.Services.AddControllers();
// Authentication - JWT Bearer
builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        options.Authority = builder.Environment.IsDevelopment()
            ? "https://localhost:7228"
            : "https://www.alvachien.com/idserver";
        options.RequireHttpsMetadata = true;
        options.SaveToken = true;
        options.IncludeErrorDetails = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false
        };
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// Database - SQLite
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "aclearningutil.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Seed LearningContentCategories
    var categories = new (int Id, string NameChinese, string NameEnglish)[]
    {
        (1, "词汇", "Vocabulary"),
        (2, "句子", "Sentence"),
        (6, "知识库", "Knowledge Bank"),
    };
    foreach (var (id, nameCn, nameEn) in categories)
    {
        if (!await db.LearningContentCategories.AnyAsync(c => c.Id == id))
        {
            db.LearningContentCategories.Add(new LearningContentCategory
            {
                Id = id,
                NameChinese = nameCn,
                NameEnglish = nameEn,
            });
        }
    }
    await db.SaveChangesAsync();

    // Migrate data from tts_map.json if it exists
    var audioFolder = Path.Combine(builder.Environment.ContentRootPath, "AudioFiles");
    var jsonFile = Path.Combine(audioFolder, "tts_map.json");

    if (File.Exists(jsonFile))
    {
        try
        {
            var jsonContent = await File.ReadAllTextAsync(jsonFile);
            var mappings = JsonSerializer.Deserialize<List<SentenceJsonMap>>(jsonContent);

            if (mappings != null && mappings.Count > 0)
            {
                var migratedCount = 0;
                foreach (var mapping in mappings)
                {
                    // Check if already exists in database
                    if (!await db.TtsMappings.AnyAsync(m => m.Sentence == mapping.Sentence))
                    {
                        db.TtsMappings.Add(new TtsMapping
                        {
                            Sentence = mapping.Sentence,
                            FileName = mapping.FileName,
                            CreatedAt = DateTime.UtcNow
                        });
                        migratedCount++;
                    }
                }

                if (migratedCount > 0)
                {
                    await db.SaveChangesAsync();
                    Console.WriteLine($"Migrated {migratedCount} TTS mappings from JSON to database.");
                }
                else
                {
                    Console.WriteLine("All TTS mappings already exist in database.");
                }
            }

            // Rename the JSON file to indicate migration is complete
            var backupFile = Path.Combine(audioFolder, "tts_map.json.migrated");
            File.Move(jsonFile, backupFile, overwrite: true);
            Console.WriteLine($"Backed up {jsonFile} to {backupFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error migrating tts_map.json: {ex.Message}");
        }
    }

    // Dynamically sync LearningContents from JSON index files
    // Supports different subfolders (e.g., "learnenglish" for vocabulary/sentences, "knowledge-exercises" for knowledge bank)
    static async Task SyncLearningContentsFromJsonAsync(AppDbContext db, string storageFolder, string subFolder, int categoryId, string jsonFileName)
    {
        var jsonFilePath = Path.Combine(storageFolder, subFolder, jsonFileName);
        if (!File.Exists(jsonFilePath))
        {
            Console.WriteLine($"Index file not found: {jsonFilePath}, skipping sync for category {categoryId}.");
            return;
        }

        var jsonContent = await File.ReadAllTextAsync(jsonFilePath);
        var jsonEntries = JsonSerializer.Deserialize<JsonElement[]>(jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (jsonEntries == null || jsonEntries.Length == 0)
        {
            Console.WriteLine($"No entries found in {jsonFileName}, skipping sync.");
            return;
        }

        // Build the set of FileUrls from JSON (check all known URL patterns for matching existing records)
        var jsonFileUrls = new HashSet<string>();
        foreach (var entry in jsonEntries)
        {
            var file = entry.GetProperty("file").GetString()!;
            jsonFileUrls.Add($"storage/{subFolder}/{file}");
            jsonFileUrls.Add($"data/{subFolder}/{file}");
            jsonFileUrls.Add($"{subFolder}/{file}");
        }

        // Pre-load existing DB records matching any of the known FileUrl patterns
        var existingMatches = await db.LearningContents
            .Where(c => c.CategoryId == categoryId && jsonFileUrls.Contains(c.FileUrl))
            .ToListAsync();

        var addCount = 0;
        foreach (var entry in jsonEntries)
        {
            var name = entry.GetProperty("name").GetString()!;
            var file = entry.GetProperty("file").GetString()!;
            var primaryFileUrl = $"storage/{subFolder}/{file}";
            var legacyFileUrl1 = $"data/{subFolder}/{file}";
            var legacyFileUrl2 = $"{subFolder}/{file}";

            var existing = existingMatches.FirstOrDefault(c =>
                c.FileUrl == primaryFileUrl || c.FileUrl == legacyFileUrl1 || c.FileUrl == legacyFileUrl2);

            if (existing != null)
            {
                // Migrate FileUrl to current pattern if needed
                if (existing.FileUrl != primaryFileUrl)
                {
                    existing.FileUrl = primaryFileUrl;
                }
                // Update name if changed
                if (existing.NameChinese != name || existing.NameEnglish != name)
                {
                    existing.NameChinese = name;
                    existing.NameEnglish = name;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                db.LearningContents.Add(new LearningContent
                {
                    CategoryId = categoryId,
                    NameChinese = name,
                    NameEnglish = name,
                    FileUrl = primaryFileUrl,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                addCount++;
            }
        }

        if (addCount > 0)
        {
            await db.SaveChangesAsync();
            Console.WriteLine($"Added {addCount} content items for category {categoryId} from {jsonFileName}.");
        }

        // Delete DB records not present in JSON (also clean up dependent learning histories and ratings)
        var toDelete = existingMatches.Where(c => !jsonFileUrls.Contains(c.FileUrl)).ToList();
        if (toDelete.Count > 0)
        {
            foreach (var content in toDelete)
            {
                var histories = await db.UserLearningHistories.Where(h => h.ContentId == content.Id).ToListAsync();
                if (histories.Count > 0) db.UserLearningHistories.RemoveRange(histories);

                var ratings = await db.UserLearningRatings.Where(r => r.ContentId == content.Id).ToListAsync();
                if (ratings.Count > 0) db.UserLearningRatings.RemoveRange(ratings);

                db.LearningContents.Remove(content);
            }
            await db.SaveChangesAsync();
            Console.WriteLine($"Removed {toDelete.Count} content items for category {categoryId} no longer in {jsonFileName}.");
        }
    }

    // Sync Vocabulary from words.json (CategoryId = 1)
    var seedStorageFolder = Path.Combine(builder.Environment.ContentRootPath, "Storage");
    await SyncLearningContentsFromJsonAsync(db, seedStorageFolder, "learnenglish", 1, "words.json");

    // Sync Sentences from sentences.json (CategoryId = 2)
    await SyncLearningContentsFromJsonAsync(db, seedStorageFolder, "learnenglish", 2, "sentences.json");

    // Sync Knowledge Bank from data.json (CategoryId = 6)
    await SyncLearningContentsFromJsonAsync(db, seedStorageFolder, "knowledge-exercises", 6, "data.json");
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => // UseSwaggerUI is called only in Development.
    {
        //options.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
        //options.RoutePrefix = string.Empty;
    });
}

app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
var cacheMaxAgeOneWeek = (60 * 60 * 24 * 7).ToString();

app.UseCors(MyAllowSpecificOrigins);

var sharedfolder = Path.Combine(builder.Environment.ContentRootPath, "AudioFiles");
if (!Directory.Exists(sharedfolder))
{
    Directory.CreateDirectory(sharedfolder);
}
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", $"public, max-age={cacheMaxAgeOneWeek}");
    },
    FileProvider = new PhysicalFileProvider(sharedfolder),
    RequestPath = "/audio"
});

// Note: Storage folder files are now served via StorageController with authentication
// See Controllers/StorageController.cs

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
