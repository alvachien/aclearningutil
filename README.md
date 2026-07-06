# ACLearningUtil

A learning utility API built with ASP.NET Core 10.0, providing Text-to-Speech (TTS) services, LLM integration for educational purposes, and a learning content management system with user history and rating tracking.

## Technology Stack

- **Framework**: ASP.NET Core 10.0
- **Database**: SQLite (via Entity Framework Core 10.0)
- **Authentication**: JWT Bearer tokens (via Identity Server)
- **Logging**: Serilog
- **API Documentation**: Swagger/OpenAPI (Swashbuckle)

## Features

### Text-to-Speech (TTS)
- Converts text to speech using Aliyun TTS API
- Caches generated audio files to avoid redundant API calls
- SQLite database tracks sentence-to-audio mappings
- Automatic migration from legacy JSON storage

### LLM Integration
- DeepSeek API integration for educational Q&A
- Subject-specific prompts (Math, Physics, Chemistry)
- English language learning assistant

### Learning Content Management
- System-owned content categories (6 seeded defaults)
- Multi-language content support (Chinese + English)
- User learning history tracking
- User content rating (1-5 scale)

## Database

The application uses SQLite for persistent storage (`aclearningutil.db`, auto-created in the app root on first run). The schema, connection configuration, schema-evolution strategy, and first-run JSON migration are documented in [`docs/design-database.md`](docs/design-database.md).

## Configuration

### appsettings.json

```json
{
  "Aliyun": {
    "TTSAPIKey": "your-tts-api-key",
    "TTSAccessKey": "your-access-key",
    "TTSAccessSecret": "your-access-secret"
  },
  "DeepSeek": {
    "APIKey": "your-deepseek-api-key"
  }
}
```

### User Secrets (Development)

For sensitive configuration during development:

```bash
dotnet user-secrets init
dotnet user-secrets set "Aliyun:TTSAPIKey" "your-key"
dotnet user-secrets set "DeepSeek:APIKey" "your-key"
```

## API Endpoints

### TTS Controller

#### GET /api/tts/details

Converts text to speech and returns the audio file URL.

**Query Parameters:**
- `sentence` (string, required): The text to convert to speech

**Response:**
```json
{
  "audioFileUrl": "audio/unique-filename.wav"
}
```

**Behavior:**
- If the sentence has been converted before, returns cached audio URL
- If new, calls Aliyun TTS API, saves audio file, and updates database
- Audio files are stored in `AudioFiles/` directory

### LLM Controllers

#### POST /api/formatllm/askanything

Ask educational questions with subject-specific formatting.

**Request Body:**
```json
{
  "formatType": "math|physics|chemistry",
  "context": "Your question here"
}
```

**Response:**
```json
{
  "content": "LLM response text"
}
```

#### GET /api/englishllm/details

English language learning assistant.

**Query Parameters:**
- `context` (string, required): The question or text to process

**Response:**
```json
{
  "content": "LLM response text"
}
```

### Learning Content Controllers

#### Learning Content Categories (Public, no auth required, read-only)

- `GET /api/learningcontentcategories` — List all categories
- `GET /api/learningcontentcategories/{id}` — Get category by ID

Categories are system-owned and seeded with 6 defaults. No create/update/delete endpoints.

#### Learning Contents (Auth required)

- `GET /api/learningcontents` — List all contents (optional `?categoryId=` filter)
- `GET /api/learningcontents/{id}` — Get content by ID
- `POST /api/learningcontents` — Create content
- `PUT /api/learningcontents/{id}` — Update content
- `DELETE /api/learningcontents/{id}` — Delete content

### User Learning Controllers (Auth required, user-scoped via JWT)

#### User Learning Histories

- `GET /api/userlearninghistories` — List current user's history (optional `?contentId=` and `?itemId=` filters)
- `GET /api/userlearninghistories/{id}` — Get history by ID
- `POST /api/userlearninghistories` — Create history record
- `PUT /api/userlearninghistories/{id}` — Update history record
- `DELETE /api/userlearninghistories/{id}` — Delete history record

**Search example**: `GET /api/userlearninghistories?contentId=1&itemId=5` returns all history items for the current user on content 1, item 5.

#### User Learning Ratings

All endpoints require JWT Bearer authentication. The `UserId` is extracted from the token (`ClaimTypes.NameIdentifier`, with `sub` as fallback); every query is scoped to the current user — you cannot read, update, or delete another user's ratings. Requests without a valid user ID return `401 Unauthorized`.

**Entity**: `UserLearningRating` — one row per rating. Fields:

| Field | Type | Meaning |
|---|---|---|
| `id` | int | DB primary key (auto-increment). Assigned by SQLite on `POST`; addresses the row in `GET /{id}`, `PUT /{id}`, `DELETE /{id}`. |
| `userId` | string | Ignored on input — server overwrites from JWT. |
| `contentId` | int | FK to `LearningContents.Id`. Which learning content is being rated. |
| `itemId` | int? | Optional. Which specific item *within* the content is being rated. `null` = rating for the whole content. |
| `scoreDate` | date | When the rating was given. Defaults to today if omitted on `POST`. |
| `rating` | byte | Rating value, 1–5 inclusive. |

The response also includes an eagerly-loaded `content` object (the full `LearningContent` the FK points to), to save a second round-trip.

##### `GET /api/userlearningratings`

List the current user's ratings.

**Query parameters:**
- `contentId` (int, optional): only return ratings for this content.
- `itemId` (int, optional): only return ratings for this item within the content. Ignored if `contentId` is not supplied.

Both filters are optional and AND-composed. With no query string, returns every rating the user has ever given (most recent first).

**Example**: `GET /api/userlearningratings?contentId=7` → all of the caller's ratings for content 7.

##### `GET /api/userlearningratings/{id}`

Fetch a single rating by its DB primary key.

**Path parameter:**
- `id` (int, required): the `Id` column of the `UserLearningRatings` row.

**Behavior**:
- `200 OK` + the rating (with nested `content`) if the row exists and belongs to the caller.
- `404 Not Found` if the row does not exist *or* exists but belongs to a different user. Returning `404` for both cases prevents enumerating other users' rating IDs.
- `401 Unauthorized` if the JWT has no user ID.

**Note**: you do **not** need to pass `contentId` to this endpoint — the rating row already carries its own `contentId` column, and the nested `content` object is loaded automatically via the FK.

##### `POST /api/userlearningratings`

Create a new rating.

**Request body:**
```json
{
  "contentId": 7,
  "itemId": 3,
  "rating": 4,
  "scoreDate": "2026-06-20"
}
```

- `contentId` (int, required): must reference an existing `LearningContent`. `400` otherwise.
- `itemId` (int, optional): `null` / omitted = whole-content rating.
- `rating` (int, required): must be between 1 and 5 inclusive. `400` otherwise.
- `scoreDate` (date, optional): defaults to today.
- `userId` is **ignored** if supplied — the server sets it from the JWT.

**Response**: `201 Created` with the new row (including the DB-assigned `id`) and a `Location` header pointing at `/api/userlearningratings/{id}`.

##### `PUT /api/userlearningratings/{id}`

Update an existing rating.

**Path parameter:**
- `id` (int, required): the DB primary key of the rating to update.

**Request body:** same shape as `POST`.

**Behavior**:
- `204 No Content` on success.
- `404 Not Found` if the row does not exist *or* belongs to a different user.
- `400 Bad Request` if `rating` is outside 1–5 or `contentId` does not exist.

##### `DELETE /api/userlearningratings/{id}`

Delete an existing rating.

**Path parameter:**
- `id` (int, required): the DB primary key of the rating to delete.

**Behavior**:
- `204 No Content` on success.
- `404 Not Found` if the row does not exist *or* belongs to a different user.

##### Client-side upsert pattern

The Angular client (`alvachien.com`) implements create-or-update without a dedicated server endpoint:

1. `GET /api/userlearningratings?contentId=X&itemId=Y`
2. If a row is returned → `PUT /{id}` with the new rating value.
3. Otherwise → `POST` a new row.

## Authentication

Most endpoints require JWT Bearer authentication. Tokens are issued by the Identity Server.

**Header:**
```
Authorization: Bearer <your-jwt-token>
```

**Public endpoints** (no auth required, read-only):
- `GET /api/learningcontentcategories`
- `GET /api/learningcontentcategories/{id}`

**User-scoped endpoints**: History and rating controllers extract `UserId` from JWT claims (`ClaimTypes.NameIdentifier` or `sub`). Users can only access/modify their own records.

## Build and Run

### Prerequisites

- .NET 10.0 SDK or later
- SQLite (included with EF Core)

### Build

```bash
dotnet build aclearningutil.sln
```

### Run (Development)

```bash
dotnet run --project src/aclearningutil/aclearningutil.csproj
```

The application will:
1. Create the SQLite database if it doesn't exist
2. Migrate data from JSON file if present
3. Start listening on configured ports (see `src/aclearningutil/Properties/launchSettings.json`)

### Run Tests

```bash
dotnet test
```

### Run (Production)

```bash
dotnet publish src/aclearningutil/aclearningutil.csproj -c Release -o ./publish
cd publish
dotnet aclearningutil.dll
```

## Solution Structure

```
aclearningutil.sln
├── src/aclearningutil/                    # Main ASP.NET Core Web API
│   ├── Controllers/
│   │   ├── TTSController.cs                       # Text-to-Speech API
│   │   ├── FormatLLMController.cs                 # Subject-specific LLM
│   │   ├── EnglishLLMController.cs                # English learning LLM
│   │   ├── LearningContentCategoriesController.cs # Content categories (public)
│   │   ├── LearningContentsController.cs          # Learning content CRUD
│   │   ├── UserLearningHistoriesController.cs     # User learning history
│   │   └── UserLearningRatingsController.cs       # User content ratings
│   ├── Data/
│   │   ├── AppDbContext.cs                        # EF Core DbContext
│   │   └── Entities/
│   │       ├── TtsMapping.cs                      # TTS mapping entity
│   │       ├── LearningContentCategory.cs         # Content category entity
│   │       ├── LearningContent.cs                 # Learning content entity
│   │       ├── UserLearningHistory.cs             # Learning history entity
│   │       └── UserLearningRating.cs              # Content rating entity
│   ├── Models/
│   │   ├── AudioFile.cs
│   │   ├── SentenceJsonMap.cs                     # Legacy JSON model (kept for migration)
│   │   ├── LLMConversation.cs
│   │   ├── LLMConversationMessage.cs
│   │   ├── LLMReplyContent.cs
│   │   └── FormatLLMInput.cs
│   ├── AudioFiles/                                # Generated audio files (runtime)
│   ├── Program.cs                                 # Application entry point
│   ├── Utility/                                   # Utility classes (LLM, Aliyun token)
│   │   ├── LLMUtil.cs                             # LLM API utilities
│   │   └── AliTokenUtil.cs                        # Aliyun token generation
│   └── aclearningutil.csproj
├── src/Util/                                       # Standalone helpers (not compiled into the API)
│   ├── add_learningcontent_columns.py              # DB schema patch helper
│   ├── validate-schema.js                          # Node JSON-schema validator
│   └── exercise-schema.json                        # JSON schema used by the validator
└── test/
    ├── aclearningutil.test/                       # Unit tests (xUnit + Moq)
    └── aclearningutil.test.common/                # Shared test utilities
```

## Logging

### Development
Logs are written to the console with minimum level: Information

### Production
Logs are written to files in `../Logs/aclearningutil/` with:
- Daily rolling files
- Minimum level: Warning
- Retention: 14 days

## Code Quality

This project follows modern .NET best practices:

- ✅ Async/await throughout (no blocking calls)
- ✅ Proper resource disposal (HttpClient, FileStream)
- ✅ Thread-safe HTTP client usage
- ✅ Comprehensive null checking
- ✅ Zero compiler warnings

## License

MIT

## Contact

Alva Chien   
