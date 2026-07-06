# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**aclearningutil** is a learning utility API built with ASP.NET Core 10.0. It provides:
- Text-to-Speech (TTS) services using Aliyun TTS API
- LLM integration for educational Q&A (DeepSeek API)
- Audio caching with SQLite database for efficient retrieval

The service is part of the broader H.I.H. (Home Information Hub) learning ecosystem.

## Architecture

### Technology Stack
- **Framework**: ASP.NET Core 10.0 (net10.0)
- **Database**: SQLite via Entity Framework Core 10.0
- **Authentication**: JWT Bearer tokens from Identity Server
- **Logging**: Serilog (console in dev, file in production)
- **API Docs**: Swagger/OpenAPI

### Key Components

**Controllers** (`Controllers/`)
- `TTSController.cs`: Text-to-Speech API with database-backed caching (`[Authorize]`)
- `FormatLLMController.cs`: Subject-specific LLM Q&A (math, physics, chemistry) (`[Authorize]`)
- `EnglishLLMController.cs`: English language learning assistant (`[Authorize]`)
- `LearningContentCategoriesController.cs`: Read-only listing of learning content categories (public, no auth required)
- `LearningContentsController.cs`: CRUD for learning content items (`[Authorize]`)
- `UserLearningHistoriesController.cs`: CRUD for user learning history (`[Authorize]`, user-scoped via JWT claims, supports `?contentId=&itemId=` search)
- `UserLearningRatingsController.cs`: CRUD for user content ratings (`[Authorize]`, user-scoped via JWT claims, rating 1-5, supports `?contentId=&itemId=` search)

**Data Layer** (`Data/`)
- `AppDbContext.cs`: EF Core DbContext for SQLite
- `Entities/TtsMapping.cs`: Entity for TTS sentence-to-audio mappings
- `Entities/LearningContentCategory.cs`: System-owned content categories (6 seeded defaults)
- `Entities/LearningContent.cs`: Learning content items (FK to category; optional `Version`, `IncludeLatex`, `TranslationDisabled` fields)
- `Entities/UserLearningHistory.cs`: User learning history (FK to content, user-scoped)
- `Entities/UserLearningRating.cs`: User content ratings (FK to content, user-scoped, rating byte 1-5)
- Database schema includes unique index on `Sentence` for fast lookups

**Utilities** (`src/aclearningutil/Utility/`)
- `LLMUtil.cs`: HTTP client for DeepSeek API calls (thread-safe implementation)
- `AliTokenUtil.cs`: Aliyun TTS token generation and API calls

**Models** (`Models/`)
- DTOs for API requests/responses
- `SentenceJsonMap.cs`: Legacy model kept for JSON-to-database migration

## Database

The SQLite database — connection configuration, the full table schema (with indexes and foreign keys), the `EnsureCreated` / `EnsureColumnAsync` schema-evolution strategy, and instructions for inspecting it — is documented in [`docs/design-database.md`](docs/design-database.md).

## Development Guidelines

### Async/Await Pattern
Always use async/await for I/O operations. Never use `.Result` or `.Wait()` which can cause deadlocks.

```csharp
// ✅ Correct
var response = await client.GetAsync(url);
var content = await response.Content.ReadAsStringAsync();

// ❌ Wrong (blocking)
var response = client.GetAsync(url).Result;
```

### Resource Disposal
Always dispose of HTTP clients and file streams properly:

```csharp
// ✅ Correct - using declarations
using HttpClient client = new();
await using FileStream fs = new(path, FileMode.Create);

// ❌ Wrong - manual disposal or no disposal
HttpClient client = new();
```

### HTTP Client Usage
For shared/static HTTP clients, use per-request headers to avoid thread-safety issues:

```csharp
// ✅ Correct - per-request message
using var request = new HttpRequestMessage(HttpMethod.Post, url);
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
var response = await httpClient.SendAsync(request);

// ❌ Wrong - mutating shared client
httpClient.DefaultRequestHeaders.Authorization = ...;
```

### Null Checking
The project uses nullable reference types. Always check for null when appropriate:

```csharp
// ✅ Correct - null-conditional operators
var result = data?.Property ?? defaultValue;

// ✅ Correct - null checks
if (mapping != null) { ... }
```

## Common Tasks

### Adding a New API Endpoint

1. Create or modify controller in `src/aclearningutil/Controllers/`
2. Add route attributes: `[Route("api/[controller]")]`, `[HttpGet]` or `[HttpPost]`
3. Inject dependencies via constructor (e.g., `AppDbContext`, `IConfiguration`)
4. Use async/await for all I/O operations
5. Return appropriate `ActionResult<T>` types

### Adding a New Database Entity

1. Create entity class in `src/aclearningutil/Data/Entities/`
2. Add `DbSet<T>` property to `src/aclearningutil/Data/AppDbContext.cs`
3. Configure entity in `OnModelCreating()` if needed (indexes, constraints)
4. Database will be auto-created on next run (for new deployments)

### Modifying Database Schema

See [`docs/design-database.md`](docs/design-database.md#modifying-the-schema) for the schema-evolution procedure: `EnsureCreated` / `EnsureColumnAsync` for adding nullable columns to existing databases, and the standalone `src/Util/add_learningcontent_columns.py` helper for patching a deployed database outside the app.

### Testing the TTS Flow

1. Start the application: `dotnet run --project src/aclearningutil/aclearningutil.csproj`
2. Call TTS endpoint with a test sentence
3. Check that:
   - Audio file is created in `src/aclearningutil/AudioFiles/`
   - Record is inserted into `TtsMappings` table
   - Subsequent calls for same sentence use cached result

### Debugging Database Issues

Query the SQLite database directly:
```bash
sqlite3 aclearningutil.db
> SELECT * FROM TtsMappings;
> .schema TtsMappings
```

## Configuration

### API Keys
Store sensitive keys in User Secrets (development) or environment variables (production):

```bash
dotnet user-secrets set "Aliyun:TTSAPIKey" "your-key"
dotnet user-secrets set "DeepSeek:APIKey" "your-key"
```

### CORS
Configured in `Program.cs`. Currently allows:
- Development: `http://localhost:4200`, `http://localhost:29800`
- Production: `https://www.alvachien.com`

## External Dependencies

### Aliyun TTS API
- Endpoint: `http://nls-gateway.aliyuncs.com/stream/v1/tts`
- Requires: API Key, Access Key, Access Secret, and temporary token
- Token generation: `AliTokenUtil.getToken()`

### DeepSeek API
- Endpoint: `https://api.deepseek.com/v1/chat/completions`
- Model: `deepseek-chat`
- Requires: API Key in configuration

### Identity Server
- Development: `https://localhost:7228`
- Production: `https://www.alvachien.com/idserver`
- Validates JWT tokens for all endpoints except `LearningContentCategoriesController` (public, no auth)
- Audience: `api.knowledgebuilder` (validated in `Program.cs`)
- UserId for user-scoped controllers is extracted from JWT via `ClaimTypes.NameIdentifier` (with `"sub"` fallback)

## Solution Structure

```
aclearningutil.sln
├── src/
│   ├── aclearningutil/                    # Main ASP.NET Core Web API
│   │   ├── Program.cs                     # Entry point (minimal hosting model)
│   │   ├── Controllers/                   # API controllers
│   │   ├── Data/                          # EF Core DbContext and entities
│   │   ├── Models/                        # DTOs and domain models
│   │   ├── Utility/                       # Utility classes (LLM, Aliyun token)
│   │   ├── AudioFiles/                    # Generated audio files (gitignored)
│   │   └── Properties/                    # launchSettings.json, publish profiles
│   └── Util/                              # Standalone helpers (Python DB patch, Node schema validator)
├── test/
│   ├── aclearningutil.test/               # Unit tests (xUnit + Moq)
│   └── aclearningutil.test.common/        # Shared test utilities
└── CLAUDE.md
```

## Build and Deployment

### Build
```bash
dotnet build aclearningutil.sln
```

### Build only the main project
```bash
dotnet build src/aclearningutil/aclearningutil.csproj
```

### Run Locally
```bash
dotnet run --project src/aclearningutil/aclearningutil.csproj
```

### Run all tests
```bash
dotnet test
```

### Run unit tests only
```bash
dotnet test --filter DisplayName~aclearningutil.test
```

### Publish
```bash
dotnet publish src/aclearningutil/aclearningutil.csproj -c Release -o ./publish
```

### CI

GitHub Actions (`.github/workflows/build-test.yml`) restores, builds (`Release`), and tests the solution on every push and on pull requests targeting `main`.

### Production Considerations
- Logs written to `../Logs/aclearningutil/` (daily rolling, 14-day retention)
- Ensure write permissions for database file and AudioFiles directory
- Configure production API keys via environment variables
- HTTPS redirection is enabled

## Code Quality Standards

This project maintains:
- ✅ Zero compiler warnings
- ✅ No blocking async calls
- ✅ Proper resource disposal
- ✅ Thread-safe HTTP client usage
- ✅ Comprehensive null checking
- ✅ Async file I/O operations

When making changes, maintain these standards. Run `dotnet build` to verify zero warnings.

## Troubleshooting

### Database Lock Issues
If you encounter database locking errors:
- Ensure only one instance of the application is running
- Check that the database file isn't opened by another process
- Consider adding connection retry logic for production

### TTS API Failures
Common issues:
- Invalid or expired Aliyun credentials
- Network connectivity to Aliyun endpoints
- Insufficient API quota

Check logs for detailed error messages.

### Authentication Failures
- Verify Identity Server is running and accessible
- Check that JWT tokens are not expired
- Ensure token audience validation is configured correctly

## Related Projects

This project is part of the H.I.H. learning ecosystem:
- **acidserver**: Identity Server (OIDC provider)
- **achihapi**: Main OData API
- **alvachien.com**: Frontend application

All projects authenticate via the same Identity Server instance.
