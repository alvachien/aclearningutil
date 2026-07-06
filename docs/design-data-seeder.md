# Data Seeder

This document describes the **data seeder** for the **aclearningutil** project — the startup logic that creates the SQLite database, seeds the system-owned content categories, performs a one-time TTS cache migration, and synchronizes learning content items from on-disk JSON index files.

All seeding runs inline in [`src/aclearningutil/Program.cs`](../src/aclearningutil/Program.cs), inside a single DI scope, immediately after the host is built and before the request pipeline starts. The schema and `HasData` seed data are defined in [`src/aclearningutil/Data/AppDbContext.cs`](../src/aclearningutil/Data/AppDbContext.cs). The full database schema is documented separately in [`docs/design-database.md`](design-database.md).

## Overview

The seeder executes four phases, in order, every time the application starts:

| Phase | What it does | Idempotent? | Source of truth |
|---|---|---|---|
| 0. **Create database** | `EnsureCreated()` builds the schema and applies the EF Core `HasData` seed | Yes (no-op if DB exists) | `AppDbContext.OnModelCreating` |
| 0b. **Patch schema** | `EnsureColumnAsync` adds new nullable columns to `LearningContents` on pre-existing DBs | Yes (skips columns that already exist) | `EnsureColumnAsync` calls in `Program.cs` |
| 1. **Seed categories** | Inserts the 6 content categories if missing | Yes (`AnyAsync` + catch `SqliteException`) | Hardcoded array in `Program.cs` |
| 2. **Migrate TTS cache** | One-time import of `AudioFiles/tts_map.json` into `TtsMappings` | Yes (file renamed on success) | `AudioFiles/tts_map.json` |
| 3. **Sync content** | Reconciles `LearningContents` with 6 JSON index files under `Storage/` | Yes (add new, update changed, never delete) | `Storage/<subfolder>/<index>.json` |

**Design goals**

- **Safe to run on every start.** Every phase checks for existing rows before writing; re-running only adds what is missing.
- **Concurrent-start tolerant.** Two instances starting at once will not corrupt each other — unique constraints and caught `SqliteException`s act as a fallback for the `AnyAsync` race.
- **Non-destructive.** Content items that disappear from the JSON index are *never* deleted from the database, because `UserLearningHistories` and `UserLearningRatings` reference them via foreign keys (`OnDelete: Restrict`). They are only logged as orphans.
- **Ensure the database schema is up to date.** `EnsureCreated()` creates the full schema from the EF Core model, so a fresh database always matches the current model. On an already-existing database the call is a no-op, so `EnsureColumnAsync` runs immediately after to `ALTER TABLE ... ADD COLUMN` for any new nullable columns (e.g. `IncludeLatex`, `TranslationDisabled`) — see Phase 0b and [`docs/design-database.md`](design-database.md) § Modifying the Schema.

### Overall startup sequence

```mermaid
sequenceDiagram
    participant App as Program.cs
    participant DB as AppDbContext
    participant FS as FileSystem
    participant SQLite as SQLite (aclearningutil.db)

    App->>DB: CreateScope().GetRequiredService<AppDbContext>()
    Note over App: Phase 0 — create database
    App->>SQLite: Database.EnsureCreated()
    Note over SQLite: First run: create schema + HasData (6 categories)<br/>Subsequent runs: no-op

    Note over App: Phase 0b — patch schema for new nullable columns
    App->>SQLite: EnsureColumnAsync → ALTER TABLE ADD COLUMN (if missing)

    Note over App: Phase 1 — seed 6 categories (idempotent)
    App->>SQLite: per-category AnyAsync → Add → SaveChangesAsync

    Note over App: Phase 2 — migrate TTS cache
    App->>FS: File.Exists(AudioFiles/tts_map.json)
    alt exists
        App->>FS: read + deserialize
        App->>SQLite: insert non-duplicate TtsMappings
        App->>FS: rename → tts_map.json.migrated
    end

    Note over App: Phase 3 — sync LearningContents (6 index files)
    App->>FS: read each Storage/{sub}/{index}.json
    App->>SQLite: add new / update existing LearningContents
    Note over App: log (do not delete) orphans

    Note over App: Continue to HTTP pipeline (Swagger, CORS, auth, …)
```

---

## Phase 0 — Create database (`EnsureCreated`)

```csharp
db.Database.EnsureCreated();
```

`EnsureCreated()` creates the SQLite file and all tables from the EF Core model **only if the database does not already exist**. On the same call it applies the `HasData` seed in [`AppDbContext.OnModelCreating`](../src/aclearningutil/Data/AppDbContext.cs), which inserts **all six** categories:

| Id | NameChinese | NameEnglish | Seeded by |
|---|---|---|---|
| 1 | 词汇 | Vocabulary | `HasData` |
| 2 | 句子 | Sentences | `HasData` |
| 3 | 听力 | Listening | `HasData` |
| 4 | 中文 | Chinese | `HasData` |
| 5 | 公式 | Formula | `HasData` **only** |
| 6 | 知识库 | Knowledge Bank | `HasData` |

> The Phase 1 array mirrors `HasData` exactly — all six categories (including Formula, Id 5) and the plural `"Sentences"` spelling. Either path produces the same rows.

Because `EnsureCreated` is a no-op on an existing database, **it does not apply schema changes** to a database that already exists. New nullable columns are added instead by the `EnsureColumnAsync` step in Phase 0b; for the full story see [`docs/design-database.md`](design-database.md) § Modifying the Schema.

---

## Phase 0b — Patch schema for new columns (`EnsureColumnAsync`)

Immediately after `EnsureCreated`, `Program.cs` runs a local function `EnsureColumnAsync` for each nullable column added to `LearningContent` after the initial schema. `EnsureCreated` only ever creates a missing database, so an already-existing `aclearningutil.db` would otherwise be missing the new columns; this step adds them with `ALTER TABLE ... ADD COLUMN`.

```csharp
await EnsureColumnAsync(db, app.Logger, "LearningContents", "IncludeLatex", "INTEGER");
await EnsureColumnAsync(db, app.Logger, "LearningContents", "TranslationDisabled", "INTEGER");
```

`EnsureColumnAsync` reads `pragma_table_info` for the table and only runs the `ALTER TABLE` when the column is absent, so it is **idempotent** and safe on both fresh and already-patched databases. The column type (`INTEGER`) mirrors exactly what EF Core would have created — SQLite has no native BOOLEAN type, and both `tinyint` and `bool` map to INTEGER affinity.

> `Version` was added the same way historically; the standalone helper [`Util/add_learningcontent_columns.py`](../src/Util/add_learningcontent_columns.py) performs the identical patch outside the app for already-deployed databases.

Only **nullable** columns can be added this way — SQLite's `ALTER TABLE ... ADD COLUMN` cannot add a `NOT NULL` column without a default. New required columns still require recreating the database.

---

## Phase 1 — Seed content categories

Immediately after `EnsureCreated`, `Program.cs` ensures all six categories exist via an explicit, idempotent loop. This guards the case where the database already exists (so `HasData` did not run) but a category row is missing.

```mermaid
sequenceDiagram
    participant App as Program.cs
    participant DB as AppDbContext
    participant SQLite as SQLite

    loop for each of 6 categories (Id = 1, 2, 3, 4, 5, 6)
        App->>SQLite: AnyAsync(c => c.Id == id)
        alt row does not exist
            App->>DB: LearningContentCategories.Add(new { Id, NameChinese, NameEnglish })
            App->>SQLite: SaveChangesAsync()
            alt SqliteException (concurrent start raced us)
                Note over App: db.ChangeTracker.Clear() — ignore
            end
        else row already exists
            Note over App: skip (idempotent)
        end
    end
```

The six categories inserted by this loop:

| Id | NameChinese | NameEnglish |
|---|---|---|
| 1 | 词汇 | Vocabulary |
| 2 | 句子 | Sentences |
| 3 | 听力 | Listening |
| 4 | 中文 | Chinese |
| 5 | 公式 | Formula |
| 6 | 知识库 | Knowledge Bank |

**Concurrency handling.** The `AnyAsync` check and the `SaveChangesAsync` are not atomic across instances. If two instances start simultaneously and both observe "not exists" for the same `Id`, one insert succeeds and the other throws a `SqliteException` (primary-key violation). The `catch (SqliteException)` block calls `db.ChangeTracker.Clear()` to discard the conflicting tracked entity and continues — the category is already present, so the outcome is correct. This is the comment's "INSERT OR IGNORE semantics".

---

## Phase 2 — Migrate TTS cache from `tts_map.json`

This is a **one-time** migration path that imports a legacy JSON cache of sentence→audio-file mappings into the `TtsMappings` table. It is triggered by the presence of `AudioFiles/tts_map.json` and marks completion by renaming the file so it is not re-imported.

```mermaid
sequenceDiagram
    participant App as Program.cs
    participant FS as FileSystem
    participant DB as AppDbContext
    participant SQLite as SQLite

    App->>FS: File.Exists(AudioFiles/tts_map.json)
    alt file missing
        Note over App: skip migration entirely
    else file exists
        App->>FS: File.ReadAllTextAsync(jsonFile)
        App->>App: JsonSerializer.Deserialize<List<SentenceJsonMap>>
        alt mappings != null && Count > 0
            loop for each mapping
                App->>SQLite: AnyAsync(m => m.Sentence == mapping.Sentence)
                alt sentence not in DB
                    App->>DB: TtsMappings.Add { Sentence, FileName, CreatedAt = UtcNow }
                    Note over App: migratedCount++
                else already cached
                    Note over App: skip
                end
            end
            alt migratedCount > 0
                App->>SQLite: SaveChangesAsync()
                App->>App: Log.Information("Migrated {Count} …")
            else
                App->>App: Log.Information("All TTS mappings already exist.")
            end
        end
        App->>FS: File.Move(jsonFile → tts_map.json.migrated, overwrite)
        App->>App: Log.Information("Backed up …")
    end
```

**Source model** — [`SentenceJsonMap`](../src/aclearningutil/Models/SentenceJsonMap.cs):

| Property | Type | Maps to |
|---|---|---|
| `Sentence` | string | `TtsMappings.Sentence` (UNIQUE-indexed) |
| `FileName` | string | `TtsMappings.FileName` |

**Deduplication.** `TtsMappings.Sentence` carries a UNIQUE index (see [`AppDbContext`](../src/aclearningutil/Data/AppDbContext.cs)). The `AnyAsync` pre-check avoids duplicates in the common case; the unique index is the hard guarantee. `CreatedAt` is set explicitly to `DateTime.UtcNow` (overriding the column's `datetime('now')` default) so imported rows carry the migration timestamp.

**Completion marker.** Regardless of whether any rows were migrated, the file is renamed to `tts_map.json.migrated` (overwriting any previous backup). On the next start the source file no longer exists, so the phase is skipped. If the migration throws, the `catch (Exception)` logs a warning and the file is *not* renamed — so the next start will retry.

---

## Phase 3 — Sync `LearningContents` from JSON index files

This is the most substantial phase. It reconciles the `LearningContents` table with six JSON index files on disk, one per content category. The work is done by a single local function, `SyncLearningContentsFromJsonAsync`, invoked six times with different parameters.

### The six sync calls

| # | Subfolder | Index file | CategoryId | Category |
|---|---|---|---|---|
| 1 | `learnenglish` | `words.json` | 1 | Vocabulary |
| 2 | `learnenglish` | `sentences.json` | 2 | Sentences |
| 3 | `knowledge-exercises` | `data.json` | 6 | Knowledge Bank |
| 4 | `learnchinese` | `data.json` | 4 | Chinese |
| 5 | `englishlistening` | `data.json` | 3 | Listening |
| 6 | `formula` | `formula.json` | 5 | Formula |

> **Note:** `formula.json` entries also carry a `contenttype` field (e.g. `"math"`/`"physics"`/`"chemistry"`) that has no corresponding DB column and is ignored by the sync logic. The `version` field is synced (see below).

### JSON index file format

Each index file is a JSON array of objects. The seeder is tolerant of two name keys:

```json
[
  { "name": "CET 4", "file": "cet4.json" },
  { "book": "Listen To This: I", "file": "ltt1.json" }
]
```

| Key | Required | Purpose |
|---|---|---|
| `file` | yes | Filename within the subfolder; used to build `FileUrl`. Entries missing this (or non-string) are skipped with a warning. |
| `name` | one of `name`/`book` | Standard content name. Written to both `NameChinese` and `NameEnglish`. |
| `book` | (alternative to `name`) | Legacy key used by `englishlistening`. Used only when `name` is absent. |
| `version` | no | Optional byte (0–255). Written to `LearningContents.Version` on insert; on update, applied only when present and differing (a missing field never clears an existing value). Out-of-range values are logged and ignored. Present on `knowledge-exercises` and `formula` entries. |
| `includeLatex` | no | Optional bool. Written to `LearningContents.IncludeLatex` on insert; on update, applied only when present and differing (missing → untouched). Present on some `knowledge-exercises` entries. |
| `translationDisabled` | no | Optional bool. Written to `LearningContents.TranslationDisabled` on insert; on update, applied only when present and differing (missing → untouched). Not currently present in any index file; read if present (intended for `learnchinese`). |
| `contenttype` | no | Present only on `formula` entries. Has no DB column and is ignored. |

If neither `name` nor `book` is present (or empty), the entry is skipped with a warning.

### `FileUrl` patterns and legacy migration

The seeder writes `FileUrl` in the **primary** form `storage/{subFolder}/{file}`. To recognize rows written by older builds, it also matches two legacy forms when looking for an existing record:

| Pattern | Role |
|---|---|
| `storage/{subFolder}/{file}` | **Primary** — always used for new inserts and as the migration target. |
| `data/{subFolder}/{file}` | Legacy form #1 — recognized for matching; migrated to primary if found. |
| `{subFolder}/{file}` | Legacy form #2 — recognized for matching; migrated to primary if found. |

### Sync sequence (per index file)

```mermaid
sequenceDiagram
    participant App as SyncLearningContentsFromJsonAsync
    participant FS as FileSystem
    participant DB as AppDbContext
    participant SQLite as SQLite

    App->>FS: File.Exists(Storage/{sub}/{index}.json)
    alt missing
        App->>App: LogWarning("Index file not found …"); return
    end
    App->>FS: File.ReadAllTextAsync
    App->>App: Deserialize JsonElement[] (case-insensitive)
    alt null or empty
        App->>App: LogWarning("No entries …"); return
    end

    Note over App: Build jsonFileUrls set — 3 URL patterns per entry
    App->>SQLite: existingMatches = LearningContents<br/>Where(CategoryId == id && FileUrl in jsonFileUrls)

    loop for each JSON entry
        App->>App: name = entry["name"] ?? entry["book"]; file = entry["file"]
        alt name or file missing/empty
            App->>App: LogWarning; continue
        end
        App->>App: parse optional version / includeLatex / translationDisabled
        App->>App: existing = existingMatches.FirstOrDefault(match by 3 URL patterns)
        alt existing found
            App->>App: if FileUrl != primary → migrate to storage/{sub}/{file}
            App->>App: if name / version / includeLatex / translationDisabled changed → update + UpdatedAt = UtcNow
            Note over App: updateCount++ (if changed)
        else not found
            App->>DB: LearningContents.Add(new { CategoryId, name, name, primaryFileUrl, version, includeLatex, translationDisabled, UtcNow, UtcNow })
            Note over App: addCount++
        end
    end

    alt addCount > 0 || updateCount > 0
        App->>SQLite: SaveChangesAsync()
        App->>App: LogInformation("Synced …: {Added} added, {Updated} updated")
    end

    Note over App: Orphan detection
    App->>App: orphaned = existingMatches where FileUrl NOT in jsonFileUrls
    alt orphaned.Count > 0
        App->>App: LogWarning("Found {Count} in DB but not in JSON — NOT deleted")
    end
```

### What gets written

For a JSON entry with no matching DB row, a new `LearningContent` is inserted:

| Column | Value |
|---|---|
| `CategoryId` | the call's `categoryId` |
| `NameChinese` | `name` |
| `NameEnglish` | `name` (same value) |
| `FileUrl` | `storage/{subFolder}/{file}` |
| `Version` | the entry's `version` (byte), or `null` if absent |
| `IncludeLatex` | the entry's `includeLatex` (bool), or `null` if absent |
| `TranslationDisabled` | the entry's `translationDisabled` (bool), or `null` if absent |
| `CreatedAt` | `DateTime.UtcNow` |
| `UpdatedAt` | `DateTime.UtcNow` |

For an entry that matches an existing row (by any of the three URL patterns), the seeder **patches in place**:
- If `FileUrl` is a legacy form, it is rewritten to the primary `storage/...` form.
- If `NameChinese` or `NameEnglish` differ from `name`, both are updated.
- If the entry declares a `version` that differs from the stored value, `Version` is updated. A missing `version` field leaves the stored value untouched (it is never cleared).
- If the entry declares `includeLatex` and it differs from the stored value, `IncludeLatex` is updated (missing → untouched).
- If the entry declares `translationDisabled` and it differs from the stored value, `TranslationDisabled` is updated (missing → untouched).
- If any of the above changed, `UpdatedAt` is bumped to `DateTime.UtcNow` and `updateCount` is incremented.

### Orphan handling (non-destructive)

After processing all JSON entries, the seeder computes the set of pre-loaded `existingMatches` whose `FileUrl` is **not** in `jsonFileUrls` — i.e. rows that exist in the database for this category but have no corresponding entry in the JSON index. These are **logged as warnings only**:

```
Found {Count} content items for category {CategoryId} in DB but not in {JsonFileName}: {Titles}.
These are NOT deleted to protect user learning histories and ratings. Remove them manually if needed.
```

This protects referential integrity: `UserLearningHistories.ContentId` and `UserLearningRatings.ContentId` both reference `LearningContents(Id)` with `OnDelete: Restrict`. Deleting a content item that a user has history or ratings for would either fail or orphan user data. Manual removal is left to the operator.

> `SaveChangesAsync()` is called whenever a sync pass produces **any** change — new inserts (`addCount > 0`) **or** in-place updates (`updateCount > 0`). Both adds and updates are persisted on every pass.

### Per-entry error isolation

Each entry is processed inside its own `try/catch`. A malformed entry (missing/invalid `file`, JSON type mismatch, etc.) logs a warning and the loop continues with the next entry — one bad entry does not abort the sync for the whole file.

---

## Idempotency & safety summary

| Property | How it is guaranteed |
|---|---|
| **Re-runnable on every start** | Phase 1 `AnyAsync` check; Phase 2 file rename; Phase 3 add/update-with-match-check. |
| **Schema stays current on existing DBs** | Phase 0b `EnsureColumnAsync` adds new nullable columns only when missing (idempotent). |
| **No duplicate categories** | `AnyAsync` pre-check + PK violation caught as `SqliteException` + `ChangeTracker.Clear()`. |
| **No duplicate TTS mappings** | `AnyAsync` pre-check + UNIQUE index on `TtsMappings.Sentence`. |
| **No duplicate content items** | Pre-loaded `existingMatches` matched by `CategoryId` + three `FileUrl` patterns. |
| **No data loss on content removal** | Orphans are logged, never deleted (protects user history/rating FKs). |
| **Concurrent-start safe** | Caught `SqliteException` in Phase 1; per-entry `try/catch` in Phase 3; UNIQUE/PK constraints as last line of defense. |
| **One-time TTS migration** | `tts_map.json` renamed to `tts_map.json.migrated` on success; retried on failure. |

## When the seeder runs

The entire seeding block runs **synchronously before `app.Run()`**, inside a single `using` scope that disposes the `AppDbContext`. This means:

- The server does **not** begin accepting requests until seeding completes.
- If a JSON index file is large, the `ReadAllTextAsync` + deserialize + per-entry matching happens on the startup path — first-request latency is unaffected, but process start is.
- Failures in Phase 1 (other than the expected `SqliteException`) and Phase 3 (outside per-entry `try/catch`, e.g. `ReadAllTextAsync` IO errors) will propagate and prevent the application from starting. Phase 2 failures are swallowed with a warning and do not block startup.

## Inspecting seeded data

```bash
sqlite3 src/aclearningutil/aclearningutil.db
> SELECT * FROM LearningContentCategories;
> SELECT Id, CategoryId, NameEnglish, FileUrl FROM LearningContents ORDER BY CategoryId;
> SELECT COUNT(*) FROM TtsMappings;
> -- Confirm the TTS migration file was renamed:
> ls src/aclearningutil/AudioFiles/tts_map.json.migrated
```

## Related documents

- [`docs/design-database.md`](design-database.md) — full database schema, constraints, and the `HasData` seed definition.
- [`docs/design-controllers.md`](design-controllers.md) — the API controllers that read and write the seeded tables at runtime.
