# Database Schema Definition

This document describes the SQLite database schema for the **aclearningutil** project, an ASP.NET Core 10.0 learning utility API. The schema is defined via Entity Framework Core (code-first) in [`src/aclearningutil/Data/AppDbContext.cs`](../src/aclearningutil/Data/AppDbContext.cs) and the entity classes under [`src/aclearningutil/Data/Entities/`](../src/aclearningutil/Data/Entities/).

## Overview

- **Database engine**: SQLite
- **Provider**: `Microsoft.EntityFrameworkCore.Sqlite`
- **Database file**: `aclearningutil.db` (auto-created in the application root on first run)
- **Connection string**: configured in `Program.cs` as `Data Source={path}`
- **Auto-migration**: on first run, TTS mappings are migrated from `AudioFiles/tts_map.json` if present

The database contains five tables organized around two concerns:

1. **TTS caching** — `TtsMappings` maps a sentence to a generated audio file.
2. **Learning content & user activity** — categories, content items, per-user history, and per-user ratings.

## Entity Relationship Diagram

```
┌─────────────────────────────┐
│  LearningContentCategories  │
│  (system-owned, seeded)     │
│  Id (PK, manual)            │
└──────────┬──────────────────┘
           │ 1
           │
           │ N            OnDelete: Restrict
┌──────────┴──────────────────┐
│      LearningContents       │
│  Id (PK, autoincrement)     │
│  CategoryId (FK) ───────────┘
└──────────┬──────────────────┘
           │ 1
           │
   ┌───────┴────────┐
   │ N              │ N        OnDelete: Restrict (both)
┌──┴─────────────┐ ┌┴───────────────────┐
│UserLearning    │ │UserLearningRatings │
│Histories       │ │                    │
│ ContentId (FK) │ │ ContentId (FK)     │
└────────────────┘ └────────────────────┘
   (both also reference UserId from JWT claims — not a FK to a local users table)


┌─────────────────┐
│   TtsMappings   │   Standalone — no foreign keys.
│   Id (PK)       │   Sentence is UNIQUE-indexed for fast lookups.
└─────────────────┘
```

## Tables

### 1. `TtsMappings`

Caches the mapping between a sentence and the audio file generated for it by the Aliyun TTS API, so repeated TTS requests for the same sentence return the cached file.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `Sentence` | TEXT | No | — | Required, max length 2000, **UNIQUE index** |
| `FileName` | TEXT | No | — | Required, max length 500 |
| `CreatedAt` | TEXT | No | `datetime('now')` | — |

**Indexes**
- `IX_TtsMappings_Sentence` — UNIQUE on `Sentence` (fast sentence → file lookup)

**Foreign keys**: none

**Entity**: [`TtsMapping.cs`](../src/aclearningutil/Data/Entities/TtsMapping.cs)

```sql
CREATE TABLE TtsMappings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Sentence TEXT NOT NULL,
    FileName TEXT NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE UNIQUE INDEX IX_TtsMappings_Sentence ON TtsMappings (Sentence);
```

### 2. `LearningContentCategories`

System-owned categories that classify learning content. The `Id` values are **manually assigned** (not autoincrement) and seeded with six defaults on database creation.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key (manual assignment) |
| `NameChinese` | TEXT | No | — | Required, max length 200 |
| `NameEnglish` | TEXT | No | — | Required, max length 200 |

**Indexes**: none

**Foreign keys**: none

**Entity**: [`LearningContentCategory.cs`](../src/aclearningutil/Data/Entities/LearningContentCategory.cs)

```sql
CREATE TABLE LearningContentCategories (
    Id INTEGER PRIMARY KEY,
    NameChinese TEXT NOT NULL,
    NameEnglish TEXT NOT NULL
);
```

#### Seeded Categories

The `HasData` call in `OnModelCreating` seeds exactly the three entity columns (`Id`, `NameChinese`, `NameEnglish`) — no other columns exist on this table:

| Id | NameChinese | NameEnglish |
|---|---|---|
| 1 | 词汇 | Vocabulary |
| 2 | 句子 | Sentences |
| 3 | 听力 | Listening |
| 4 | 中文 | Chinese |
| 5 | 公式 | Formula |
| 6 | 知识库 | Knowledge Bank |

For reference only (not stored in the database), the corresponding content files on disk for each category are:

| Id | Source folder (filesystem, not a DB column) |
|---|---|
| 1 | `Storage/learnenglish/words.json` |
| 2 | `Storage/learnenglish/sentences.json` |
| 3 | `Storage/englishlistening/data.json` |
| 4 | `Storage/learnchinese/data.json` |
| 5 | (not yet implemented) |
| 6 | `Storage/knowledge-exercises/data.json` |

### 3. `LearningContents`

Individual learning content items, each belonging to a category. `FileUrl` points to the content file under `Storage/`.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `CategoryId` | INTEGER | No | — | Required, FK → `LearningContentCategories(Id)`, `OnDelete: Restrict` |
| `NameChinese` | TEXT | No | — | Required, max length 500 |
| `NameEnglish` | TEXT | No | — | Required, max length 500 |
| `FileUrl` | TEXT | No | — | Required, max length 1000 |
| `CreatedAt` | TEXT | No | `datetime('now')` | — |
| `UpdatedAt` | TEXT | No | `datetime('now')` | — |

**Indexes**
- `IX_LearningContents_CategoryId` — on `CategoryId`

**Foreign keys**
- `CategoryId` → `LearningContentCategories(Id)`, `OnDelete: Restrict`

**Navigation property**: `Category` (`LearningContentCategory?`)

**Entity**: [`LearningContent.cs`](../src/aclearningutil/Data/Entities/LearningContent.cs)

```sql
CREATE TABLE LearningContents (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    CategoryId INTEGER NOT NULL,
    NameChinese TEXT NOT NULL,
    NameEnglish TEXT NOT NULL,
    FileUrl TEXT NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (CategoryId) REFERENCES LearningContentCategories(Id)
);
CREATE INDEX IX_LearningContents_CategoryId ON LearningContents (CategoryId);
```

### 4. `UserLearningHistories`

Per-user learning history records. Scoped to a user via the `UserId` extracted from JWT claims (`ClaimTypes.NameIdentifier` with `"sub"` fallback). `ItemId` optionally identifies a specific item within a content file.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `UserId` | TEXT | No | — | Required, max length 200 |
| `ContentId` | INTEGER | No | — | Required, FK → `LearningContents(Id)`, `OnDelete: Restrict` |
| `ItemId` | INTEGER | Yes | — | Optional |
| `LearnDate` | TEXT | No | `date('now')` | — |
| `SuccessIndicator` | INTEGER | No | — | Boolean (0/1) |

**Indexes**
- `IX_UserLearningHistories_UserId` — on `UserId`
- `IX_UserLearningHistories_ContentId` — on `ContentId`

**Foreign keys**
- `ContentId` → `LearningContents(Id)`, `OnDelete: Restrict`

**Navigation property**: `Content` (`LearningContent?`)

**Entity**: [`UserLearningHistory.cs`](../src/aclearningutil/Data/Entities/UserLearningHistory.cs)

```sql
CREATE TABLE UserLearningHistories (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    ContentId INTEGER NOT NULL,
    ItemId INTEGER,
    LearnDate TEXT NOT NULL DEFAULT (date('now')),
    SuccessIndicator INTEGER NOT NULL,
    FOREIGN KEY (ContentId) REFERENCES LearningContents(Id)
);
CREATE INDEX IX_UserLearningHistories_UserId ON UserLearningHistories (UserId);
CREATE INDEX IX_UserLearningHistories_ContentId ON UserLearningHistories (ContentId);
```

### 5. `UserLearningRatings`

Per-user content ratings. Scoped to a user via the `UserId` extracted from JWT claims. `Rating` is a byte in the range 1–5.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `UserId` | TEXT | No | — | Required, max length 200 |
| `ContentId` | INTEGER | No | — | Required, FK → `LearningContents(Id)`, `OnDelete: Restrict` |
| `ItemId` | INTEGER | Yes | — | Optional |
| `ScoreDate` | TEXT | No | `date('now')` | — |
| `Rating` | INTEGER | No | — | Byte, range 1–5 |

**Indexes**
- `IX_UserLearningRatings_UserId` — on `UserId`
- `IX_UserLearningRatings_ContentId` — on `ContentId`

**Foreign keys**
- `ContentId` → `LearningContents(Id)`, `OnDelete: Restrict`

**Navigation property**: `Content` (`LearningContent?`)

**Entity**: [`UserLearningRating.cs`](../src/aclearningutil/Data/Entities/UserLearningRating.cs)

```sql
CREATE TABLE UserLearningRatings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    ContentId INTEGER NOT NULL,
    ItemId INTEGER,
    ScoreDate TEXT NOT NULL DEFAULT (date('now')),
    Rating INTEGER NOT NULL,
    FOREIGN KEY (ContentId) REFERENCES LearningContents(Id)
);
CREATE INDEX IX_UserLearningRatings_UserId ON UserLearningRatings (UserId);
CREATE INDEX IX_UserLearningRatings_ContentId ON UserLearningRatings (ContentId);
```

## Conventions & Notes

- **Primary keys**: `Id` is an autoincrement INTEGER for all tables except `LearningContentCategories`, where it is manually assigned to keep the seeded category IDs stable.
- **Date/time storage**: SQLite stores datetimes as TEXT. `CreatedAt`/`UpdatedAt` default to `datetime('now')` (full timestamp); `LearnDate`/`ScoreDate` default to `date('now')` (date only).
- **Booleans**: EF Core maps `bool` (`SuccessIndicator`) to INTEGER (0/1) in SQLite.
- **Foreign-key behavior**: all FKs use `OnDelete: Restrict`, so deleting a `LearningContent` that still has history/rating rows, or a `LearningContentCategory` that still has content rows, is prevented at the EF level. (Note: SQLite does not enforce FKs unless `PRAGMA foreign_keys = ON` is set; EF Core's `Restrict` is enforced through the EF change tracker.)
- **User scoping**: `UserLearningHistories` and `UserLearningRatings` are scoped per-user via `UserId`, which comes from the authenticated JWT — there is no local `Users` table.
- **Max-length constraints** (`HasMaxLength`) translate to EF Core metadata and are enforced in the application layer; SQLite itself does not enforce TEXT length limits.

## Modifying the Schema

For development with existing data:

1. Update the entity classes in `src/aclearningutil/Data/Entities/`.
2. Update the `OnModelCreating()` configuration in `src/aclearningutil/Data/AppDbContext.cs`.
3. Either delete `aclearningutil.db` to recreate from scratch (loses data), or apply an EF Core migration:
   ```bash
   dotnet ef migrations add <Name> --project src/aclearningutil
   dotnet ef database update --project src/aclearningutil
   ```

## Inspecting the Database

```bash
sqlite3 src/aclearningutil/aclearningutil.db
> .tables
> .schema TtsMappings
> SELECT * FROM LearningContentCategories;
```
