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

The application uses SQLite for persistent storage:

- **Database File**: `aclearningutil.db` (auto-created on first run)
- **Location**: Application root directory

### Schema

```sql
CREATE TABLE TtsMappings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Sentence TEXT NOT NULL UNIQUE,
    FileName TEXT NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT(datetime('now'))
);

CREATE TABLE LearningContentCategories (
    Id INTEGER PRIMARY KEY,
    NameChinese TEXT NOT NULL,
    NameEnglish TEXT NOT NULL
);
-- Seeded with 6 default categories: 词汇/Vocabulary, 句子/Sentences, 听力/Listening,
-- 中文/Chinese, 公式/Formula, 知识库/Knowledge Bank

CREATE TABLE LearningContents (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CategoryId INTEGER NOT NULL,
    NameChinese TEXT NOT NULL,
    NameEnglish TEXT NOT NULL,
    FileUrl TEXT NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT(datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT(datetime('now')),
    FOREIGN KEY (CategoryId) REFERENCES LearningContentCategories(Id)
);

CREATE TABLE UserLearningHistories (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    ContentId INTEGER NOT NULL,
    ItemId INTEGER,
    LearnDate TEXT NOT NULL DEFAULT(date('now')),
    SuccessIndicator INTEGER NOT NULL,
    FOREIGN KEY (ContentId) REFERENCES LearningContents(Id)
);

CREATE TABLE UserLearningRatings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    ContentId INTEGER NOT NULL,
    ItemId INTEGER,
    ScoreDate TEXT NOT NULL DEFAULT(date('now')),
    Rating INTEGER NOT NULL,
    FOREIGN KEY (ContentId) REFERENCES LearningContents(Id)
);
```

### Migration from JSON

On first startup, the application automatically migrates data from `AudioFiles/tts_map.json` to the SQLite database:

1. Reads existing mappings from JSON file
2. Inserts records into database (skipping duplicates)
3. Renames `tts_map.json` → `tts_map.json.migrated` as backup
4. Logs migration statistics

Subsequent startups use the database directly.

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

- `GET /api/userlearningratings` — List current user's ratings (optional `?contentId=` and `?itemId=` filters)
- `GET /api/userlearningratings/{id}` — Get rating by ID
- `POST /api/userlearningratings` — Create rating (validates 1-5)
- `PUT /api/userlearningratings/{id}` — Update rating
- `DELETE /api/userlearningratings/{id}` — Delete rating

**Search example**: `GET /api/userlearningratings?contentId=1&itemId=5` returns the rating for the current user on content 1, item 5.

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
│   ├── Util/
│   │   ├── LLMUtil.cs                             # LLM API utilities
│   │   └── AliTokenUtil.cs                        # Aliyun token generation
│   ├── AudioFiles/                                # Generated audio files (runtime)
│   ├── Program.cs                                 # Application entry point
│   └── aclearningutil.csproj
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
