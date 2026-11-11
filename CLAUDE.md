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
- `LearningContentsController.cs`: CRUD for learning content items (`[Authorize]`; `POST`/`PUT`/`DELETE` additionally require the `RequireAdmin` policy — see Authorization)
- `HabitsController.cs` + `HabitItems/HabitCriteria/HabitPunches/ItemProperties` controllers: habit-tracking API, strictly owner-scoped; `Habits` also hosts the owner-only `{id}/Shares` invitation CRUD (`GET`/`POST`/`DELETE`, `[Authorize]`, see `docs/design-habit-api.md`)
- `SharedHabitsController.cs`: READ-ONLY "shared with me" surface (`GET /api/SharedHabits[/{id}][/History]`) — visible only to users with a `HabitShareGrant` (or the owner); the deliberate FR-6 exception; non-invited/unknown ids 404; no write endpoints
- `UserLearningRatingsController.cs`: CRUD for user content ratings (`[Authorize]`, user-scoped via JWT claims, rating 1-5, supports `?contentId=&itemId=` search)
- `UserLoginHistoriesController.cs`: per-day login history (`[Authorize]`, user-scoped; `POST` upserts today's row — called by the SPA on completed OIDC sign-in, per-user rate-limited via the `UserWrites` policy, with a 5-minute dedup window so duplicate reports within it leave the row untouched (`loginCount` = distinct sign-ins/day); `GET ?from=&to=` lists the caller's days, default trailing 90; no PUT/DELETE)
- `UserLearningTimesController.cs`: per-day-per-category learning time (`[Authorize]`, user-scoped; `POST { categoryId }` accepts the SPA heartbeat and credits only the server-measured seconds since the day's SHARED cursor (the latest accepted beat across all categories of that user/day), capped at 75/beat with a once-per-day 60s bootstrap — durations are unforgeable and the day total cannot exceed real elapsed time even for a six-category beat loop; `POST` + `PUT Preferences` carry `[EnableRateLimiting("UserWrites")]` (10/min/user) and a 1 KB body cap; `GET ?from=&to=&categoryId=` lists the caller's rows, default trailing 90; no PUT/DELETE)
- `WordAudioController.cs`: per-word pronunciation audio (`GET /api/WordAudio?word=`, `[Authorize]`, no rate limit) — resolves the word in read-only `Storage/words.db` and streams the MP3 from `Storage/wordaudio/`
- `WordDictionaryController.cs`: per-word dictionary detail (`GET /api/WordDictionary?word=`, `[Authorize]`, no rate limit) — Chinese definitions + bilingual example sentences (capped at 10) from read-only `Storage/words.db`; 404 for unknown words

For the full controller reference (every route, parameter, validation rule, and response shape), see [`docs/design-controllers.md`](docs/design-controllers.md). It covers all controllers including `StorageController` (`GET /api/Storage/{subfolder}/{filename}`), which serves learning-content files and the per-word `learnenglish/word_references/<word>.json` reference files.

**Data Layer** (`Data/`)
- `AppDbContext.cs`: EF Core DbContext for SQLite
- `Entities/TtsMapping.cs`: Entity for TTS sentence-to-audio mappings
- `Entities/LearningContentCategory.cs`: System-owned content categories (6 seeded defaults)
- `Entities/LearningContent.cs`: Learning content items (FK to category; optional `Version`, `IncludeLatex`, `TranslationDisabled` fields)
- `Entities/UserLearningRating.cs`: User content ratings (FK to content, user-scoped, rating byte 1-5)
- `Entities/UserLoginHistory.cs`: Per-day login history (one row per user per server-local day, unique `(UserId, LoginDate)`; first/last UTC instants + count of deduplicated sign-ins, the second report within 5 minutes counting as the first one)
- `Entities/UserLearningTime.cs`: Accumulated learning time (one row per user/day/category, unique `(UserId, LearnDate, CategoryId)`; server-credited `LearningSeconds`; each row's `LastBeatAt` UTC instant feeds the shared per-day cursor that bounds every credit)
- `Entities/LeaderboardPreference.cs`: Per-user leaderboard opt-out (one row per user; absence = visible)
- `Entities/Habit.cs`, `HabitItem.cs`, `Criterion.cs`, `Punch.cs`, `HabitShareGrant.cs`, `HabitEnums.cs`: habit-tracking model (owner-scoped habits, items with typed properties, criteria tree, per-day punches, invitation grants, cycle/property/criterion enums) — see `docs/design-habit-api.md`
- Database schema includes unique index on `Sentence` for fast lookups

**Utilities** (`src/aclearningutil/Utility/`)
- `LLMUtil.cs`: HTTP client for DeepSeek API calls (thread-safe implementation)
- `AliTokenUtil.cs`: Aliyun TTS token generation and API calls

**Rate limiting** (`src/aclearningutil/RateLimiting/`)
- `UserWriteRateLimiting.cs`: the `UserWrites` policy — a per-user fixed-window limiter (10/min, partitioned on `ClaimTypes.NameIdentifier` / `"sub"`) for the statistics write endpoints whose upserts take SQLite's global write lock, plus the 1 KB body cap constant those endpoints declare. Registered in `Program.cs`; see `docs/design-controllers.md` Conventions -> Rate limiting.

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

### Admin allowlist
`appsettings.json` -> `Admin:UserIds` (array of strings) lists the user-ID claims permitted to mutate shared
`LearningContents`. Policy `RequireAdmin` (`Program.cs`) + `Authorization/AdminUserAuthorizationHandler`;
empty array = fail closed. This is local authorization only - no token or CORS change, so other
projects need no matching config. See [`docs/design-controllers.md`](docs/design-controllers.md)
(Conventions -> Admin allowlist).

## External Dependencies

### Aliyun TTS API
- Endpoint: `https://nls-gateway.aliyuncs.com/stream/v1/tts`
- Requires: API Key, Access Key, Access Secret, and temporary token
- Token generation: `AliTokenUtil.getToken()`

### DeepSeek API
- Endpoint: `https://api.deepseek.com/v1/chat/completions`
- Model: `deepseek-chat`
- Requires: API Key in configuration

### Identity Server
- Development: `https://localhost:7228` (Docker-hosted; not managed by the workspace start/stop scripts)
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
│   │   ├── RateLimiting/                  # UserWrites per-user policy for the stats writes
│   │   ├── Utility/                       # Utility classes (LLM, Aliyun token)
│   │   ├── AudioFiles/                    # Generated audio files (gitignored)
│   │   └── Properties/                    # launchSettings.json, publish profiles
│   └── Util/                              # Standalone helpers (Python DB patch, Node schema validator)
├── test/
│   ├── aclearningutil.test/               # Unit tests (xUnit + FluentAssertions + Moq)
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

This project aims for (a maintained standard, not yet enforced by CI - `TreatWarningsAsErrors` is not set):
- Zero compiler warnings (one analyzer rule, `EF1003`, is intentionally suppressed for the hard-coded internal SQL in `Program.cs`)
- No blocking async calls (no `.Result` / `.Wait()`)
- Proper resource disposal (`HttpRequestMessage` / `FileStream` via `using`; a few `HttpResponseMessage` instances are not yet disposed - known minor gap)
- Thread-safe HTTP client usage (per-request `HttpRequestMessage`, no shared `DefaultRequestHeaders` mutation)
- Comprehensive null checking (nullable reference types enabled)
- Async file I/O operations

When making changes, maintain these standards. Run `dotnet build` to verify warnings.

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

### `403 Forbidden` on content writes
`LearningContents` `POST`/`PUT`/`DELETE` require the `RequireAdmin` policy; `GET`s do not.
- A valid token with no admin membership yields `403` — this is authorization, not authentication, so the Identity Server being up is not the issue.
- Check `Admin:UserIds` in `appsettings.json` (or the environment/secret override in production). An empty or missing array fails closed: no one is admin.
- The comparison uses the token's `ClaimTypes.NameIdentifier` (`"sub"` fallback), so the allowlisted value must be the acidserver `AspNetUsers.Id` of the operating user.

## Related Projects

This project is part of the H.I.H. learning ecosystem:
- **acidserver**: Identity Server (OIDC provider)
- **achihapi**: Main OData API
- **alvachien.com**: Frontend application

All projects authenticate via the same Identity Server instance.
