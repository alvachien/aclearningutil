using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Events;
using System.Text.Json;
using System.Threading.RateLimiting;
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
                 path: Path.Combine(builder.Environment.ContentRootPath, "..", "Logs", "aclearningutil", "log-.txt"),
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
        options.IncludeErrorDetails = builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = "api.knowledgebuilder"
        };
    });
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// Rate limiting for LLM/TTS endpoints (prevents unbounded external API spend)
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("LLMAndTTS", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 5;
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
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

    // Ensure new nullable columns exist on pre-existing SQLite databases. EnsureCreated()
    // only creates a missing database; it does not add columns to an already-created DB,
    // so older databases need an explicit ALTER TABLE for the IncludeLatex/TranslationDisabled
    // columns added to LearningContent.
    static async Task EnsureColumnAsync(AppDbContext db, Microsoft.Extensions.Logging.ILogger logger, string tableName, string columnName, string columnType)
    {
#pragma warning disable EF1003 // Identifiers (table/column names) cannot be parameterized; all inputs here are hard-coded internal constants, not user input.
        var existingColumns = await db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('" + tableName + "')")
            .ToListAsync();

        if (!existingColumns.Contains(columnName))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + columnType + ";");
            logger.LogInformation("Added missing column {Column} to table {Table}.", columnName, tableName);
        }
#pragma warning restore EF1003
    }

    await EnsureColumnAsync(db, app.Logger, "LearningContents", "IncludeLatex", "INTEGER");
    await EnsureColumnAsync(db, app.Logger, "LearningContents", "TranslationDisabled", "INTEGER");

    // Seed LearningContentCategories (use INSERT OR IGNORE semantics for idempotent startup)
    var categories = new (int Id, string NameChinese, string NameEnglish)[]
    {
        (1, "词汇", "Vocabulary"),
        (2, "句子", "Sentences"),
        (3, "听力", "Listening"),
        (4, "中文", "Chinese"),
        (5, "公式", "Formula"),
        (6, "知识库", "Knowledge Bank"),
    };
    foreach (var (id, nameCn, nameEn) in categories)
    {
        var exists = await db.LearningContentCategories.AnyAsync(c => c.Id == id);
        if (!exists)
        {
            try
            {
                db.LearningContentCategories.Add(new LearningContentCategory
                {
                    Id = id,
                    NameChinese = nameCn,
                    NameEnglish = nameEn,
                });
                await db.SaveChangesAsync();
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Concurrent start — another instance already seeded this category. Safe to ignore.
                db.ChangeTracker.Clear();
            }
        }
    }

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
                    Log.Information("Migrated {Count} TTS mappings from JSON to database.", migratedCount);
                }
                else
                {
                    Log.Information("All TTS mappings already exist in database.");
                }
            }

            // Rename the JSON file to indicate migration is complete
            var backupFile = Path.Combine(audioFolder, "tts_map.json.migrated");
            File.Move(jsonFile, backupFile, overwrite: true);
            Log.Information("Backed up {SourceFile} to {BackupFile}.", jsonFile, backupFile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error migrating tts_map.json.");
        }
    }

    // Dynamically sync LearningContents from JSON index files
    // Supports different subfolders (e.g., "learnenglish" for vocabulary/sentences, "knowledge-exercises" for knowledge bank)
    static async Task SyncLearningContentsFromJsonAsync(AppDbContext db, Microsoft.Extensions.Logging.ILogger logger, string storageFolder, string subFolder, int categoryId, string jsonFileName)
    {
        var jsonFilePath = Path.Combine(storageFolder, subFolder, jsonFileName);
        if (!File.Exists(jsonFilePath))
        {
            logger.LogWarning("Index file not found: {JsonFilePath}, skipping sync for category {CategoryId}.", jsonFilePath, categoryId);
            return;
        }

        var jsonContent = await File.ReadAllTextAsync(jsonFilePath);
        var jsonEntries = JsonSerializer.Deserialize<JsonElement[]>(jsonContent,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (jsonEntries == null || jsonEntries.Length == 0)
        {
            logger.LogWarning("No entries found in {JsonFileName}, skipping sync.", jsonFileName);
            return;
        }

        // Build the set of FileUrls from JSON (check all known URL patterns for matching existing records)
        var jsonFileUrls = new HashSet<string>();
        foreach (var entry in jsonEntries)
        {
            try
            {
                if (!entry.TryGetProperty("file", out var fileProp) || fileProp.ValueKind != JsonValueKind.String)
                {
                    logger.LogWarning("Skipping entry with missing or invalid 'file' property in {JsonFileName}.", jsonFileName);
                    continue;
                }
                var file = fileProp.GetString()!;
                jsonFileUrls.Add($"storage/{subFolder}/{file}");
                jsonFileUrls.Add($"data/{subFolder}/{file}");
                jsonFileUrls.Add($"{subFolder}/{file}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error processing entry in {JsonFileName}.", jsonFileName);
            }
        }

        // Pre-load existing DB records matching any of the known FileUrl patterns
        var existingMatches = await db.LearningContents
            .Where(c => c.CategoryId == categoryId && jsonFileUrls.Contains(c.FileUrl))
            .ToListAsync();

        var addCount = 0;
        var updateCount = 0;
        foreach (var entry in jsonEntries)
        {
            try
            {
                // Accept either "name" (standard) or "book" (legacy, used by englishlistening)
                string? name = null;
                if (entry.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                {
                    name = nameProp.GetString();
                }
                else if (entry.TryGetProperty("book", out var bookProp) && bookProp.ValueKind == JsonValueKind.String)
                {
                    name = bookProp.GetString();
                }

                if (!entry.TryGetProperty("file", out var fileProp) || fileProp.ValueKind != JsonValueKind.String)
                {
                    logger.LogWarning("Skipping entry with missing or invalid 'file' property in {JsonFileName}.", jsonFileName);
                    continue;
                }
                var file = fileProp.GetString()!;

                if (string.IsNullOrEmpty(name))
                {
                    logger.LogWarning("Skipping entry with no 'name' or 'book' property in {JsonFileName}.", jsonFileName);
                    continue;
                }

                // Optional "version" field (byte, 0-255). Absent or invalid → null.
                // On update, a null here leaves the existing Version untouched (never clears a set value).
                byte? version = null;
                if (entry.TryGetProperty("version", out var versionProp) && versionProp.ValueKind == JsonValueKind.Number)
                {
                    if (versionProp.TryGetByte(out var versionByte))
                    {
                        version = versionByte;
                    }
                    else
                    {
                        logger.LogWarning("Entry '{Name}' in {JsonFileName} has a 'version' outside byte range (0-255); ignoring version.", name, jsonFileName);
                    }
                }

                // Optional "includeLatex" flag (knowledge-exercises data.json). Absent → null (untouched on update).
                bool? includeLatex = null;
                if (entry.TryGetProperty("includeLatex", out var includeLatexProp) && (includeLatexProp.ValueKind == JsonValueKind.True || includeLatexProp.ValueKind == JsonValueKind.False))
                {
                    includeLatex = includeLatexProp.GetBoolean();
                }

                // Optional "translationDisabled" flag (learnchinese data.json). Absent → null (untouched on update).
                bool? translationDisabled = null;
                if (entry.TryGetProperty("translationDisabled", out var translationDisabledProp) && (translationDisabledProp.ValueKind == JsonValueKind.True || translationDisabledProp.ValueKind == JsonValueKind.False))
                {
                    translationDisabled = translationDisabledProp.GetBoolean();
                }

                var primaryFileUrl = $"storage/{subFolder}/{file}";
                var legacyFileUrl1 = $"data/{subFolder}/{file}";
                var legacyFileUrl2 = $"{subFolder}/{file}";

                var existing = existingMatches.FirstOrDefault(c =>
                    c.FileUrl == primaryFileUrl || c.FileUrl == legacyFileUrl1 || c.FileUrl == legacyFileUrl2);

                if (existing != null)
                {
                    var changed = false;
                    // Migrate FileUrl to current pattern if needed
                    if (existing.FileUrl != primaryFileUrl)
                    {
                        existing.FileUrl = primaryFileUrl;
                        changed = true;
                    }
                    // Update name if changed
                    if (existing.NameChinese != name || existing.NameEnglish != name)
                    {
                        existing.NameChinese = name;
                        existing.NameEnglish = name;
                        changed = true;
                    }
                    // Update version only when the JSON entry declares one and it differs
                    if (version.HasValue && existing.Version != version)
                    {
                        existing.Version = version;
                        changed = true;
                    }
                    // Update includeLatex only when the JSON entry declares it and it differs
                    if (includeLatex.HasValue && existing.IncludeLatex != includeLatex)
                    {
                        existing.IncludeLatex = includeLatex;
                        changed = true;
                    }
                    // Update translationDisabled only when the JSON entry declares it and it differs
                    if (translationDisabled.HasValue && existing.TranslationDisabled != translationDisabled)
                    {
                        existing.TranslationDisabled = translationDisabled;
                        changed = true;
                    }
                    if (changed)
                    {
                        existing.UpdatedAt = DateTime.UtcNow;
                        updateCount++;
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
                        Version = version,
                        IncludeLatex = includeLatex,
                        TranslationDisabled = translationDisabled,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                    });
                    addCount++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error processing entry in {JsonFileName}.", jsonFileName);
            }
        }

        if (addCount > 0 || updateCount > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation("Synced category {CategoryId} from {JsonFileName}: {Added} added, {Updated} updated.", categoryId, jsonFileName, addCount, updateCount);
        }

        // Log DB records that exist in DB but not in JSON (instead of hard-deleting, to protect user data)
        var orphaned = existingMatches.Where(c => !jsonFileUrls.Contains(c.FileUrl)).ToList();
        if (orphaned.Count > 0)
        {
            logger.LogWarning(
                "Found {Count} content items for category {CategoryId} in DB but not in {JsonFileName}: {Titles}. " +
                "These are NOT deleted to protect user learning histories and ratings. " +
                "Remove them manually if needed.",
                orphaned.Count, categoryId, jsonFileName,
                string.Join(", ", orphaned.Select(c => c.NameEnglish)));
        }
    }

    // Sync Vocabulary from words.json (CategoryId = 1)
    var seedStorageFolder = Path.Combine(builder.Environment.ContentRootPath, "Storage");
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var syncLogger = loggerFactory.CreateLogger("SyncLearningContents");
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "learnenglish", 1, "words.json");

    // Sync Sentences from sentences.json (CategoryId = 2)
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "learnenglish", 2, "sentences.json");

    // Sync Knowledge Bank from data.json (CategoryId = 6)
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "knowledge-exercises", 6, "data.json");

    // Sync Chinese from data.json (CategoryId = 4)
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "learnchinese", 4, "data.json");

    // Sync Listening from data.json (CategoryId = 3)
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "englishlistening", 3, "data.json");

    // Sync Formula from formula.json (CategoryId = 5).
    // formula.json entries also carry a "contenttype" field (e.g. "math"/"physics"/"chemistry")
    // that has no corresponding DB column, so it is intentionally ignored by the sync logic.
    await SyncLearningContentsFromJsonAsync(db, syncLogger, seedStorageFolder, "formula", 5, "formula.json");
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

// Storage folder files are served via StorageController with route-based URL:
// GET /api/Storage/{subfolder}/{filename} (e.g., /api/Storage/knowledge-exercises/data.json)
// See Controllers/StorageController.cs

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();
