# Database Schema Definition

This document describes the SQLite database schema for the **aclearningutil** project, an ASP.NET Core 10.0 learning utility API. The schema is defined via Entity Framework Core (code-first) in [`src/aclearningutil/Data/AppDbContext.cs`](../src/aclearningutil/Data/AppDbContext.cs) and the entity classes under [`src/aclearningutil/Data/Entities/`](../src/aclearningutil/Data/Entities/).

## Overview

- **Database engine**: SQLite
- **Provider**: `Microsoft.EntityFrameworkCore.Sqlite`
- **Database file**: `aclearningutil.db` (auto-created in the application root on first run)
- **Connection string**: configured in `Program.cs` as `Data Source={path}`
- **Auto-migration**: on first run, TTS mappings are migrated from `AudioFiles/tts_map.json` if present

The database contains seven content tables organized around two concerns:

1. **TTS caching** — `TtsMappings` maps a sentence to a generated audio file.
2. **Learning content & user activity** — categories, content items, per-user ratings, per-day login history, and per-day-per-category learning time.

The habit-tracking feature adds eleven further tables (`Habits` and friends) — documented in § Habit-Tracking Tables below and in [`design-habit-api.md`](design-habit-api.md).

Everything above lives in `aclearningutil.db` and is EF-managed. The API additionally reads one **external, non-EF data source** — the per-word dictionary database `Storage/words.db` (plus its `wordaudio/` MP3s) — covered end to end in § Workflow: Fetching Word Detail at the end of this document.

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
           │ N        OnDelete: Restrict
   ┌───────┴───────────────────┐
   │     UserLearningRatings   │
   │  ContentId (FK)           │
   └───────────────────────────┘
   (also references UserId from JWT claims — not a FK to a local users table)

UserLoginHistories and UserLearningTimes are standalone (no foreign keys;
UserId is the JWT claim, category ids are not joined). The retired
UserLearningHistories table (§4) used to fan out here too — see its note.


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
| `Version` | INTEGER (tinyint) | Yes | — | Optional, byte (0–255); seeded from JSON `version` |
| `IncludeLatex` | INTEGER | Yes | — | Optional bool (0/1); seeded from JSON `includeLatex` |
| `TranslationDisabled` | INTEGER | Yes | — | Optional bool (0/1); seeded from JSON `translationDisabled` |
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
    Version INTEGER,
    IncludeLatex INTEGER,
    TranslationDisabled INTEGER,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (CategoryId) REFERENCES LearningContentCategories(Id)
);
CREATE INDEX IX_LearningContents_CategoryId ON LearningContents (CategoryId);
```

### 4. `UserLearningHistories` — retired (2026-10-25)

This table is gone from the EF model: the per-item study log was designed but never gained a client (per-word signal is carried by `UserLearningRatings`, usage rollups by `UserLoginHistories` and `UserLearningTimes`). Entity, `DbSet`, model configuration, controller and tests were deleted, and `Program.cs` startup now issues `DROP TABLE IF EXISTS "UserLearningHistories"` so pre-existing databases (which may still hold the empty table, or legacy rows from an early iteration) end up clean and its old `Restrict` FK stops blocking content deletion. The section number stays vacant so later cross-references remain valid.

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
- `IX_UserLearningRatings_UserId_ContentId_ItemId` — **unique**, on `(UserId, ContentId, ItemId)` (`AppDbContext.cs:94-96`): one rating per user/content/item triple — the POST upsert key (§4). Pre-existing databases get it via startup dedup + `CREATE UNIQUE INDEX IF NOT EXISTS` (design-data-seeder.md Phase 0e).

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
CREATE UNIQUE INDEX IX_UserLearningRatings_UserId_ContentId_ItemId ON UserLearningRatings (UserId, ContentId, ItemId);
```

### 6. `UserLoginHistories`

Per-day login history, one row per user per server-local calendar day, upserted by `POST /api/UserLoginHistories` when the SPA completes an OIDC sign-in (see [`design-controllers.md`](design-controllers.md) §15). Standalone — no foreign keys; `UserId` comes from the JWT, there is no local users table.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `UserId` | TEXT | No | — | Required, max length 200 |
| `LoginDate` | TEXT | No | — | Server-local calendar day (`yyyy-MM-dd`, `DateOnly`) |
| `FirstLoginAt` | TEXT | No | — | UTC instant; read converter restores `Kind=Utc` |
| `LastLoginAt` | TEXT | No | — | UTC instant; same converter |
| `LoginCount` | INTEGER | No | — | Distinct sign-ins recorded on that day — the server treats a POST within 5 minutes of the last accepted sign-in as a duplicate report (review finding H3), so scripted loops cannot inflate this |

**Indexes**
- `IX_UserLoginHistories_UserId_LoginDate` — UNIQUE on `(UserId, LoginDate)`; guards the POST's upsert and doubles as the tenant-scoped GET's access path (no separate `UserId` index needed). Like every model object, the table and index reach pre-existing databases through the schema bootstrap's generic Phase-1 `CREATE ... IF NOT EXISTS` replay.

**Entity**: [`UserLoginHistory.cs`](../src/aclearningutil/Data/Entities/UserLoginHistory.cs)

```sql
CREATE TABLE UserLoginHistories (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    LoginDate TEXT NOT NULL,
    FirstLoginAt TEXT NOT NULL,
    LastLoginAt TEXT NOT NULL,
    LoginCount INTEGER NOT NULL
);
CREATE UNIQUE INDEX IX_UserLoginHistories_UserId_LoginDate ON UserLoginHistories (UserId, LoginDate);
```

### 7. `UserLearningTimes`

Accumulated learning time, one row per user per server-local calendar day per content category, upserted by the SPA's 60-second heartbeat (`POST /api/UserLearningTimes`, see [`design-controllers.md`](design-controllers.md) §18). Standalone — no foreign keys; `UserId` comes from the JWT and `CategoryId` intentionally mirrors the seeded `LearningContentCategories` ids without joining (the beat must stay a local write).

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `UserId` | TEXT | No | — | Required, max length 200 |
| `LearnDate` | TEXT | No | — | Server-local calendar day (`yyyy-MM-dd`, `DateOnly`; assigned by the controller, no DB default) |
| `CategoryId` | INTEGER | No | — | 1-6, validated by the controller |
| `UserName` | TEXT | Yes | — | Optional display name from the heartbeat (max 200, last non-empty wins); leaderboard label only. Added after the table shipped — `EnsureColumnAsync` brings pre-existing databases up to date |
| `LearningSeconds` | INTEGER | No | — | Server-credited cumulative seconds for the day |
| `LastBeatAt` | TEXT | No | — | UTC instant of the last accepted beat on THIS row; read converter restores `Kind=Utc`. Across the day's rows it forms the shared per-user-per-day cursor — each beat credits `MAX(lastBeatAt)`-based elapsed time (cap 75 s/beat), so the day total cannot exceed real elapsed time |

**Indexes**
- `IX_UserLearningTimes_UserId_LearnDate_CategoryId` — UNIQUE on `(UserId, LearnDate, CategoryId)`; guards the heartbeat upsert (Serializable transaction) and serves the tenant-scoped GET. Reaches pre-existing databases through the schema bootstrap's Phase-1 replay.

**Entity**: [`UserLearningTime.cs`](../src/aclearningutil/Data/Entities/UserLearningTime.cs)

```sql
CREATE TABLE UserLearningTimes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    LearnDate TEXT NOT NULL,
    CategoryId INTEGER NOT NULL,
    LearningSeconds INTEGER NOT NULL,
    LastBeatAt TEXT NOT NULL,
    UserName TEXT
);
CREATE UNIQUE INDEX IX_UserLearningTimes_UserId_LearnDate_CategoryId ON UserLearningTimes (UserId, LearnDate, CategoryId);
```

### 8. `LeaderboardPreferences`

Optional per-user leaderboard visibility choice (opt-out companion to the heartbeat's `userName`): a row exists once the user has ever touched the switch; absence means visible. Standalone — no foreign keys, `UserId` from the JWT.

| Column | Type | Nullable | Default | Constraints |
|---|---|---|---|---|
| `Id` | INTEGER | No | — | Primary Key, autoincrement |
| `UserId` | TEXT | No | — | Required, max length 200, UNIQUE |
| `LeaderboardOptOut` | INTEGER | No | — | Boolean (0/1); true excludes the user's totals from the Leaderboard query |

**Entity**: [`LeaderboardPreference.cs`](../src/aclearningutil/Data/Entities/LeaderboardPreference.cs)

```sql
CREATE TABLE LeaderboardPreferences (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UserId TEXT NOT NULL,
    LeaderboardOptOut INTEGER NOT NULL
);
CREATE UNIQUE INDEX IX_LeaderboardPreferences_UserId ON LeaderboardPreferences (UserId);
```

## Habit-Tracking Tables

The habit-tracking feature (see [`design-habit-api.md`](design-habit-api.md)) adds eleven more tables to this database — `Habits`, `HabitShareGrants`, `HabitItems`, `ItemProperties`, `Criteria`, `CriterionLeaves` (+ `CriterionLeafScopeItems`), `CriterionComposites` (+ `CriterionCompositeOperands`), `Punches`, `PunchValues` — with their own per-user `OwnerId` tenant column. The full per-table column/index/FK listing lives in `design-habit-api.md` § Data Model; the index inventory for `Punches` is:

- `IX_Punches_HabitId_PunchDate` — on `(HabitId, PunchDate)`
- `IX_Punches_ItemId_PunchDate` — on `(ItemId, PunchDate)`
- `IX_Punches_OwnerId` — on `OwnerId` (tenant-key filter; added to the EF model with the habit work and rolled out to pre-existing databases by the schema bootstrap's Phase-1 `CREATE INDEX IF NOT EXISTS` replay)

## Conventions & Notes

- **Primary keys**: `Id` is an autoincrement INTEGER for all tables except `LearningContentCategories`, where it is manually assigned to keep the seeded category IDs stable.
- **Date/time storage**: SQLite stores datetimes as TEXT. `CreatedAt`/`UpdatedAt` default to `datetime('now')` (full timestamp); `ScoreDate` defaults to `date('now')` (date only); `LoginDate`/`LearnDate` (`UserLearningTimes`) carry no DB default and are assigned server-side as `DateOnly`.
- **Booleans**: EF Core maps `bool` to INTEGER (0/1) in SQLite (`SuccessIndicator`, and the nullable `IncludeLatex` / `TranslationDisabled` on `LearningContents`).
- **Foreign-key behavior**: all FKs use `OnDelete: Restrict`, so deleting a `LearningContent` that still has rating rows, or a `LearningContentCategory` that still has content rows, is prevented at the EF level. (Note: SQLite does not enforce FKs unless `PRAGMA foreign_keys = ON` is set; EF Core's `Restrict` is enforced through the EF change tracker.)
- **User scoping**: `UserLearningRatings`, `UserLoginHistories` and `UserLearningTimes` are scoped per-user via `UserId`, which comes from the authenticated JWT — there is no local `Users` table.
- **Max-length constraints** (`HasMaxLength`) translate to EF Core metadata and are enforced in the application layer; SQLite itself does not enforce TEXT length limits.

## Modifying the Schema

This project does **not** use EF Core migrations. The schema is created on startup via `db.Database.EnsureCreated()` (see [`Program.cs`](../src/aclearningutil/Program.cs)), which only builds a database that does not yet exist — it never alters an existing one. To evolve the schema against a database that already exists:

1. Add the column to the entity class in `src/aclearningutil/Data/Entities/`.
2. Configure it in `OnModelCreating()` in `src/aclearningutil/Data/AppDbContext.cs`.
3. Add an `EnsureColumnAsync(...)` call in `Program.cs` so startup runs `ALTER TABLE ... ADD COLUMN` for the new nullable column on pre-existing databases — mirror exactly what `EnsureCreated` would have created (e.g. `INTEGER` for bool/byte columns). This is how `IncludeLatex` and `TranslationDisabled` were added. **New tables** (e.g. `HabitShareGrants`) instead go through `HabitSchemaBootstrap` automatically: its Phase-1 replay of the EF create script (`CREATE ... IF NOT EXISTS`) materializes missing tables on existing databases — no `ALTER` or `EnsureColumnAsync` involved.
4. For a fresh database, just delete `aclearningutil.db` and let `EnsureCreated` recreate it (loses data).

A standalone helper, [`src/Util/add_learningcontent_columns.py`](../src/Util/add_learningcontent_columns.py), applies the same `ALTER TABLE` patch with a `.bak` backup and idempotent re-runs, for manually patching a deployed database outside the app.

## Inspecting the Database

```bash
sqlite3 src/aclearningutil/aclearningutil.db
> .tables
> .schema TtsMappings
> SELECT * FROM LearningContentCategories;
```

## Workflow: Fetching Word Detail (`words.db` + `wordaudio/`)

`Storage/words.db` is a **standalone SQLite file, not part of `aclearningutil.db`** and invisible to EF/`AppDbContext`; it carries one table per word entry aspect, alongside the pronunciation MP3s in `Storage/wordaudio/`. Everything about it lives outside this repo's build: this API only ever opens it with `SqliteOpenMode.ReadOnly`, so it is exempt from `EnsureCreated()` and the schema-evolution procedure above, and content changes arrive wholesale with a deployment. The pipeline has three stages — **produce** (utility tools in `knowledgebuilder-content`), **distribute** (copy into `Storage/`), **consume** (two read-only endpoints) — described below. The authoritative schema is the `DB_SCHEMA` constant in `fetch_word.py`; column semantics and merge rules are documented in [`../../knowledgebuilder-content/util/how-to-use.md`](../../knowledgebuilder-content/util/how-to-use.md).

### 1. Producing the content — `knowledgebuilder-content/util/`

The database and audio are generated by `fetch_word.py`, which queries the Youdao dictionary for each word of a plain-text word list (one word per line):

- **Full mode** — `python fetch_word.py --file words.txt`: for each word, fetch the raw dictionary JSON (`dict.youdao.com/jsonapi`) into `util/word_raw/` and download the **US pronunciation MP3** (`dict.youdao.com/dictvoice?audio=<word>&type=2`; UK audio is deliberately not downloaded) into `util/wordaudio/`, then transform the raw JSON and insert a rich entry into `util/words.db`. Idempotent at every step — existing raw files, MP3s and DB rows are skipped — so an interrupted run resumes by re-running; `--sleep` (default 10 s, polite pacing after each word's request batch) and `--limit` (max network requests per run) throttle it, and a `words.lock` file keeps runs single-instance. Textbook notation (`labo(u)r`, `add ... to ...`, `keep one's balance`) is normalized for the query while entries keep the original wording as the DB key.
- **Transform mode** — `python fetch_word.py --words "present,go"`: rebuild/reinsert entries from cached raw files only, no network.
- Maintenance: `reconcile_audio.py` re-syncs the DB's audio paths/flags with `wordaudio/` on disk (`--apply`, `--refetch-missing` re-downloads dangling MP3s); `check_missing_words.py` reports content-file words absent from the DB (its output, `missing_words.md`, is the shopping list for the next fetch).

Git conventions in the content repo: `words.db` is **committed**; `wordaudio/*.mp3` and `word_raw/` are **gitignored** (regenerable) — so cloning the content repo gives the database but not the audio, and the MP3s travel with the operator's working copy.

### 2. Distributing it — copying db + audio into `Storage/`

`Storage/` is gitignored in `aclearningutil` and populated either by hand (dev) or by the publish script (deploy). Neither `words.db` nor `wordaudio/` is a served content subfolder — `StorageController` allow-lists only the five category folders, and both live at the `Storage/` **root**, reachable exclusively through the stage-3 endpoints:

- **Deploy**: `publish-learning-all.ps1` copies `knowledgebuilder-content/util/words.db` → published `Storage/words.db` and `util/wordaudio/` → `Storage/wordaudio/` (warning, not failure, when either is missing).
- **Dev**: copy both manually from `knowledgebuilder-content/util/` into `src/aclearningutil/Storage/` (same convention as the five content folders).
- The DB references its MP3s by *relative path* (`wordaudio/<stem>.mp3`, the stem being the sanitized word), and the audio's existence is precomputed at authoring time into the `has_us_audio`/`has_uk_audio` flags — so the pair must always be copied together, and after any drift between them run `reconcile_audio.py --apply` before publishing.

### 3. Consuming it — the two read-only endpoints

Both are JWT-authenticated (`api.knowledgebuilder`), intentionally not rate-limited (local file reads, unlike the LLM/TTS spend endpoints), 404 on blank/unknown words or a missing `words.db`, and carry `Cache-Control: public, max-age=2592000` (30 days — the library only changes at deploy time, so a long TTL keeps repeat study sessions off the API). Full contracts in [`design-controllers.md`](design-controllers.md) §16–§17.

- `GET /api/WordAudio?word=` — `WordAudioController` runs `SELECT us_audio, uk_audio FROM words WHERE word = @word COLLATE NOCASE LIMIT 1` (US preferred, UK fallback), treats the returned path as untrusted (basename only, must end `.mp3`, must resolve inside `Storage/wordaudio/`), then streams the MP3 as `audio/mpeg`.
- `GET /api/WordDictionary?word=` — `WordDictionaryController` first resolves the canonical key `SELECT COALESCE(base_word, word) ...` (spelling variants carry no detail rows), then reads `definitions` and `examples` (capped at 10) for that key and returns them as the `WordDictionaryDto` JSON.

The `knowledgebuilder` vocabulary review screen is the sole consumer: recorded audio first, browser `speechSynthesis` on 404; definitions/examples sections likewise hide quietly on a miss (~9% of textbook entries — mostly phrase-heavy lists — were never fetched, so a miss is normal, not an error).

### Schema inventory

Nine tables (row counts approximate, as of 2026-10). The API consumes three of them:

| Table | Rows | Consumed by | Notes |
|---|---:|---|---|
| `words` | 10.2k | §16 `GET /api/WordAudio` (`us_audio`, `uk_audio`); §17 `GET /api/WordDictionary` (key + `base_word` variant resolution) | Headword rows: `word` PK (original textbook wording incl. phrases like `add ... to ...`), `type` word/phrase, UK/US phonetic text, audio paths + `has_*_audio` flags, `base_word` FK pointing spelling variants at their canonical row |
| `definitions` | 18.5k | §17 `definitions[]` | Chinese meanings per sense; keyed by the **base** spelling; `part_of_speech` is `''` for phrase glosses |
| `examples` | 62.9k | §17 `examples[]` (capped at 10 per response) | Bilingual pairs — `source` = English sentence, `translation` = Chinese (the column name `source` is historical, not an attribution) |
| `english_definitions` | 41.3k | — *dormant* | English (WordNet-style) senses, FK to `english_definition_examples` for per-sense sentences |
| `english_definition_examples` | 24.8k | — *dormant* | Example sentences attached to `english_definitions` |
| `phrases` | 58.5k | — *dormant* | Collocations listed under a headword |
| `synonyms` | 34.8k | — *dormant* | Synonym groups by POS/meaning |
| `word_forms` | 21.8k | — *dormant* | Inflections (过去式/复数/…) |
| `exam_tags` | 43.9k | — *dormant* | Exam labels (初中/高中/CET4/CET6/考研/IELTS…) |

Cross-cutting lookup rules the endpoints implement (and any future `words.db` consumer must respect): matching is `COLLATE NOCASE` at most one row per case-folded spelling; detail rows always attach to the base spelling, resolved via `COALESCE(base_word, word)` (single level — no chains); child ordering is `ORDER BY seq, rowid` because the base-word merge stamps deduplicated rows `seq = -1`; and all columns are `NOT NULL DEFAULT ''`, so readers never face NULLs. The dormant tables are deployed-but-unserved headroom: exposing them (e.g., a richer word card) is a new endpoint only — no deployment or content-pipeline change.
