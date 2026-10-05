# Controllers Reference

This document describes every API controller in the **aclearningutil** project, including the HTTP method, route, parameter list (with source — query / route / body), validation rules, and response shape for each endpoint.

All controllers live in [`src/aclearningutil/Controllers/`](../src/aclearningutil/Controllers/).

## Conventions

- **Base path**: each controller uses `[Route("api/[controller]")]`, so the path is `api/<ControllerName>` (controller name without the `Controller` suffix).
- **`[ApiController]`**: all controllers set this, enabling automatic 400 validation responses for model-binding errors and inference of `[FromBody]` / `[FromRoute]`.
- **Authentication**: every controller is decorated with `[Authorize]` **except** `LearningContentCategoriesController`, which is public. Auth uses JWT Bearer tokens issued by the Identity Server (audience `api.knowledgebuilder`).
- **Rate limiting**: `TTSController`, `FormatLLMController`, and `EnglishLLMController` are decorated with `[EnableRateLimiting("LLMAndTTS")]` (policy configured in `Program.cs`). The habit-tracking controllers (§9–§14) are deliberately NOT rate-limited (user-scoped CRUD at personal-use volume).
- **User scoping**: `UserLearningHistoriesController`, `UserLearningRatingsController` and `UserLoginHistoriesController` extract the user from the JWT via `ClaimTypes.NameIdentifier` (with `"sub"` fallback). All read/write operations are scoped to that user; the `UserId` column is always set server-side from the token and never trusted from the request body. The habit-tracking controllers use the same extraction (via `HabitControllerBase.GetUserId()`), always as `OwnerId`.
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

`POST`/`PUT` on the CRUD controllers accept the entity classes directly (`LearningContent`, `UserLearningHistory`, `UserLearningRating`). Their shapes are documented in [`docs/database-schema.md`](database-schema.md). Note: server-managed fields (`CreatedAt`, `UpdatedAt`, `UserId`) are ignored or overwritten on write.

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
**Auth**: `[Authorize]`

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
**Responses**: `201 Created` → `LearningContent` (with `Location` header pointing to `GetById`); `400 BadRequest` on validation failure.

### `PUT api/LearningContents/{id}`

Update an existing content item.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Content ID to update. |
| `content` | body | `LearningContent` | yes | New field values. |

**Validation**: same as `POST` (names, `FileUrl`, `CategoryId`).
**Side effects**: `UpdatedAt` is set to `DateTime.UtcNow`. `CreatedAt` is preserved.
**Responses**: `204 NoContent`; `400 BadRequest`; `404 NotFound`.

### `DELETE api/LearningContents/{id}`

Delete a content item **and** its dependent records.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | Content ID to delete. |

**Side effects**: because the FK relationships use `DeleteBehavior.Restrict`, the controller manually removes all `UserLearningHistories` and `UserLearningRatings` rows referencing this content before deleting it.
**Responses**: `204 NoContent`; `404 NotFound`.

---

## 3. `UserLearningHistoriesController`

**File**: [`UserLearningHistoriesController.cs`](../src/aclearningutil/Controllers/UserLearningHistoriesController.cs)
**Route**: `api/UserLearningHistories`
**Auth**: `[Authorize]` (user-scoped)

CRUD for the current user's learning history.

### `GET api/UserLearningHistories`

List the current user's history, optionally filtered, with pagination. Ordered by `LearnDate` descending. Includes the parent `Content`.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `contentId` | query | int? | no | — | Filter to a content item. |
| `itemId` | query | int? | no | — | Filter to a specific item within the content. |
| `page` | query | int? | no | `1` | Page number (1-based). |
| `pageSize` | query | int? | no | `50` | Page size; clamped to `[1, 200]`. |

**Responses**: `200 OK` → `List<UserLearningHistory>`; `401 Unauthorized` if no user ID in token.

### `GET api/UserLearningHistories/{id}`

Get a single history record belonging to the current user.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | History record ID. |

**Responses**: `200 OK` → `UserLearningHistory`; `401 Unauthorized`; `404 NotFound` (also returned if the record exists but belongs to another user).

### `POST api/UserLearningHistories`

Create a history record for the current user.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `history` | body | `UserLearningHistory` | yes | Must include `ContentId`; optionally `ItemId`, `LearnDate`, `SuccessIndicator`. |

**Validation**: `ContentId` must reference an existing `LearningContent`.
**Side effects**: `UserId` is set from the JWT (body value ignored). `LearnDate` defaults to `DateTime.Today` if not supplied / equal to `default`.
**Responses**: `201 Created` → `UserLearningHistory`; `400 BadRequest`; `401 Unauthorized`.

### `PUT api/UserLearningHistories/{id}`

Update one of the current user's history records.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | History record ID. |
| `history` | body | `UserLearningHistory` | yes | New field values. |

**Validation**: `ContentId` must reference an existing `LearningContent`.
**Side effects**: updates `ContentId`, `ItemId`, `LearnDate`, `SuccessIndicator`. `UserId` is unchanged.
**Responses**: `204 NoContent`; `400 BadRequest`; `401 Unauthorized`; `404 NotFound`.

### `DELETE api/UserLearningHistories/{id}`

Delete one of the current user's history records.

| Parameter | Source | Type | Required | Description |
|---|---|---|---|---|
| `id` | route | int | yes | History record ID. |

**Responses**: `204 NoContent`; `401 Unauthorized`; `404 NotFound`.

---

## 4. `UserLearningRatingsController`

**File**: [`UserLearningRatingsController.cs`](../src/aclearningutil/Controllers/UserLearningRatingsController.cs)
**Route**: `api/UserLearningRatings`
**Auth**: `[Authorize]` (user-scoped)

CRUD for the current user's content ratings (`Rating` is a byte in 1–5).

### `GET api/UserLearningRatings`

List the current user's ratings, optionally filtered, with pagination. Ordered by `ScoreDate` descending. Includes the parent `Content`.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `contentId` | query | int? | no | — | Filter to a content item. |
| `itemId` | query | int? | no | — | Filter to a specific item within the content. |
| `page` | query | int? | no | `1` | Page number (1-based). |
| `pageSize` | query | int? | no | `50` | Page size; clamped to `[1, 200]`. |

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
**Responses**: `201 Created` → `UserLearningRating`; `400 BadRequest`; `401 Unauthorized`.

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
- A final `Path.GetFullPath` check confirms the resolved path stays inside `Storage/`.
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
| `dto` | body | `HabitCreate` | yes | `name`, `description?`, `cycle` (required, nullable wire field), `startDate`, `endDate?`, `items` (1–100, each ≥1 property ≤100), `criteria` (1–100, exactly one `isRoot`). |

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
**Auth**: `[Authorize]` (user-scoped)

The caller's own login history: **one row per user per server-local calendar day**, upserted when the SPA reports a completed OIDC sign-in. There is deliberately no `PUT`/`DELETE` and no by-id route — login truth must not be client-mutable.

DTO: `UserLoginHistoryDto` — `{ loginDate (yyyy-MM-dd), firstLoginAt, lastLoginAt (UTC, 'Z'-suffixed), loginCount }`. POST takes **no body**: the user comes from the token and the clock from the server.

### `POST api/UserLoginHistories`

Upsert today's row: no row yet → insert with `firstLoginAt = lastLoginAt = now`, `loginCount = 1`; row exists → `lastLoginAt = now`, `loginCount += 1`. Repeat calls are idempotent for the row and cumulative for the count. The read-then-write runs inside one `Serializable` transaction (SQLite `BEGIN IMMEDIATE` — the write lock is taken before the read, so concurrent first-login POSTs can neither double-insert against the `(UserId, LoginDate)` unique index nor lose an increment; same mechanism as `HabitPunchesController.Create`).

**Responses**: `200 OK` → `UserLoginHistoryDto` (always 200 — the row's identity is (user, day), not a new id); `401 Unauthorized` if no user ID in token.

### `GET api/UserLoginHistories?from=&to=`

List the caller's days, ordered by `LoginDate` descending.

| Parameter | Source | Type | Required | Default | Description |
|---|---|---|---|---|---|
| `from` | query | DateOnly? | no | `to - 89 days` | Inclusive start of the window. |
| `to` | query | DateOnly? | no | server-local today | Inclusive end. Default window = trailing 90 days. |

**Validation**: `from > to` → `400 BadRequest`.
**Responses**: `200 OK` → `List<UserLoginHistoryDto>`; `401 Unauthorized`.

---

## Endpoint Summary

| Method | Route | Auth | Rate-limited | Description |
|---|---|---|---|---|
| GET | `api/LearningContentCategories` | no | no | List categories |
| GET | `api/LearningContentCategories/{id}` | no | no | Get category |
| GET | `api/LearningContents` | yes | no | List content (filter + paging) |
| GET | `api/LearningContents/{id}` | yes | no | Get content |
| POST | `api/LearningContents` | yes | no | Create content |
| PUT | `api/LearningContents/{id}` | yes | no | Update content |
| DELETE | `api/LearningContents/{id}` | yes | no | Delete content (+ dependents) |
| GET | `api/UserLearningHistories` | yes (user) | no | List my history |
| GET | `api/UserLearningHistories/{id}` | yes (user) | no | Get my history |
| POST | `api/UserLearningHistories` | yes (user) | no | Create history |
| PUT | `api/UserLearningHistories/{id}` | yes (user) | no | Update history |
| DELETE | `api/UserLearningHistories/{id}` | yes (user) | no | Delete history |
| GET | `api/UserLearningRatings` | yes (user) | no | List my ratings |
| GET | `api/UserLearningRatings/{id}` | yes (user) | no | Get my rating |
| POST | `api/UserLearningRatings` | yes (user) | no | Create rating |
| PUT | `api/UserLearningRatings/{id}` | yes (user) | no | Update rating |
| DELETE | `api/UserLearningRatings/{id}` | yes (user) | no | Delete rating |
| GET | `api/TTS/details` | yes | yes | Text-to-Speech (cached) |
| POST | `api/FormatLLM/AskAnything` | yes | yes | Subject LLM Q&A |
| GET | `api/EnglishLLM/details` | yes | yes | English LLM Q&A |
| GET | `api/Storage/{subfolder}/{filename}` | yes | no | Serve Storage file |
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
| POST | `api/UserLoginHistories` | yes (user) | no | Upsert today's login row |
| GET | `api/UserLoginHistories` | yes (user) | no | My per-day login history |
