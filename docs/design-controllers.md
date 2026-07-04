# Controllers Reference

This document describes every API controller in the **aclearningutil** project, including the HTTP method, route, parameter list (with source — query / route / body), validation rules, and response shape for each endpoint.

All controllers live in [`src/aclearningutil/Controllers/`](../src/aclearningutil/Controllers/).

## Conventions

- **Base path**: each controller uses `[Route("api/[controller]")]`, so the path is `api/<ControllerName>` (controller name without the `Controller` suffix).
- **`[ApiController]`**: all controllers set this, enabling automatic 400 validation responses for model-binding errors and inference of `[FromBody]` / `[FromRoute]`.
- **Authentication**: every controller is decorated with `[Authorize]` **except** `LearningContentCategoriesController`, which is public. Auth uses JWT Bearer tokens issued by the Identity Server (audience `api.knowledgebuilder`).
- **Rate limiting**: `TTSController`, `FormatLLMController`, and `EnglishLLMController` are decorated with `[EnableRateLimiting("LLMAndTTS")]` (policy configured in `Program.cs`).
- **User scoping**: `UserLearningHistoriesController` and `UserLearningRatingsController` extract the user from the JWT via `ClaimTypes.NameIdentifier` (with `"sub"` fallback). All read/write operations are scoped to that user; the `UserId` column is always set server-side from the token and never trusted from the request body.
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
