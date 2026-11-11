# Controllers Reference

This document describes every API controller in the **aclearningutil** project, including the HTTP method, route, parameter list (with source — query / route / body), validation rules, and response shape for each endpoint.

All controllers live in [`src/aclearningutil/Controllers/`](../src/aclearningutil/Controllers/).

## Conventions

- **Base path**: each controller uses `[Route("api/[controller]")]`, so the path is `api/<ControllerName>` (controller name without the `Controller` suffix).
- **`[ApiController]`**: all controllers set this, enabling automatic 400 validation responses for model-binding errors and inference of `[FromBody]` / `[FromRoute]`.
- **Authentication**: every controller is decorated with `[Authorize]` **except** `LearningContentCategoriesController`, which is public. Auth uses JWT Bearer tokens issued by the Identity Server (audience `api.knowledgebuilder`).
- **Admin allowlist (`RequireAdmin`)**: the catalog-mutating `LearningContents` `POST`/`PUT`/`DELETE` actions add `[Authorize(Policy="RequireAdmin")]` (`LearningContentsController.cs:58,86,155`). The policy (`Program.cs:100-110`) is enforced by `Authorization/AdminUserAuthorizationHandler` against the `Admin:UserIds` config array — the `ClaimTypes.NameIdentifier`/`"sub"` values permitted to create/update/delete shared `LearningContents`. An authenticated non-admin gets `403 Forbidden`; an empty (or missing) array **fails closed** — nobody is admin. To authorize an operator: register the user in acidserver, look up its `AspNetUsers.Id`, and add it to `Admin:UserIds` in `appsettings.json`. All other endpoints and the `LearningContents` `GET`s need only a plain user token.
- **Rate limiting**: `TTSController`, `FormatLLMController`, and `EnglishLLMController` are decorated with `[EnableRateLimiting("LLMAndTTS")]` — a process-global fixed window of **30 permits per minute** with a queue of **5** (oldest-first); rejections return `429 Too Many Requests` (`Program.cs:115-125`). The three client-triggerable statistics writes (`POST api/UserLoginHistories` §15, `POST api/UserLearningTimes` and `PUT api/UserLearningTimes/Preferences` §18) carry `[EnableRateLimiting("UserWrites")]` — a **per-user** fixed window of **10 permits per minute** partitioned on the token's user id (`RateLimiting/UserWriteRateLimiting.cs`; registered in `Program.cs`). The honest client cadence is ~1 heartbeat/minute/tab, so the ceiling only binds abusive loops — decisive because every accepted write takes SQLite's single global write lock. The two body-reading write routes also set `[RequestSizeLimit(1024)]` → `413` before MVC ever deserializes an oversized payload. The habit-tracking controllers (§9–§14) remain deliberately NOT rate-limited (user-scoped CRUD at personal-use volume), nor are the local-file-read endpoints §16–§17 (`WordAudio`/`WordDictionary`) and `Storage`.
- **User scoping**: `UserLearningRatingsController`, `UserLoginHistoriesController` and `UserLearningTimesController` extract the user from the JWT via `ClaimTypes.NameIdentifier` (with `"sub"` fallback). All read/write operations are scoped to that user; the `UserId` column is always set server-side from the token and never trusted from the request body. The habit-tracking controllers use the same extraction (via `HabitControllerBase.GetUserId()`), always as `OwnerId`.
- **Habit error envelope**: every habit-tracking controller (`Habits`, `HabitItems`, `ItemProperties`, `HabitCriteria`, `HabitPunches`, `SharedHabits`) applies the `[HabitExceptionFilter]` — business failures return RFC 7807 `application/problem+json` with a stable machine-readable `code` in `extensions.code` (full code table in [`design-habit-api.md`](design-habit-api.md) § Errors). Identity gates (`unauthenticated`, 401), tenant misses (`notFound`, 404 — cross-owner access never 403s), and validation failures (422) all travel through this envelope; no habit endpoint returns a bare `NotFound()`/`Unauthorized()` body. The route tables below list status codes in short form; habit routes additionally carry the coded ProblemDetails body on errors.
- **Habit wire format**: habit DTOs are defined in `Models/HabitRequests.cs` / `Models/HabitResponses.cs`; enums travel as the spec's lowercase/snake strings (`"daily"`, `"per_cycle"`, `"union_distinct"`, …) and dates as `yyyy-MM-dd`. Required enums are nullable on the wire — omitting `cycle`/`propertyType`/`criterionType` is a 422 `missingCycle`/`missingPropertyType`/`missingCriterionType`, never a silent default. A non-string or unknown enum value fails at binding with a plain MVC 400 validation ProblemDetails (no `code`).
- **`CancellationToken`**: appears in every action signature but is framework-injected (request-aborted token), not a caller-supplied parameter — it is omitted from the parameter tables below.
- **Pagination**: list endpoints use query parameters `page` (default `1`) and `pageSize` (default `50`, clamped to a max of `200`).

## Request / Response Models (DTOs)

Referenced by the LLM and TTS controllers. Defined in [`src/aclearningutil/Models/`](../src/aclearningutil/Models/).

### `FormatLLMInput` — request body for `FormatLLMController.AskAnything`

| Property | Type | Default | Notes |
|---|---|---|---|
| `FormatType` | string | `"math"` | Must be `"math"`, `"physics"`, or `"chemistry"`. |
| `Context` | string | `""` | Required, max 10000 characters. |

### `LLMReplyContent` — response body from both LLM controllers

| Property | Type | Notes |
|---|---|---|
| `Content` | string | The LLM-generated reply. |

### `AudioFile` — response body from `TTSController.GetTTS`

| Property | Type | Notes |
|---|---|---|
| `AudioFileUrl` | string | Relative URL of the generated WAV file, in the form `audio/<filename>.wav`. |

### Entity bodies

`POST`/`PUT` on the CRUD controllers accept the entity classes directly (`LearningContent`, `UserLearningRating`). Their shapes are documented in [`docs/design-database.md`](design-database.md). Note: server-managed fields (`CreatedAt`, `UpdatedAt`, `UserId`) are ignored or overwritten on write.

---

## 1. `LearningContentCategoriesController`

**File**: [`LearningContentCategoriesController.cs`](../src/aclearningutil/Controllers/LearningContentCategoriesController.cs)
**Route**: `api/LearningContentCategories`
**Auth**: **none** (public)

Read-only listing of the six system-owned content categories.

### `GET api/LearningContentCategories`

List all categories ordered by `Id`.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| — | — | — | — | No parameters. |

**Responses**: `200 OK` → `List<LearningContentCategory>`.

### `GET api/LearningContentCategories/{id}`

Get a single category by ID.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Category ID. |

**Responses**: `200 OK` → `LearningContentCategory`; `404 NotFound` if missing.

---

## 2. `LearningContentsController`

**File**: [`LearningContentsController.cs`](../src/aclearningutil/Controllers/LearningContentsController.cs)
**Route**: `api/LearningContents`
**Auth**: `[Authorize]` for `GET`s; `[Authorize(Policy="RequireAdmin")]` for `POST`/`PUT`/`DELETE` (see Conventions → Admin allowlist)

CRUD for learning content items.

### `GET api/LearningContents`

List content items, optionally filtered by category, with pagination. Ordered by `UpdatedAt` descending. Includes the parent `Category`.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `categoryId` | query | int? | no | — | Filter to a category. |
| `page` | query | int? | no | `1` | Page number (1-based). |
| `pageSize` | query | int? | no | `50` | Page size; clamped to `[1, 200]`. |

**Responses**: `200 OK` → `List<LearningContent>` (with `Category` populated).

### `GET api/LearningContents/{id}`

Get a single content item by ID (includes `Category`).

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Content ID. |

**Responses**: `200 OK` → `LearningContent`; `404 NotFound`.

### `POST api/LearningContents`

Create a new content item.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `content` | body | `LearningContent` | yes | Must include `CategoryId`, `NameChinese`, `NameEnglish`, `FileUrl`. |

**Validation**:
- `NameChinese` and `NameEnglish` must be non-whitespace.
- `FileUrl` must be a relative path, must not contain `..`, and must not be path-rooted.
- `CategoryId` must reference an existing category.

**Side effects**: `CreatedAt` and `UpdatedAt` are set server-side to `DateTime.UtcNow`.
**Responses**: `201 Created` → `LearningContent` (with `Location` header pointing to `GetById`); `400 BadRequest` on validation failure; `403 Forbidden` for authenticated non-admins.

### `PUT api/LearningContents/{id}`

Update an existing content item.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Content ID to update. |
| `content` | body | `LearningContent` | yes | New field values. |

**Validation**: same as `POST` (names, `FileUrl`, `CategoryId`).
**Side effects**: `UpdatedAt` is set to `DateTime.UtcNow`. `CreatedAt` is preserved. (`Version` is also written from the body — see `LearningContentsController.cs:115`.)
**Responses**: `204 NoContent`; `400 BadRequest`; `404 NotFound`; `403 Forbidden` for authenticated non-admins.

### `DELETE api/LearningContents/{id}`

Delete a content item **and** its dependent records.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Content ID to delete. |

**Side effects**: because the FK relationships use `DeleteBehavior.Restrict`, the controller manually removes all `UserLearningRatings` rows referencing this content before deleting it.
**Responses**: `204 NoContent`; `404 NotFound`; `403 Forbidden` for authenticated non-admins.

---

## 3. `UserLearningHistoriesController` — removed (2026-10-25)

The per-item study log (`UserLearningHistories` table: `ContentId`/`ItemId`/`LearnDate`/`SuccessIndicator` rows, full CRUD) was retired without ever gaining a client — the product's per-word signal is `UserLearningRatings` (§4) and the study-usage rollups are `UserLoginHistories` (§15) and `UserLearningTimes` (§18). The entity, `DbSet`, controller, endpoints and tests are deleted; `Program.cs` startup issues `DROP TABLE IF EXISTS "UserLearningHistories"` so leftover legacy rows can never block content deletion through the retired table's `Restrict` FK. The section number stays vacant so §4–§18 cross-references remain valid.

---

## 4. `UserLearningRatingsController`

**File**: [`UserLearningRatingsController.cs`](../src/aclearningutil/Controllers/UserLearningRatingsController.cs)
**Route**: `api/UserLearningRatings`
**Auth**: `[Authorize]` (user-scoped)

CRUD for the current user's content ratings (`Rating` is a byte in 1–5).

### `GET api/UserLearningRatings`

List the current user's ratings, optionally filtered, with pagination. Ordered by `ScoreDate` descending. The parent `Content` is **not** included: the navigation property is marked `[JsonIgnore]` on the entity, so the response carries the rating fields only (`id`, `userId`, `contentId`, `itemId`, `scoreDate`, `rating`) — a client listing the ratings of one content file already knows which content it is, and echoing the row once per rating only multiplied the payload.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `contentId` | query | int? | no | — | Filter to a content item. |
| `itemId` | query | int? | no | — | Filter to a specific item. Applied independently — may be used without `contentId`. |
| `page` | query | int? | no | — | Page number (1-based). **Pagination is applied only when `page` or `pageSize` is supplied** (this endpoint is the one exception to the Pagination convention above); without them the full filtered list is returned, so clients can cache it as the complete rating set. |
| `pageSize` | query | int? | no | — | Page size; clamped to `[1, 200]` (default `50` when paging). |

**Responses**: `200 OK` → `List<UserLearningRating>`; `401 Unauthorized`.

### `GET api/UserLearningRatings/{id}`

Get a single rating belonging to the current user.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Rating record ID. |

**Responses**: `200 OK` → `UserLearningRating`; `401 Unauthorized`; `404 NotFound`.

### `POST api/UserLearningRatings`

Create a rating for the current user.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `rating` | body | `UserLearningRating` | yes | Must include `ContentId` and `Rating` (1–5); optionally `ItemId`, `ScoreDate`. |

**Validation**:
- `Rating` must be between 1 and 5.
- `ContentId` must reference an existing `LearningContent`.

**Side effects**: `UserId` is set from the JWT (body value ignored). `ScoreDate` defaults to `DateTime.Today` if not supplied / equal to `default`.
**Upsert**: `(UserId, ContentId, ItemId)` carries a unique index (`AppDbContext.cs:94-96`). When a row for the same triple already exists, the POST updates its `Rating`/`ScoreDate` and returns `200 OK` with that row instead of creating a duplicate.
**Responses**: `201 Created` → `UserLearningRating` (new row); `200 OK` → `UserLearningRating` (existing triple updated); `400 BadRequest`; `401 Unauthorized`.

### `PUT api/UserLearningRatings/{id}`

Update one of the current user's ratings.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Rating record ID. |
| `rating` | body | `UserLearningRating` | yes | New field values. |

**Validation**: `Rating` in 1–5; `ContentId` must exist.
**Side effects**: updates `ContentId`, `ItemId`, `ScoreDate`, `Rating`. `UserId` is unchanged.
**Responses**: `204 NoContent`; `400 BadRequest`; `401 Unauthorized`; `404 NotFound`.

### `DELETE api/UserLearningRatings/{id}`

Delete one of the current user's ratings.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Rating record ID. |

**Responses**: `204 NoContent`; `401 Unauthorized`; `404 NotFound`.

---

## 5. `TTSController`

**File**: [`TTSController.cs`](../src/aclearningutil/Controllers/TTSController.cs)
**Route**: `api/TTS`
**Auth**: `[Authorize]`, rate-limited (`LLMAndTTS`)

Text-to-Speech with database + file caching. Generates a WAV via the Aliyun TTS API and caches the sentence → file mapping.

### `GET api/TTS/details`

Generate (or return cached) audio for a sentence.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `sentence` | query | string | yes | The text to synthesize; max 1000 characters. |

**Behavior**:
- Looks up `sentence` in `TtsMappings`. On a hit, returns the cached file URL.
- On a miss, calls the Aliyun TTS endpoint (16 kHz WAV), writes the file to `AudioFiles/`, inserts a `TtsMappings` row, and returns the URL.
- A per-sentence `SemaphoreSlim` serializes concurrent requests for the same sentence. If a unique-constraint race still occurs (SQLite error 19), the orphaned file is cleaned up and the existing mapping is returned.

**Responses**:
- `200 OK` → `AudioFile` (`{ "AudioFileUrl": "audio/<guid>.wav" }`).
- `400 Bad Request` — empty sentence, or sentence longer than 1000 chars (plain-text body).
- `502 Bad Gateway` — Aliyun TTS returned a non-audio response (plain-text body with upstream error).
- `500 Internal Server Error` — DB save failure or missing file (plain-text body).

---

## 6. `FormatLLMController`

**File**: [`FormatLLMController.cs`](../src/aclearningutil/Controllers/FormatLLMController.cs)
**Route**: `api/FormatLLM`
**Auth**: `[Authorize]`, rate-limited (`LLMAndTTS`)

Subject-specific LLM Q&A (math / physics / chemistry) backed by the DeepSeek API.

### `POST api/FormatLLM/AskAnything`

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `input` | body | `FormatLLMInput` | yes | See DTO table above. |

**Body shape**:
```json
{
  "FormatType": "math",
  "Context": "Explain the quadratic formula."
}
```

**Validation**:
- `Context` required, max 10000 characters.
- `FormatType` must be `"math"`, `"physics"`, or `"chemistry"` (each maps to a Chinese system prompt: 数学/物理/化学 teacher persona).

**Responses**:
- `200 OK` → `LLMReplyContent` (`{ "Content": "..." }`).
- `400 Bad Request` — empty/over-long context, or invalid `FormatType`.
- `500 Internal Server Error` — DeepSeek API key not configured.
- `502 Bad Gateway` — upstream LLM error.

---

## 7. `EnglishLLMController`

**File**: [`EnglishLLMController.cs`](../src/aclearningutil/Controllers/EnglishLLMController.cs)
**Route**: `api/EnglishLLM`
**Auth**: `[Authorize]`, rate-limited (`LLMAndTTS`)

English-language learning assistant backed by the DeepSeek API. Uses a fixed system prompt ("English teacher for Junior and Senior High School students").

### `GET api/EnglishLLM/details`

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `context` | query | string | yes | The question/prompt; max 10000 characters. |

**Responses**:
- `200 OK` → `LLMReplyContent` (`{ "Content": "..." }`).
- `400 Bad Request` — empty/over-long context.
- `500 Internal Server Error` — DeepSeek API key not configured.
- `502 Bad Gateway` — upstream LLM error.

---

## 8. `StorageController`

**File**: [`StorageController.cs`](../src/aclearningutil/Controllers/StorageController.cs)
**Route**: `api/Storage/{**filepath}` (catch-all route segment)
**Auth**: `[Authorize]`

Serves static learning-content files from the `Storage/` folder with authentication and path-traversal protection.

### `GET api/Storage/{subfolder}/{filename}`

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `filepath` | route (catch-all) | string | yes | Format `subfolder/filename`, e.g. `knowledge-exercises/data.json`. |

**Validation / safety**:
- Path is normalized (`\` → `/`, trimmed); requests containing `..` or `//` are rejected.
- Must split into exactly `subfolder/filename`.
- `subfolder` must be in the allow-list: `learnenglish`, `learnchinese`, `knowledge-exercises`, `englishlistening`, `formula`.
- File extension must be in the allow-list: `.json`, `.png`, `.jpg`, `.jpeg`, `.mp3`.
- The path is resolved segment-by-segment against the actual `Storage/` entries: an exact match wins, otherwise each segment is matched case-insensitively — so `LEARNENGLISH/Words.JSON` serves `learnenglish/words.json` on case-sensitive file systems (Linux CI) as well as Windows. A final containment check confirms the resolved path stays inside `Storage/`.
- Response carries `Cache-Control: public, max-age=3600` (1 hour).

**Responses**:
- `200 OK` → `FileResult` with content type (`application/json`, `image/png`, `image/jpeg`, `audio/mpeg`, or `application/octet-stream`).
- `400 Bad Request` — missing/invalid path, disallowed subfolder or extension, or traversal attempt.
- `404 NotFound` — file does not exist.

### Word references (`learnenglish/word_references/`)

Per-word bilingual example-sentence files live at
`Storage/learnenglish/word_references/<word>.json` — one JSON array per word,
each entry `{ "ent_sent", "chn_sent", "source" }`:

```json
[
  { "ent_sent": "A chill stole over her body.", "chn_sent": "她突然感到浑身发冷。", "source": "《牛津词典》" }
]
```

They are served by the same catch-all route above — **no separate endpoint
exists**:

```
GET api/Storage/learnenglish/word_references/chill.json
```

Because the route splits only on the first `/` (`Split('/', 2)`), `learnenglish`
is the validated subfolder and `word_references/chill.json` is treated as the
filename (extension `.json` passes the allow-list). Notes:

- The `learnenglish/` prefix is **required**. `api/Storage/word_references/...`
  is rejected with `400` because `word_references` is not an allow-listed
  subfolder.
- These files are **not** listed in `words.json` / `sentences.json`, so the
  startup sync in `Program.cs` creates **no `LearningContents` rows** for them.
  They do not appear in the Category 1 (Vocabulary) listing, and there is no
  index/manifest endpoint enumerating which words have references — callers must
  build the URL from the word (filename stem, lowercased).
- Auth and caching are identical to other Storage files (JWT required;
  `Cache-Control: public, max-age=3600`).

The files are generated by `fetch_word_references.py` in the
`knowledgebuilder-content` repo; see
[`knowledgebuilder-content/util/how-to-use.md`](../../knowledgebuilder-content/util/how-to-use.md)
for the content format and generation workflow.

For new consumers prefer §17 `WordDictionaryController` (`GET api/WordDictionary?word=`):
it reads the example sentences from `words.db` (`examples`) by word, so phrases,
case and spelling variants need no filename-stem conventions. These JSON files
remain served as-is (no current UI consumer).

---

# Habit-Tracking Controllers (§9–§14)

Sections 9–14 document the habit-tracking surface. Full behavioral design (data model, evaluation semantics, error-code table) lives in [`design-habit-api.md`](design-habit-api.md); this chapter is the route-by-route reference derived from the controllers. Common traits:

- All six controllers derive from `HabitControllerBase` (user-id extraction from the JWT with `ClaimTypes.NameIdentifier` → `"sub"` fallback; tenant-scoped habit loading).
- All require authentication (`[Authorize]`); a valid token *without* a user-id claim gets `401` + `code=unauthenticated`.
- A missing resource **or** a resource owned by another user both return `404` + `code=notFound` (cross-user existence is never revealed); business-rule failures return `422` with the `extensions.code` named in each table.
- `CancellationToken` parameters are framework-injected and omitted from the tables, as elsewhere.
- The invitation picker's user search is an **external dependency** (`acidserver`'s `GET {idServer}/api/users/search?q=`); it is not an endpoint of this API and is not documented here beyond the fact that the stored `granteeUserId` is the `userId` it returns.

## 9. `HabitsController`

**File**: [`HabitsController.cs`](../src/aclearningutil/Controllers/HabitsController.cs)
**Route**: `api/Habits`
**Auth**: `[Authorize]` (owner-scoped)

Habit CRUD, deactivation, owner-managed share invitations, and per-day history. Every response embeds current-cycle `progress`.

### `GET api/Habits`

List the caller's habits (ordered by `Id`) with embedded progress.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| — | — | — | — | No parameters. |

**Responses**: `200` → `List<HabitOutDto>`; `401 unauthenticated`.

### `GET api/Habits/{habitId}`

Single habit with current-cycle progress.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `habitId` | route | int | yes | Habit ID. |

**Responses**: `200` → `HabitOutDto` (includes `deactivatedDate`, `yyyy-MM-dd` or null); `404 notFound` (missing or other user's).

### `POST api/Habits`

Create habit + items + properties + criteria atomically (name-based cross-references resolve within the payload).

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `dto` | body | `HabitCreate` | yes | `name`, `description?`, `cycle` (required, nullable wire field), `startDate`, `endDate?`, `items` (1–100, each ≥1 property ≤100), `criteria` (1–100, exactly one `isRoot` — zero roots fails `noRootCriterion`). |

**Validation**: `invalidName`/`validationTooLong` (name ≤500; description ≤2000); `invalidDateRange`; `missingCycle`; `noItems`/`noCriteria`/`tooManyEntries`; per item: `itemWithoutProperties`, `missingPropertyType`, `invalidPropertyType` (unknown member / name+id collision / habit-wide same-name⇒same-type), `invalidBaseRate` (non-numeric or ≤0/non-finite), `invalidItemUniqueness` (required for list, forbidden otherwise), `duplicateName` (item and property names); per criterion: `missingCriterionType`, `duplicateName`, `duplicateReference` (repeated scope-item / operand names), `tooManyEntries` (reference lists ≤100), condition: `unknownOperandName` (unresolvable property/scope names; subset scope missing the bound property), `invalidThreshold` (missing/≤0/non-finite; integer for boolean), composite: `invalidTarget` (missing operator), `invalidOperandCount` (NOT=1, AND/OR≥2); root: `invalidTarget`/`missingCycleTarget` per success-type rules; graph: `circularCriterion`. Any rejection writes nothing.

**Responses**: `201` → `HabitOutDto` (Location `GetById`); `401`; `422` with the codes above.

### `PUT api/Habits/{habitId}`

Update basic fields. `state` is not editable here.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `habitId` | route | int | yes | Habit ID. |
| `dto` | body | `HabitUpdate` | yes | `name`, `description?`, `cycle` (required), `startDate`, `endDate?`. |

**Validation**: same field rules as create; `structuralChangeBlocked` when `cycle` changes while any punch exists. **Responses**: `200` → `HabitOutDto`; `404`; `401`; `422`.

### `POST api/Habits/{habitId}/Deactivate`

Irreversible deactivation; records `deactivatedDate` (server's today) and thereafter history ends at that day.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `habitId` | route | int | yes | Habit ID. |

**Responses**: `200` → `HabitOutDto`; `422 alreadyInactive`; `404`; `401`.

### `GET api/Habits/{habitId}/Shares`

Owner-facing invitation list.

**Responses**: `200` → `List<ShareGrantOut>` (`id`, `granteeUserId`, `granteeUserName`, `createdAt`); `404` (non-owner); `401`.

### `POST api/Habits/{habitId}/Shares`

Invite one user (immediate, no accept step). Duplicate pre-check + insert run in one `Serializable` transaction.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `habitId` | route | int | yes | Habit ID. |
| `dto` | body | `ShareGrantCreate` | yes | `granteeUserId` (trimmed, ≤200 chars — acidserver user id), `granteeUserName` (trimmed, ≤500). |

**Validation**: `invalidGrantee` (blank, or self-invite — compares the trimmed grantee to the trimmed token id); `validationTooLong`; `missingShareOwnerName` (caller token lacks a `name` claim); `duplicateName` (already invited — also the unique-index race mapping).

**Responses**: `201` → `ShareGrantOut` (Location `GetShares`); `404` (non-owner); `401`; `422`.

### `DELETE api/Habits/{habitId}/Shares/{grantId}`

Revoke an invitation (re-privatizes instantly).

**Responses**: `204`; `404 notFound` (grant not under this habit — foreign `grantId` under a valid habit also 404, no leak); `401`.

### `DELETE api/Habits/{habitId}`

Delete habit + full graph (items, properties, criteria + details, punches + values, share grants).

**Responses**: `204`; `404 notFound` (ProblemDetails, not a bare body); `401`.

### `GET api/Habits/{habitId}/History`

Per-day history (newest-first). Every day of range ∩ active window up to today gets a row — punch-less days included with their empty-data outcome; the range end additionally clamps at `deactivatedDate − 1` for deactivated habits. Cumulative rows evaluate from the cycle start even when `from` is mid-cycle, and `isSuccessful` latches once the cycle has passed.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `habitId` | route | int | yes | Habit ID. |
| `from` | query | DateOnly? | no | `yyyy-MM-dd`, inclusive; defaults to the habit start. |
| `to` | query | DateOnly? | no | `yyyy-MM-dd`, inclusive; clamped to the window end / today. |

**Responses**: `200` → `List<DayHistoryOut>`; `422 invalidDateRange` (`from > to`); `404`; `401`.

---

## 10. `HabitItemsController`

**File**: [`HabitItemsController.cs`](../src/aclearningutil/Controllers/HabitItemsController.cs)
**Route**: `api/Habits/{habitId}/Items`
**Auth**: `[Authorize]` (owner-scoped)

Items within a habit. Adding items is always permitted (even with punch history); removal is blocked while punches reference the item.

| Method / Path | Success | Failure codes | Notes |
|---|---|---|---|
| `GET api/Habits/{habitId}/Items` | `200` → `List<ItemOut>` | `404`, `401` | Properties carry `currentCycleValue`/`todayValue` aggregates; `hasPunches` batched per item. |
| `GET api/Habits/{habitId}/Items/{itemId}` | `200` → `ItemOut` | `404`, `401` | |
| `POST api/Habits/{habitId}/Items` | `201` → `ItemOut` | `422` `invalidName`/`validationTooLong`, `itemWithoutProperties`, `tooManyEntries` (≤100 properties), `missingPropertyType`, `invalidPropertyType` (member / same-name⇒same-type vs habit), `invalidBaseRate`, `invalidItemUniqueness`, `duplicateName` (item name; duplicate property name within the payload) | |
| `PUT api/Habits/{habitId}/Items/{itemId}` | `200` → `ItemOut` | `422` `invalidName`/`validationTooLong`, `duplicateName`; `404`, `401` | Renames + reorder only; property definitions untouched. |
| `DELETE api/Habits/{habitId}/Items/{itemId}` | `204` | `422 itemHasPunches`; `404`, `401` | Subset-scope membership rows shrink away with the item (criteria bind names and survive). |

---

## 11. `ItemPropertiesController`

**File**: [`ItemPropertiesController.cs`](../src/aclearningutil/Controllers/ItemPropertiesController.cs)
**Route**: `api/Habits/{habitId}/Items/{itemId}/Properties`
**Auth**: `[Authorize]` (owner-scoped)

Property definitions on an item. `propertyType` and `itemUniqueness` are immutable after creation (absent from the update payload).

| Method / Path | Success | Failure codes | Notes |
|---|---|---|---|
| `POST …/Properties` | `201` → `PropertyOut` | `422` `invalidName`/`validationTooLong` (name ≤500), `missingPropertyType` (omitted wire field — never a silent `boolean`), `invalidPropertyType` (unknown member; same-name⇒same-type across the habit), `invalidBaseRate` (non-numeric / ≤0 / non-finite), `invalidItemUniqueness` (required for list, forbidden otherwise; unknown member), `duplicateName` (within item) | `404` unknown habit/item. |
| `GET …/Properties/{propertyId}` | `200` → `PropertyOut` | `404`, `401` | With current aggregates. |
| `PUT …/Properties/{propertyId}` | `200` → `PropertyOut` | `422` `invalidName`/`validationTooLong`, `duplicateName` (within item), `invalidPropertyType` (rename collides with a different-typed same name), `invalidBaseRate` (non-numeric / ≤0 / non-finite) | `404`. |
| `DELETE …/Properties/{propertyId}` | `204` | `422 itemHasPunches`; `404`, `401` | Criteria bind property NAMES — a referenced-by-name property is deletable; the criterion simply stops passing. |

---

## 12. `HabitCriteriaController`

**File**: [`HabitCriteriaController.cs`](../src/aclearningutil/Controllers/HabitCriteriaController.cs)
**Route**: `api/Habits/{habitId}/Criteria`
**Auth**: `[Authorize]` (owner-scoped)

Success criteria. Standalone requests reference scope items/operands by **id** (name-based refs belong to the `POST api/Habits` payload only); conditions always bind the property by **name**.

| Method / Path | Success | Failure codes | Notes |
|---|---|---|---|
| `GET api/Habits/{habitId}/Criteria` | `200` → `List<CriterionOut>` | `404`, `401` | Full stored definitions. |
| `GET …/Criteria/{criterionId}` | `200` → `CriterionOut` | `404`, `401` | |
| `POST …/Criteria` | `201` → `CriterionOut` | `422` `invalidName`/`validationTooLong`, `missingCriterionType`, `invalidPropertyType` (unknown member; name-based refs supplied here; condition with composite fields; aggregation mode invalid for the property's type), `duplicateName` (within habit), `scopeItemsRequired`, `duplicateReference` (repeated `scopeItemIds`/`operandCriterionIds`), `tooManyEntries` (reference lists ≤100), `invalidThreshold` (≤0/non-finite; integer for boolean), `invalidTarget` (missing operator / root rules), `invalidOperandCount`, `unknownOperandName` (subset scope lacks the bound property), `circularCriterion` not applicable (new id, no incoming edges) | `404` unknown property / item / operand id. `isRoot: true` atomically demotes the previous root (clearing its `successType`/`cycleTarget`). |
| `PUT …/Criteria/{criterionId}` | `200` → `CriterionOut` | `422` same validation family, plus `rootRequired` (clearing the root without designating another), `circularCriterion` (operand overlay re-checked against the habit graph) | `criterionType` is immutable by construction (not in the payload); 404. |
| `DELETE …/Criteria/{criterionId}` | `204` | `422` `rootCriterionProtected`, `criterionInUse` (referenced as an operand); `404`, `401` | Removes the condition/composite detail rows with it. |

---

## 13. `HabitPunchesController`

**File**: [`HabitPunchesController.cs`](../src/aclearningutil/Controllers/HabitPunchesController.cs)
**Route**: `api/Habits` (punch sub-resources)
**Auth**: `[Authorize]` (owner-scoped)

Punch sessions. Creation gates on habit state + the **punch date**'s window (FR-3.3); edits/deletes are exempt (FR-3.4). Boolean values upsert the day's single row on both create and update; the same-day read-then-write runs inside one `IsolationLevel.Serializable` (`BEGIN IMMEDIATE`) transaction.

| Method / Path | Success | Failure codes | Notes |
|---|---|---|---|
| `GET api/Habits/{habitId}/Punches?from=&to=` | `200` → `List<PunchOut>` | `422 invalidDateRange`; `404`, `401` | Habit-scoped aggregate across items; `punchDate` desc then `punchedAt` desc. |
| `GET api/Habits/{habitId}/Items/{itemId}/Punches?from=&to=` | `200` → `List<PunchOut>` | `422 invalidDateRange`; `404`, `401` | |
| `GET …/Punches/{punchId}` | `200` → `PunchOut` | `404`, `401` | |
| `POST api/Habits/{habitId}/Items/{itemId}/Punches` | `201` → `PunchOut` | `422` `habitInactive`; `outOfWindow` (future date / date outside `[startDate, endDate?]`); `invalidPropertyType` (empty values; duplicate property in one session; type/value mismatch; numeric ≤0/non-finite; blank/empty list entries; >100 values or >100 entries → `tooManyEntries`; entry >1000 chars → `validationTooLong`); `duplicateEntry` (`per_day`/`per_cycle` uniqueness — whole session rejected atomically); `invalidDateRange` n/a | `404` unknown habit/item/property (a property of another item counts as missing). Optional body `punchDate` back-fills a past in-window day; uniqueness/unchecked windows follow the punched day. |
| `PUT …/Punches/{punchId}` | `200` → `PunchOut` | `422` same value/uniqueness family (`per_cycle` re-check excludes the edited punch; booleans upsert the day's row from any session) | `404`. State/window gates do not apply. |
| `DELETE …/Punches/{punchId}` | `204` | `404`, `401` | Cascade-removes the value rows; state/window gates do not apply. |

---

## 14. `SharedHabitsController`

**File**: [`SharedHabitsController.cs`](../src/aclearningutil/Controllers/SharedHabitsController.cs)
**Route**: `api/SharedHabits`
**Auth**: `[Authorize]` (FR-6 exception: **read-only**, limited to explicitly invited users; the owner may also use these routes on their own habits)

The "shared with me" surface. Non-invited ids and unknown ids are indistinguishable `404 notFound`; owner identity only ever appears as the `ownerName` snapshot (never `OwnerId`). There are no write endpoints here.

| Method / Path | Success | Failure codes | Notes |
|---|---|---|---|
| `GET api/SharedHabits` | `200` → `List<SharedHabitOut>` (`{ habit, ownerName }`) | `401` | Only habits with a `HabitShareGrant` for the caller; empty list when none. |
| `GET api/SharedHabits/{habitId}` | `200` → `{ habit, ownerName, items, criteria }` | `404`, `401` | Items/properties/criteria computed for the viewer's request date; `habit.deactivatedDate` mirrors the owner surface. |
| `GET api/SharedHabits/{habitId}/History?from=&to=` | `200` → `List<DayHistoryOut>` | `422 invalidDateRange`; `404`, `401` | Identical granularity to the owner's History (full punch values visible), same cycle-start fact loading and deactivation clamping. |

---

## 15. `UserLoginHistoriesController`

**File**: [`UserLoginHistoriesController.cs`](../src/aclearningutil/Controllers/UserLoginHistoriesController.cs)
**Route**: `api/UserLoginHistories`
**Auth**: `[Authorize]` (user-scoped), POST rate-limited (`UserWrites`)

The caller's own login history: **one row per user per server-local calendar day**, upserted when the SPA reports a completed OIDC sign-in. There is deliberately no `PUT`/`DELETE` and no by-id route — login truth must not be client-mutable.

DTO: `UserLoginHistoryDto` — `{ loginDate (yyyy-MM-dd), firstLoginAt, lastLoginAt (UTC, 'Z'-suffixed), loginCount }` (`loginCount` counts DEDUPLICATED sign-ins, see POST below). POST takes **no body**: the user comes from the token and the clock from the server.

### `POST api/UserLoginHistories`

Upsert today's row: no row yet → insert with `firstLoginAt = lastLoginAt = now`, `loginCount = 1`; row exists → if `now − lastLoginAt` is at least the **5-minute dedup window** (`LoginDedupWindowSeconds = 300`), set `lastLoginAt = now` and `loginCount += 1`; within the window the POST is a **duplicate report** and the row is left untouched (still `200`, echoing the unchanged values) — so a scripted loop can neither mint `loginCount` nor walk `lastLoginAt` forward, and clock-skewed future stored instants never count either (review finding H3). `loginCount` therefore means "sign-ins at least 5 minutes apart on that day": a self-scoped cosmetic stat (rendered only on the caller's own user-detail page), whose scripted ceiling is 288/day before the `UserWrites` limiter even starts rejecting. The read-then-write runs inside one `Serializable` transaction (SQLite `BEGIN IMMEDIATE` — the write lock is taken before the read, so concurrent first-login POSTs can neither double-insert against the `(UserId, LoginDate)` unique index nor lose an increment; same mechanism as `HabitPunchesController.Create`).

Per-user rate-limited (`UserWrites`, 10/min): the honest cadence is one call per interactive sign-in, and every call takes the database's global write lock — unlimited POSTs are a write-lock weapon (review finding H2).

**Responses**: `200 OK` → `UserLoginHistoryDto` (always 200 — the row's identity is (user, day), not a new id; duplicate reports within the dedup window are 200 with unchanged values); `401 Unauthorized` if no user ID in token; `429 Too Many Requests` over the per-user window.

### `GET api/UserLoginHistories?from=&to=`

List the caller's days, ordered by `LoginDate` descending.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `from` | query | DateOnly? | no | `to - 89 days` | Inclusive start of the window. |
| `to` | query | DateOnly? | no | server-local today | Inclusive end. Default window = trailing 90 days. |

**Validation**: `from > to` → `400 BadRequest`.
**Responses**: `200 OK` → `List<UserLoginHistoryDto>`; `401 Unauthorized`.

---

## 16. `WordAudioController`

**File**: [`WordAudioController.cs`](../src/aclearningutil/Controllers/WordAudioController.cs)
**Route**: `api/WordAudio`
**Auth**: `[Authorize]`

Serves per-word pronunciation audio looked up in `Storage/words.db` and streamed
from `Storage/wordaudio/`. The UI vocabulary study/typing/dictation modes call
this instead of browser TTS for words that have recorded audio, falling back to
`speechSynthesis` on `404`. Not rate-limited - it is a local file read with no
external-API spend, unlike `TTSController`.

### `GET api/WordAudio`

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `word` | query | string | yes | The word/phrase to look up, e.g. `hello` or `add ... to ...`. Case-insensitive (`COLLATE NOCASE`). |

**Behavior**:
- Opens `Storage/words.db` read-only and runs `SELECT us_audio, uk_audio FROM words WHERE word = @word COLLATE NOCASE LIMIT 1`.
- Prefers `us_audio`; falls back to `uk_audio` when US is absent. (Today all rows carry only US audio.)
- The DB stores a relative path like `wordaudio/France.f5cd26.mp3`; only the basename is used and must end in `.mp3`. A `Path.GetFullPath` check confirms the resolved path stays inside `Storage/wordaudio/`.
- Response carries `Cache-Control: public, max-age=2592000` (30 days).

**Responses**:
- `200 OK` -> `FileResult` (`audio/mpeg`).
- `404 NotFound` - blank word; `words.db` missing; word unknown; word has no audio; or the audio file is absent on disk.

`words.db` and `wordaudio/` are authored by `fetch_word.py` in the
`knowledgebuilder-content` repo and copied into `Storage/` at deploy time by
`publish-learning-all.ps1` (see
[`knowledgebuilder-content/util/how-to-use.md`](../../knowledgebuilder-content/util/how-to-use.md)).
In dev, populate `src/aclearningutil/Storage/` with `words.db` and `wordaudio/`
copied from `knowledgebuilder-content/util/` - the `Storage/` folder is
gitignored and populated manually (same convention as the five content folders).

---

## 17. `WordDictionaryController`

**File**: [`WordDictionaryController.cs`](../src/aclearningutil/Controllers/WordDictionaryController.cs)
**Route**: `api/WordDictionary`
**Auth**: `[Authorize]`

Serves the dictionary detail of one word - Chinese definitions and bilingual
example sentences - read from `Storage/words.db`, the same external read-only
database as §16. The UI vocabulary review screen renders both lists below the
word's gloss (gated by its hide-explanation setting); on `404` the sections
stay hidden. Not rate-limited - a local file read with no external-API spend.

### `GET api/WordDictionary`

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `word` | query | string | yes | The word/phrase to look up, e.g. `hello` or `at present`. Case-insensitive (`COLLATE NOCASE`); spelling variants resolve through `words.base_word` to the base spelling whose row carries the merged detail. |

**Behavior**:
- Resolves the request to the canonical DB key via `SELECT COALESCE(base_word, word) FROM words WHERE word = @word COLLATE NOCASE`; no row (or blank `word`, or missing `words.db`) → `404`.
- Reads definitions with `SELECT part_of_speech, meaning FROM definitions WHERE word = @key ORDER BY seq, rowid` and examples with `SELECT source, translation FROM examples WHERE word = @key ORDER BY seq, rowid LIMIT 10` (`seq, rowid` because the base-word merge stamps deduplicated rows with `seq = -1`; the cap bounds the 77-sentence outliers).
- `partOfSpeech` can be `""` - phrase glosses carry no POS label; the UI hides the chip.
- The response echoes the resolved canonical spelling in `word` (differs from the request only for variants).
- Response carries `Cache-Control: public, max-age=2592000` (30 days, same TTL as the audio).

**Responses**:
- `200 OK` -> `WordDictionaryDto` (`{ word, definitions[], examples[] }`; a known word with no children answers empty lists, not `404`).
- `404 NotFound` - blank word; `words.db` missing; or the word has no row in `words`.

Same provenance as §16 (`fetch_word.py` in `knowledgebuilder-content`, deployed
by `publish-learning-all.ps1`). This endpoint supersedes the static
`learnenglish/word_references/<word>.json` files of §8 for UI consumers:
`words.db` is keyed by the word, not by a sanitized filename, so phrases and
case need no client-side stem logic.

---

## 18. `UserLearningTimesController`

**File**: [`UserLearningTimesController.cs`](../src/aclearningutil/Controllers/UserLearningTimesController.cs)
**Route**: `api/UserLearningTimes`
**Auth**: `[Authorize]` (user-scoped), POST/PUT-Preferences rate-limited (`UserWrites`)

The caller's accumulated learning time: **one row per user per server-local calendar day per content category**, upserted by the SPA's 60-second heartbeat (fired only while an exercise page is visible and the user is active — see `UserLearningTimeService` in knowledgebuilder). There is deliberately no `PUT`/`DELETE` and no by-id route — learning time must not be client-mutable. The two write routes are per-user rate-limited (`UserWrites`, 10/min; honest cadence ~1/min/tab) and body-capped at 1 KB (`413`) — same rationale as §15: every accepted beat takes SQLite's global write lock (review finding H2).

DTO: `LearningTimeDto` — `{ learnDate (yyyy-MM-dd), categoryId (1-6), learningSeconds, lastBeatAt (UTC, 'Z'-suffixed) }`. POST body: `LearningTimeBeatRequest` — `{ categoryId, userName? }`; the user comes from the token and every second from the server clock, so the client cannot forge a duration. `userName` is the sender's own display name for the leaderboard: trimmed, clamped to 200, stored only against the CALLER's rows (last non-empty beat wins) — a sender can at worst mislabel itself.

### `POST api/UserLearningTimes`

Upsert today's row for the posted category. The credit is **server-measured against a SHARED per-user-per-day beat cursor** — the latest accepted beat across ALL of the day's category rows (`MAX(lastBeatAt)` of the user/day rows, at most 6, read from the same locked snapshot as the row to upsert): the day has no rows → `learningSeconds = 60` (the day's FIRST beat bootstraps the minute the heartbeat loop had already been watching); otherwise add `min(round(now − cursor), 75)` seconds and move the beaten row's `lastBeatAt = now` (which moves the cursor). Negative elapsed (clock skew) and sub-second gaps credit 0, so replayed, early or multi-tab beats add ~nothing; a resumed-after-idle gap is capped at 75s because the idle minutes were never verified as learning.

Crediting off the day cursor rather than the category row's own `lastBeatAt` is what bounds the **day total** (the sum across categories) by real elapsed time: a client looping all six categories earns the one true gap in total — not six — and the 60-second bootstrap can be collected only once per day, not once per category (review finding H1; per-row crediting allowed ≈6× inflation of leaderboard/detail totals). Per-category attribution is unchanged: each credit lands on the category the sender beats, so honest multi-category study still splits correctly.

Like §15, the read-then-write runs in one `Serializable` transaction (SQLite `BEGIN IMMEDIATE`), guarding the `(UserId, LearnDate, CategoryId)` unique index and losing no increments under concurrency.

**Validation**: `categoryId` outside 1-6 → `400 BadRequest`.
**Responses**: `200 OK` → `LearningTimeDto` (always 200 — the row's identity is (user, day, category), not a new id); `401 Unauthorized` if no user ID in token; `429 Too Many Requests` over the per-user `UserWrites` window; `413` for bodies over 1 KB.

### `GET api/UserLearningTimes?from=&to=&categoryId=`

List the caller's rows, ordered by `LearnDate` descending then `CategoryId` ascending.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `from` | query | DateOnly? | no | `to - 89 days` | Inclusive start of the window. |
| `to` | query | DateOnly? | no | server-local today | Inclusive end. Default window = trailing 90 days. |
| `categoryId` | query | int? | no | all | Restrict to one category (1-6). |

**Validation**: `from > to` → `400 BadRequest`; `categoryId` outside 1-6 → `400 BadRequest`.
**Responses**: `200 OK` → `List<LearningTimeDto>`; `401 Unauthorized`.

### `GET api/UserLearningTimes/Leaderboard?window=7&top=3`

The site's first deliberate cross-user surface: the top learners over a rolling trailing window of `window` server-local days (inclusive of today), ranked by summed `learningSeconds`.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `window` | query | int? | no | `7` | Trailing days, clamped to `[1, 92]`. |
| `top` | query | int? | no | `3` | Entries returned, clamped to `[1, 10]`. |

Grouped per user; ordered `TotalSeconds` descending with `UserId` ascending as the stable tie-break. Each entry is `{ userId, userName, totalSeconds }` — `userName` is the display name stored via the heartbeat (MAX over the window's rows keeps it deterministic when they disagree), falling back to the first 8 characters of the id when none was ever sent. No emails, no per-day breakdown, no category detail. Requires a signed-in caller (`[Authorize]`) but is NOT owner-scoped by design; `knowledgebuilder`'s homepage surfaces it only while authenticated.

**Responses**: `200 OK` → `List<LearningTimeLeaderboardEntry>` (empty list when nobody has beaten yet); `401 Unauthorized`.

### `GET` / `PUT api/UserLearningTimes/Preferences`

The caller's leaderboard visibility choice — the opt-out companion to the cross-user Leaderboard. Backed by `LeaderboardPreferences` (one optional row per user; **absence means visible**). Opting out hides the caller's totals from the leaderboard only: their beats keep accumulating and their own learning-time card is unaffected, and an opted-out user still sees everyone else's board.

- `GET` → `LearningTimePreferencesDto` (`{ leaderboardOptOut }`, defaulting to `false` with no row).
- `PUT` accepts the same DTO and upserts it (Serializable transaction, mirroring the beat handler; one row per user, every PUT updates it). The response echoes the stored value. Shares the beat's per-user `UserWrites` limiter (10/min) and 1 KB body cap — it takes the same global write lock. `401 Unauthorized` with no user ID in token; `429`/`413` as on POST.

The Leaderboard query above excludes opted-out users via a `NOT IN` sub-query on that table.

---

## Endpoint Summary

| Method | Route | Auth | Rate-limited | Description |
|---|---|---|---|---|
| GET | `api/LearningContentCategories` | no | no | List categories |
| GET | `api/LearningContentCategories/{id}` | no | no | Get category |
| GET | `api/LearningContents` | yes | no | List content (filter + paging) |
| GET | `api/LearningContents/{id}` | yes | no | Get content |
| POST | `api/LearningContents` | yes (admin) | no | Create content |
| PUT | `api/LearningContents/{id}` | yes (admin) | no | Update content |
| DELETE | `api/LearningContents/{id}` | yes (admin) | no | Delete content (+ dependents) |
| GET | `api/UserLearningRatings` | yes (user) | no | List my ratings |
| GET | `api/UserLearningRatings/{id}` | yes (user) | no | Get my rating |
| POST | `api/UserLearningRatings` | yes (user) | no | Create rating |
| PUT | `api/UserLearningRatings/{id}` | yes (user) | no | Update rating |
| DELETE | `api/UserLearningRatings/{id}` | yes (user) | no | Delete rating |
| GET | `api/TTS/details` | yes | yes | Text-to-Speech (cached) |
| POST | `api/FormatLLM/AskAnything` | yes | yes | Subject LLM Q&A |
| GET | `api/EnglishLLM/details` | yes | yes | English LLM Q&A |
| GET | `api/Storage/{subfolder}/{filename}` | yes | no | Serve Storage file |
| POST | `api/UserLearningTimes` | yes (user) | yes (user) | Learning-time heartbeat (server-credited, shared day cursor) |
| GET | `api/UserLearningTimes` | yes (user) | no | List my learning time (day×category) |
| GET | `api/UserLearningTimes/Leaderboard` | yes | no | Top learners over a trailing window |
| GET | `api/UserLearningTimes/Preferences` | yes (user) | no | Leaderboard visibility choice |
| PUT | `api/UserLearningTimes/Preferences` | yes (user) | yes (user) | Upsert visibility choice |
| GET | `api/WordAudio` | yes | no | Per-word pronunciation audio (words.db) |
| GET | `api/WordDictionary` | yes | no | Per-word definitions + examples (words.db) |
| GET | `api/Habits` | yes (owner) | no | List my habits (+progress) |
| GET | `api/Habits/{habitId}` | yes (owner) | no | Get habit |
| POST | `api/Habits` | yes (owner) | no | Create habit + items + criteria |
| PUT | `api/Habits/{habitId}` | yes (owner) | no | Update habit basics |
| POST | `api/Habits/{habitId}/Deactivate` | yes (owner) | no | Deactivate (irreversible) |
| GET | `api/Habits/{habitId}/Shares` | yes (owner) | no | List invitations |
| POST | `api/Habits/{habitId}/Shares` | yes (owner) | no | Invite one user |
| DELETE | `api/Habits/{habitId}/Shares/{grantId}` | yes (owner) | no | Revoke invitation |
| DELETE | `api/Habits/{habitId}` | yes (owner) | no | Delete habit + graph |
| GET | `api/Habits/{habitId}/History` | yes (owner) | no | Per-day history |
| GET | `api/Habits/{habitId}/Items` | yes (owner) | no | List items |
| GET | `api/Habits/{habitId}/Items/{itemId}` | yes (owner) | no | Get item |
| POST | `api/Habits/{habitId}/Items` | yes (owner) | no | Create item (+properties) |
| PUT | `api/Habits/{habitId}/Items/{itemId}` | yes (owner) | no | Rename/reorder item |
| DELETE | `api/Habits/{habitId}/Items/{itemId}` | yes (owner) | no | Delete item (punch-free) |
| POST | `api/Habits/{habitId}/Items/{itemId}/Properties` | yes (owner) | no | Create property |
| GET | `api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | yes (owner) | no | Get property |
| PUT | `api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | yes (owner) | no | Update property |
| DELETE | `api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | yes (owner) | no | Delete property (unpunched) |
| GET | `api/Habits/{habitId}/Criteria` | yes (owner) | no | List criteria |
| GET | `api/Habits/{habitId}/Criteria/{criterionId}` | yes (owner) | no | Get criterion |
| POST | `api/Habits/{habitId}/Criteria` | yes (owner) | no | Create criterion (id refs) |
| PUT | `api/Habits/{habitId}/Criteria/{criterionId}` | yes (owner) | no | Update criterion |
| DELETE | `api/Habits/{habitId}/Criteria/{criterionId}` | yes (owner) | no | Delete criterion |
| GET | `api/Habits/{habitId}/Punches` | yes (owner) | no | Habit punch aggregate |
| GET | `api/Habits/{habitId}/Items/{itemId}/Punches` | yes (owner) | no | List item punches |
| GET | `api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | yes (owner) | no | Get punch |
| POST | `api/Habits/{habitId}/Items/{itemId}/Punches` | yes (owner) | no | Record punch session |
| PUT | `api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | yes (owner) | no | Replace punch values |
| DELETE | `api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | yes (owner) | no | Delete punch session |
| GET | `api/SharedHabits` | yes (invitee) | no | "Shared with me" gallery |
| GET | `api/SharedHabits/{habitId}` | yes (invitee/owner) | no | Read-only habit detail |
| GET | `api/SharedHabits/{habitId}/History` | yes (invitee/owner) | no | Read-only per-day history |
| POST | `api/UserLoginHistories` | yes (user) | yes (user) | Upsert today's login row |
| GET | `api/UserLoginHistories` | yes (user) | no | My per-day login history |
