# Habit Tracker — API Design (`aclearningutil`)

## Overview

This document adapts the standalone `habit-api` (Python/FastAPI) from `../habit-tracker-v0.3.13/docs/api-design.md` into the **existing `aclearningutil` project**. Habit tracking becomes a new functional area of the learning API — a set of `Habit*` controllers, an EF Core entity model, and application services — rather than a separate service.

- Functional requirements: [`../../docs/habit-tracker-functional-spec.md`](../../docs/habit-tracker-functional-spec.md) (EN) / [CN](../../docs/habit-tracker-functional-spec-cn.md)
- Reference API design (superseded by this doc): `habit-tracker-v0.3.13/docs/api-design.md`

All functional behavior (habit model, properties, success criteria, punch semantics, cycle windows, error codes) is preserved from the reference design. The differences are: technology stack, routing prefix, wire-format casing conventions, tenant-ID claim mapping, JWT validation infrastructure, and deployment model — each called out below.

## Tech Stack Mapping

| Concern | Reference (`habit-api`) | This design (`aclearningutil`) |
|---|---|---|
| Language | Python 3.12 | C# / .NET 10 (`net10.0`) |
| Framework | FastAPI + Uvicorn | ASP.NET Core Web API (controllers, attribute routing) |
| Database | SQLite, raw `sqlite3`, per-request connection | SQLite via **EF Core**, `aclearningutil.db` (existing context + `EnsureCreated()`) |
| Auth | Manual RS256 JWKS validation (`joserfc`), refresh-on-fail, `asyncio.Lock` | Existing **JWT Bearer middleware** — OIDC discovery + automatic key-refresh handled by the framework (`Authority` = acidserver) |
| Tenant key | `user_sub` = OIDC `sub` | `OwnerId` = `ClaimTypes.NameIdentifier` with `"sub"` fallback (same extraction as all user-scoped endpoints in this project; value is acidserver's `AspNetUsers.Id`) |
| Route prefix | `/habits…` | `/api/Habits…` (matches existing controller convention, e.g. `/api/WordAudio`) |
| JSON casing | snake_case | **camelCase** on the wire (System.Text.Json default); enum *values* stay lowercase/snake as in the spec (`"daily"`, `"per_cycle"`) |
| Errors | `ErrorOut { code, detail }` | RFC 7807 `ProblemDetails` + `extensions.code` (same machine-readable code table) |
| Containerization | Own Dockerfile + compose | None — rides `aclearningutil`'s existing dev scripts and `publish-learning-all.ps1` deployment |

## Architecture

```
knowledgebuilder (UI)
      │  HTTPS + Bearer JWT (aud: api.knowledgebuilder)
      ▼
aclearningutil (existing ASP.NET Core host)
  ├── JWT Bearer middleware — existing auth setup (Authority: acidserver)
  ├── Controllers (new)
  │     ├── HabitsController        — habits CRUD, deactivate, history
  │     ├── HabitItemsController    — items CRUD
  │     ├── ItemPropertiesController— properties CRUD
  │     ├── HabitCriteriaController — criteria CRUD
  │     └── HabitPunchesController  — punch sessions CRUD + lists
  ├── Services (new)
  │     ├── HabitEvaluationService  — cycle windows + criterion tree evaluation
  │     ├── HabitRuleValidator      — shared business-rule validation (static)
  │     └── HabitException (+ filter) — RFC 7807 ProblemDetails with extensions.code;
  │                                    also maps DbUpdateException unique-index
  │                                    violations → duplicateName and JSON binding
  │                                    failures → invalidEnumValue
  │     (Controllers derive from HabitControllerBase — user-id extraction from the
  │      JWT + tenant-scoped habit loading; no separate CurrentUserService.)
  └── Existing DbContext (extended with habit entities)
        └── SQLite: src/aclearningutil/aclearningutil.db
```

Project conventions that apply (from the repo's `CLAUDE.md`): `async`/`await` for all I/O, nullable reference types on, `using` declarations, no `.Result`/`.Wait()`, `dotnet format`-clean.

### Suggested file layout

Follow the existing folder layout of the project; the design assumes:

```
src/aclearningutil/
├── Controllers/HabitsController.cs, HabitItemsController.cs,
│               ItemPropertiesController.cs, HabitCriteriaController.cs,
│               HabitPunchesController.cs, HabitControllerBase.cs
├── Data/Entities/     (EF entities: Habit, HabitItem, ItemProperty, Criterion, Punch, …)
├── Models/            (HabitRequests.cs, HabitResponses.cs — request/response records)
├── Services/          (HabitRuleValidator, HabitEvaluationService, HabitException)
└── Utility/           (HabitSchemaBootstrap, HabitJsonOptions)
```

## Data Model (EF Core entities)

Logical schema mirrors the reference 1:1. Column types below are the SQLite store types; enum-valued columns are stored as strings and constrained at application level (EF Core does not emit CHECK constraints — see § deviations). **Storage spelling:** `HasConversion<string>()` writes the C# member name verbatim — `Monthly`, `Boolean`, `PerCycle`, `And`, … — NOT the lowercase/snake values shown in the tables below (those are wire-format spellings only; the JSON converter maps between them). All tables carry an `OwnerId` column so every query can be tenant-filtered at the earliest level.

### Habit

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | INTEGER PK autoincrement |
| `OwnerId` | `string` (450) | Tenant isolation key — indexed (`IX_Habits_OwnerId`) |
| `Name` | `string` | Required; **not unique** per user |
| `Description` | `string?` | |
| `Cycle` | `HabitCycle` enum | Stored as string: `daily` \| `weekly` \| `monthly` \| `whole` |
| `StartDate` | `DateOnly` | ISO `yyyy-MM-dd` |
| `EndDate` | `DateOnly?` | Must be ≥ `StartDate` when set |
| `State` | `HabitState` enum | Stored as string: `active` \| `inactive`; default `active` |
| `CreatedAt` | `DateTime` | UTC ISO |

Navigations: `Items`, `Criteria` (cascade delete), `Shares` (see `HabitShareGrant`).

### HabitShareGrant (sharing)

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK autoincrement |
| `HabitId` | `int` | FK → `Habits` (cascade delete); indexed with `GranteeUserId` UNIQUE |
| `OwnerId` | `string` | Redundant copy (fast tenant filter) |
| `GranteeUserId` | `string` | The invited user's acidserver id (= their `sub` claim) — the access-matching key |
| `GranteeUserName` | `string` | Display-name snapshot at invite time (UI only; matching uses the id) |
| `OwnerName` | `string` | Owner display-name snapshot — taken SERVER-SIDE from the inviter's own `name` claim (never client-sent); serialized to viewers as `ownerName` |
| `CreatedAt` | `DateTime` | UTC ISO |

One grant per `(HabitId, GranteeUserId)` (unique index — a lost concurrent-invite race maps to 422 `duplicateName` through the exception filter, like the three name indexes). The invite's `AnyAsync` pre-check and insert run inside one `IsolationLevel.Serializable` transaction. A grant grants READ-ONLY visibility of the habit + its full punch history through `api/SharedHabits` — no other user sees anything. Created/revoked via `api/Habits/{id}/Shares`; effects are immediate (viewer queries join the table live; deletion re-privatizes instantly). Created by the schema bootstrap's DDL replay (`CREATE TABLE IF NOT EXISTS`) — no `ALTER` needed on existing databases.

### HabitItem

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK autoincrement |
| `HabitId` | `int` | FK → `Habit` **Cascade** |
| `OwnerId` | `string` | Redundant copy (fast tenant filter, as in reference) |
| `Name` | `string` | |
| `Order` | `int` | Display order |
| `CreatedAt` | `DateTime` | UTC |

Index: `IX_HabitItems_HabitId`. Unique index `UX_HabitItems_HabitId_Name` on `(HabitId, Name)` — DB-level enforcement of item-name uniqueness; violation maps to `duplicate_name`.

### ItemProperty

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `ItemId` | `int` | FK → `HabitItem` **Cascade** |
| `HabitId`, `OwnerId` | `int`, `string` | Redundant copies for fast filtering |
| `Name` | `string` | |
| `PropertyType` | `PropertyType` enum | Stored as string: `boolean` \| `numeric` \| `list` |
| `BaseRate` | `double?` | Numeric only, > 0 when set |
| `ItemUniqueness` | `ItemUniqueness?` enum | `per_day` \| `per_cycle`; required for `list`, forbidden otherwise |
| `Order` | `int` | |
| `CreatedAt` | `DateTime` | |

Indexes: on `ItemId`, on `HabitId`; unique `UX_ItemProperties_ItemId_Name` on `(ItemId, Name)` → `duplicate_name`.

### Criterion

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `HabitId` | `int` | FK **Cascade** |
| `OwnerId` | `string` | |
| `Name` | `string` | |
| `CriterionType` | enum | `condition` \| `composite` — immutable after creation |
| `IsRoot` | `bool` | |
| `SuccessType` | `SuccessType?` | Root only; required on root (`daily` \| `cumulative`, spec "Success types"). `daily` = day-count: the tree re-evaluates every calendar day and successful days accumulate against `CycleTarget`; `cumulative` = aggregated over the cycle, target derived from the tree. Rejected with `invalidTarget` on non-root criteria or when missing on the root |
| `CycleTarget` | `double?` | Root in `daily` mode only: successful days required (daily cycle = 1, weekly ≤ 7, monthly ≤ 31, whole unbounded). NULL on non-root criteria **and** on cumulative roots — the cumulative target is derived (condition: threshold; composite: required passing operands) and never stored |
| `CreatedAt` | `DateTime` | |

Indexes: `IX_Criteria_HabitId`; unique `UX_Criteria_HabitId_Name`; **partial unique index** `UX_Criteria_OneRoot` on `(HabitId) WHERE "IsRoot" = 1` — SQLite supports partial indexes; configure via `.HasFilter("[IsRoot] = 1")`. This enforces the single-root invariant at the DB level.

### CriterionCondition (1:1 with a condition Criterion)

> Physical table: `CriterionLeaves` (frozen legacy name — pinned via `ToTable` so existing
> databases and the schema-bootstrap raw SQL stay valid after the leaf→condition rename).

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `CriterionId` | `int` | FK → `Criterion` **Cascade**, UNIQUE |
| `PropertyName` | `string` | The tested property's NAME (no FK — the condition binds the name across the scope, spec FR-2.3). Deleting property rows never breaks the criterion: if the name disappears from the entire scope, the aggregate is permanently 0 and the condition never passes. Validated at write time to exist on ≥ 1 in-scope item |
| `ItemScope` | enum | `all` \| `subset` (subset must name ≥ 1 item at creation; may shrink later) |
| `AggregationMode` | `AggregationMode?` | `sum/avg/max/min/latest` (numeric), `union_distinct/latest` (list), `ever_true` (boolean, the only mode). Null = the type default |
| `Threshold` | `double` | > 0; pass = aggregate ≥ threshold (the only operator — upper bounds/negation go through NOT composites). Integer item-count semantics for boolean properties |

### CriterionConditionScopeItem

> Physical table/column names `CriterionLeafScopeItems` / `CriterionLeafId` are frozen legacy
> identifiers (pinned in `AppDbContext`); the CLR types are `CriterionConditionScopeItem` /
> `CriterionConditionId`.

Join table, PK `(CriterionLeafId, ItemId)`; both FKs **Cascade**. Semantics unchanged from reference (scope shrinks automatically when a scoped item is deleted — deleting an item without punches is always permitted, even when referenced here; only punch records gate item deletion).

### CriterionComposite (1:1 with a composite Criterion)

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `CriterionId` | `int` | FK **Cascade**, UNIQUE |
| `Operator` | enum | `and` \| `or` \| `not` |

### CriterionCompositeOperand

| Property | C# type | Notes |
|---|---|---|
| `CompositeId` | `int` | FK → `CriterionComposite` **Cascade** |
| `OperandCriterionId` | `int` | FK → `Criterion` **Restrict** (deleting an operand in use → `criterion_in_use`; DB-level safety net) |
| `Order` | `int` | |

PK `(CompositeId, OperandCriterionId)`.

### Punch

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `HabitId`, `ItemId` | `int` | FKs **Cascade** |
| `OwnerId` | `string` | |
| `PunchedAt` | `DateTime` | UTC instant; updated on boolean upsert |
| `PunchDate` | `DateOnly` | Client-supplied `punchDate` on create — validated against the server calendar (≤ today, inside the habit window) — defaulting to **server local time** at punch moment (NFR-5) |
| `CreatedAt` | `DateTime` | UTC |

Indexes: `IX_Punches_OwnerId` (tenant key), and composite `(HabitId, PunchDate)`, `(ItemId, PunchDate)`. Model indexes reach pre-existing databases through the schema bootstrap's Phase-1 `CREATE INDEX IF NOT EXISTS` replay (see § Database Initialization).

**Timezone note:** `PunchDate` defaults to `DateOnly.FromDateTime(DateTime.Now)` evaluated on the server (deployed host time, Beijing time for `www.alvachien.com`). No per-user timezone (out of scope). A punch create may instead carry an explicit `punchDate` to back-fill a past day, but it is validated **against the server's calendar** (future or out-of-window dates → `outOfWindow`), so a client clock can never move the day beyond today. Clients near midnight may observe a date differing from their local date; the UI shows a hint (§ UI design doc).

### PunchValue

| Property | C# type | Notes |
|---|---|---|
| `Id` | `int` | PK |
| `PunchId` | `int` | FK → `Punch` **Cascade** |
| `PropertyId` | `int` | FK → `ItemProperty` **Restrict** (punch values block property deletion → `item_has_punches`) |
| `BoolValue` | `bool?` | boolean properties |
| `NumValue` | `double?` | numeric properties, > 0 |
| `ListEntries` | `string?` | JSON array of strings (System.Text.Json serialize/deserialize; store as TEXT) |

Indexes: `IX_PunchValues_PunchId`; `(PropertyId, PunchId)`. Exactly one value column non-null, matching the property's type.

Behavior per property type is unchanged from the reference:

- **boolean** — upsert: at most one `PunchValue` row per (property, item, day) across all punch sessions; a second punch the same day replaces `BoolValue` and updates `PunchedAt`. Enforced inside a single EF transaction opened with **`IsolationLevel.Serializable`** (`BeginTransactionAsync(IsolationLevel.Serializable, ct)`): the provider verified mapping (see `SqliteSerializableTransactionTests`) is `BEGIN IMMEDIATE` — the write lock is taken at BEGIN, so a concurrent same-day writer cannot interleave its read between our read and insert (both readers missing each other's row is the double-live-boolean hazard). The invariant survives both punch Create and punch Update (same isolation on the edit path). Last writer wins by design. Note the finding that the no-argument `BeginTransactionAsync(ct)` default already resolved to this immediate BEGIN on this stack (EF's `Unspecified` → Microsoft.Data.Sqlite `DefaultIsolationLevel = Serializable`, which maps to BEGIN IMMEDIATE) — the explicit level pins that guarantee instead of depending on provider defaults.
- **numeric** — multiple rows per property per day; values accumulate.
- **list** — entries appended across sessions; `item_uniqueness` enforced (see Validation).

## Enums & Wire Format

C# enums serialize as the spec's lowercase strings via a single `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)` registered on the MVC JSON options (no per-member `[EnumMember]` attributes; configured in `Utility/HabitJsonOptions.cs`) — this yields the mixed set below (`daily`, `per_cycle`, `and`) directly from the PascalCase enum members. **Strings only on the wire:** an integer or unknown-string enum value in a request body is rejected at binding time instead of silently mapping to a member by number. Omitted required enums bind to `null` (the wire fields are nullable) and the controllers turn that into a coded 422 — habit create/update without `cycle` → `missingCycle`; property create (standalone, nested in item create, or nested in habit create) without `propertyType` → `missingPropertyType`; criterion create (standalone or nested in habit create) without `criterionType` → `missingCriterionType`. None of them ever silently defaults to the enum's 0 member. Storage in SQLite is the verbatim PascalCase member name (see § Data Model).

**Actual failure shapes for enum-valued fields (verified behavior, not aspirational):**

| Request | Response |
|---|---|
| Field omitted (`propertyType` / `criterionType` / `cycle` absent) | 422 ProblemDetails with `extensions.code` = `missingPropertyType` / `missingCriterionType` / `missingCycle` |
| Non-string value (`"cycle": 0`) or unknown string (`"cycle": "yearly"`) | **400** validation ProblemDetails produced by the MVC input formatter (a `JsonException` during body binding surfaces as model state) — **without** `extensions.code`, because the binding failure happens before any controller action runs |
| Out-of-range enum reachable post-binding (e.g. direct code paths) | 422 `invalidPropertyType` / `invalidItemUniqueness` defense-in-depth checks in the validators |

The exception filter keeps its `JsonException` → 422 `invalidEnumValue` branch (`HabitExceptionFilter.HandleJsonBinding`) as a safety net for exceptions raised post-binding; in the normal MVC pipeline binding failures never reach it, so `invalidEnumValue` is effectively unreachable on the wire paths today — the filter's unit test pins the mapping for the paths that remain.

| C# | Wire values |
|---|---|
| `HabitCycle` | `daily` `weekly` `monthly` `whole` |
| `HabitState` | `active` `inactive` |
| `PropertyType` | `boolean` `numeric` `list` |
| `ItemUniqueness` | `per_day` `per_cycle` |
| `CriterionType` | `condition` `composite` |
| `ItemScope` | `all` `subset` |
| `CompositeOperator` | `and` `or` `not` |
| `SuccessType` | `daily` `cumulative` |
| `AggregationMode` | `ever_true` `sum` `avg` `max` `min` `latest` `union_distinct` |

Dates serialize as `yyyy-MM-dd` (`DateOnly`), instants as ISO-8601 UTC **with a trailing `Z`** — SQLite reads `DateTime` columns back as `Kind=Unspecified`, so the habit entities' timestamp columns carry a read-side value converter (`AppDbContext`) that re-marks them `Kind=Utc` (store format unchanged). All JSON property names are camelCase (e.g. `cycleFrom`, `propertyName`, `currentDayValue`, `successfulDays`) — the reference's snake_case names are dropped in favor of this project's ASP.NET default. The UI design doc uses this same contract.

## REST API

All endpoints require authentication (`[Authorize]` on the controller group; the project's existing default policy validates issuer + audience `api.knowledgebuilder`). The reference's public `GET /version` is **not duplicated** — `aclearningutil` keeps its existing health/version endpoint; the UI points at that one.

Every handler resolves `OwnerId` from the JWT (`ClaimTypes.NameIdentifier`, fallback `sub`) and applies it to every query. Cross-user access returns 404 (`notFound`), never 403 — same enumeration-resistance rule as the reference.

**FR-6 exception (invitations):** the read-only `api/SharedHabits` surface relaxes the `OwnerId` filter ONLY toward explicitly invited users — access requires a `HabitShareGrant` matching the caller's id (the habit owner also passes, for convenience). Users without a grant and unknown ids get indistinguishable 404s, so a habit shared with someone else (or private) is never even confirmed to exist. All WRITE paths (items, properties, criteria, punches, deactivate, delete, share management) remain owner-gated exactly as before.

### Habits

| Method | Path | Success | Description |
|---|---|---|---|
| `GET` | `/api/Habits` | 200 | List caller's habits with current-cycle progress |
| `POST` | `/api/Habits` | 201 | Create habit + items + properties + criteria (atomic, one transaction) |
| `GET` | `/api/Habits/{habitId}` | 200 | Single habit with current-cycle progress |
| `PUT` | `/api/Habits/{habitId}` | 200 | Update name/description/cycle/start/end (`state` not settable here) |
| `POST` | `/api/Habits/{habitId}/Deactivate` | 200 | Deactivate — irreversible; `alreadyInactive` (422) if already inactive |
| `GET` | `/api/Habits/{habitId}/Shares` | 200 | List invitations (`ShareGrantOut[]`) — owner-only |
| `POST` | `/api/Habits/{habitId}/Shares` | 201 | Invite one user: `{ granteeUserId, granteeUserName }` (from acidserver `/api/users/search`). Owner name is snapshotted from the CALLER'S `name` claim. 422 `invalidGrantee` (blank fields / self-invite), 422 `duplicateName`, 422 `missingShareOwnerName` (token without a name claim) |
| `DELETE` | `/api/Habits/{habitId}/Shares/{grantId}` | 204 | Revoke an invitation (re-privatizes for that user instantly); foreign grant ids under a valid habit 404 |
| `DELETE` | `/api/Habits/{habitId}` | 204 | Delete habit + all data (cascade) |
| `GET` | `/api/Habits/{habitId}/History` | 200 | Per-day history with criterion pass/fail; optional `from`/`to` (`yyyy-MM-dd`, inclusive); lists every day of range∩active-window up to today, punch-less days included |

### SharedHabits ("shared with me", read-only)

`SharedHabitsController` — `[Authorize]`, extends the habit controller base. Lists and reads only the habits with a `HabitShareGrant` for the CALLER (plus the caller's own habits on the `{habitId}` routes, for convenience). There are no write endpoints here; items/criteria values are computed against the caller's request date (same aggregation the owner sees). `SharedHabitOut = { habit: HabitOut, ownerName }`; `HabitOut` never carries `OwnerId`, and invitees are NOT able to punch or edit.

| Method | Path | Success | Description |
|---|---|---|---|
| `GET` | `/api/SharedHabits` | 200 | "Shared with me": every habit the caller was invited to (owner-name snapshot + embedded progress) |
| `GET` | `/api/SharedHabits/{habitId}` | 200 | `{ habit, ownerName, items, criteria }` — full definition read-only; 404 (`notFound`) unless invited (or the owner) |
| `GET` | `/api/SharedHabits/{habitId}/History` | 200 | Identical shape/granularity to the owner's History (full punch values visible to invitees); optional `from`/`to`, `invalidDateRange` on inverted range |

Name-snapshot staleness: `ownerName` reflects the inviter's display name when the grant was created; an ID-provider rename does not propagate to existing grants. Invitee identification always matches on `granteeUserId` (the immutable claim id), never on the name snapshot.

### Items

| Method | Path | Success |
|---|---|---|
| `GET` | `/api/Habits/{habitId}/Items` | 200 |
| `POST` | `/api/Habits/{habitId}/Items` | 201 |
| `GET` | `/api/Habits/{habitId}/Items/{itemId}` | 200 |
| `PUT` | `/api/Habits/{habitId}/Items/{itemId}` | 200 |
| `DELETE` | `/api/Habits/{habitId}/Items/{itemId}` | 204 (blocked if punches exist) |

### Item Properties

| Method | Path | Success |
|---|---|---|
| `POST` | `/api/Habits/{habitId}/Items/{itemId}/Properties` | 201 |
| `GET` | `/api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | 200 |
| `PUT` | `/api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | 200 |
| `DELETE` | `/api/Habits/{habitId}/Items/{itemId}/Properties/{propertyId}` | 204 (blocked only while punch values reference the property; criteria bind names and survive) |

### Criteria

| Method | Path | Success |
|---|---|---|
| `GET` | `/api/Habits/{habitId}/Criteria` | 200 |
| `POST` | `/api/Habits/{habitId}/Criteria` | 201 |
| `GET` | `/api/Habits/{habitId}/Criteria/{criterionId}` | 200 |
| `PUT` | `/api/Habits/{habitId}/Criteria/{criterionId}` | 200 — `isRoot: true` atomically clears the previous root in the same transaction; `isRoot: false` on the current root → `rootRequired` |
| `DELETE` | `/api/Habits/{habitId}/Criteria/{criterionId}` | 204 — blocked for root (`rootCriterionProtected`) and for referenced operands (`criterionInUse`) |

### Punches

| Method | Path | Success |
|---|---|---|
| `POST` | `/api/Habits/{habitId}/Items/{itemId}/Punches` | 201 — optional body `punchDate` records the session for a past in-window day (back-fill); omitted = server today |
| `GET` | `/api/Habits/{habitId}/Items/{itemId}/Punches` | 200 — optional `from`/`to` |
| `GET` | `/api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | 200 |
| `PUT` | `/api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | 200 |
| `DELETE` | `/api/Habits/{habitId}/Items/{itemId}/Punches/{punchId}` | 204 |
| `GET` | `/api/Habits/{habitId}/Punches` | 200 — habit-scoped aggregate, optional `from`/`to`, ordered by `punchDate` desc then `punchedAt` desc |

## Request / Response DTOs

Defined as C# records in `Models/HabitRequests.cs` and `Models/HabitResponses.cs`. Field semantics are identical to the reference (`HabitCreate`, `HabitOut`, `ProgressOut`, …); names are PascalCase in C#, camelCase on the wire. Error-code identifiers below are written in **camelCase** (see § Errors for the reference → this design code mapping convention).

### Create / update payloads

```csharp
record HabitCreateDto(
    string Name,                       // required, min length 1
    string? Description,
    HabitCycle Cycle,
    DateOnly StartDate,
    DateOnly? EndDate,                 // must be >= StartDate
    IReadOnlyList<ItemCreateDto> Items,        // min 1 — created atomically with habit
    IReadOnlyList<CriterionCreateDto> Criteria // min 1 — exactly one with IsRoot = true
);

record HabitUpdateDto(string Name, string? Description, HabitCycle Cycle,
    DateOnly StartDate, DateOnly? EndDate);    // no State field; Cycle blocked if punches exist

record ItemCreateDto(string Name, int Order, IReadOnlyList<PropertyCreateDto> Properties); // ≥1, ≤100 properties
record ItemUpdateDto(string Name, int Order);

record PropertyCreateDto(string Name, PropertyType? PropertyType,   // REQUIRED — omitted → 422 missingPropertyType
    double? BaseRate,                  // numeric only, > 0 and finite
    ItemUniqueness? ItemUniqueness,    // required for list, forbidden otherwise
    int Order);
record PropertyUpdateDto(string Name, double? BaseRate, int Order);
    // PropertyType and ItemUniqueness are immutable after creation

record CriterionCreateDto(
    string Name, bool IsRoot, CriterionType? CriterionType,   // REQUIRED — omitted → 422 missingCriterionType
    // condition — name-based in EVERY phase (there is no property-id binding any more);
    // scope items are name-based during HabitCreate, id-based afterwards:
    string? PropertyName, AggregationMode? AggregationMode,   // mode null = type default
    ItemScope? ItemScope, IReadOnlyList<string>? ScopeItemNames,
    IReadOnlyList<int>? ScopeItemIds,
    double? Threshold,                 // required for condition, > 0 and finite (integer count for boolean)
    // composite:
    CompositeOperator? Operator,
    IReadOnlyList<string>? OperandCriterionNames,   // HabitCreate only
    IReadOnlyList<int>? OperandCriterionIds,        // post-creation only
    // root only:
    SuccessType? SuccessType,          // required on root; forbidden on non-root
    double? CycleTarget);              // daily root only; must be null for a cumulative root; positive finite integer
record CriterionUpdateDto( /* same shape — operands/scope id-based, condition name-based; CriterionType not updatable.
    Duplicate entries inside scopeItemIds/operandCriterionIds (or their name forms in the create
    payload) are rejected with 422 duplicateReference BEFORE any insert — the join-table PKs
    would otherwise surface as a 500 */ );

record PunchCreateDto(IReadOnlyList<PropertyValueCreateDto> Values,     // min 1, max 100 (→ tooManyEntries)
    DateOnly? PunchDate);   // null = server today; else must be ≤ server today and inside [startDate, endDate?] → outOfWindow
record PropertyValueCreateDto(int PropertyId, bool? BoolValue,
    double? NumValue, IReadOnlyList<string>? ListEntries);
    // exactly one value set, matching the property's type; NumValue > 0 and finite;
    // ListEntries min 1, max 100 entries (→ tooManyEntries), each ≤ 1000 chars (→ validationTooLong)
record PunchUpdateDto(IReadOnlyList<PropertyValueCreateDto> Values);
```

Conditions always reference the property by name (in both payloads and standalone/PUT requests). The name-based vs id-based exclusivity remains for scopes and operands: `ScopeItemNames` / `OperandCriterionNames` during `HabitCreate`; `ScopeItemIds` / `OperandCriterionIds` afterwards. Providing both forms → `invalidPropertyType`; unresolvable names roll back the whole transaction → `unknownOperandName` (the `detail` message names the offending value so the UI can highlight the row). A property name shared across items of one habit must carry the same `PropertyType` (violations → `invalidPropertyType`), which keeps the name-bound aggregation well-typed over the scope.

### Response payloads

```csharp
record HabitOutDto(int Id, string Name, string? Description, HabitCycle Cycle,
    DateOnly StartDate, DateOnly? EndDate, HabitState State,
    DateOnly? DeactivatedDate,  // spec FR-2.4 — set by POST .../Deactivate, null while active;
                                // serialized yyyy-MM-dd like every other DateOnly field
    bool HasPunches,      // EXISTS over all cycles, computed at query time, not stored
    DateTime CreatedAt, ProgressOutDto Progress);
    // OwnerId intentionally NOT serialized (internal tenant key) — FR-6

record ProgressOutDto(DateOnly CycleFrom, DateOnly? CycleTo,   // CycleTo null = open-ended whole (no sentinel)
    CriterionProgressOutDto RootCriterion,
    IReadOnlyList<CriterionProgressOutDto> Criteria); // all non-root criteria; root not duplicated

record CriterionProgressOutDto(
    int CriterionId, string Name, bool IsRoot, CriterionType CriterionType, bool Passed,
    // condition only: PropertyName + effective AggregationMode; CurrentValue per mode:
    //   boolean (ever_true) — count of scoped items whose property was true on ≥1 day in the window
    //   numeric (sum/avg/max/min/latest) — over (numValue × baseRate), missing property ⇒ 0
    //   list (union_distinct) — per-item distinct-entry count in the window, SUMMED over scoped items
    //        (the same text under two items counts twice — spec RC-9); latest = per-item last punch
    // composite only: CurrentValue = count of passing operands; Threshold = operands required (AND: n, OR: 1, NOT: 1)
    // pass is always aggregate ≥ threshold (single operator)
    string? PropertyName, AggregationMode? AggregationMode, double? CurrentValue, double? Threshold,
    // root only:
    SuccessType? SuccessType,
    double? CycleTarget,      // daily mode only; null for cumulative roots (target derived)
    double? CurrentDayValue,  // daily mode; non-null iff today ∈ active window AND habit active
                              // (condition root: day aggregate; composite root: 1.0/0.0 pass bit)
    int? SuccessfulDays,      // daily mode only
    CompositeOperator? Operator, IReadOnlyList<int>? OperandIds);

record ItemOutDto(int Id, int HabitId, string Name, int Order, DateTime CreatedAt,
    bool HasPunches, IReadOnlyList<PropertyOutDto> Properties);

record PropertyOutDto(int Id, int ItemId, string Name, PropertyType PropertyType,
    double? BaseRate, ItemUniqueness? ItemUniqueness, int Order, DateTime CreatedAt,
    double CurrentCycleValue,  // this property on this item, current cycle: boolean — days true;
                               // numeric — sum(value × baseRate); list — distinct entries in the window
    double? TodayValue);       // 0.0 (not null) when a boolean punch recorded `false` today;
                               // null only when nothing was punched for it today

record CriterionOutDto(int Id, int HabitId, string Name, bool IsRoot,
    CriterionType CriterionType, string? PropertyName, AggregationMode? AggregationMode,
    ItemScope? ItemScope,
    IReadOnlyList<int>? ScopeItemIds, double? Threshold, CompositeOperator? Operator,
    IReadOnlyList<int>? OperandCriterionIds, SuccessType? SuccessType, double? CycleTarget,
    DateTime CreatedAt);

record PunchOutDto(int Id, int HabitId, int ItemId, DateTime PunchedAt,
    DateOnly PunchDate, DateTime CreatedAt, IReadOnlyList<PropertyValueOutDto> Values);
record PropertyValueOutDto(int PropertyId, string PropertyName,
    PropertyType PropertyType, bool? BoolValue, double? NumValue,
    IReadOnlyList<string>? ListEntries);

record DayHistoryOutDto(DateOnly Date, DateOnly CycleFrom, DateOnly? CycleTo, bool IsSuccessful,
    IReadOnlyList<CriterionDayResultOutDto> Criteria,
    IReadOnlyList<PunchOutDto> Punches); // flat list across items; UI groups by item
record CriterionDayResultOutDto(int CriterionId, string Name, bool Passed,
    double? CurrentValue);
```

History enumerates **every day** of the queried range inside the habit's active window up to the server's today — punch-less days appear with an empty `punches` list and their computed day outcome (NOT-style criteria rely on the empty-data evaluation, spec FR-3.4). For a deactivated habit the window end additionally clamps at `DeactivatedDate − 1` (its last active day, FR-2.2/FR-2.4): no rows are emitted on or after the deactivation day, so a punch-less post-deactivation day can never advertise a NOT-style achievement. Everything is derived from CURRENT punches and CURRENT criteria for every cycle (spec: no snapshots, no frozen statuses); for a cumulative root `isSuccessful` is latched on the first day the cycle passed and stays true to the cycle end — the latch replays the cycle's prefixes even when the requested window starts after the pass day (punch FACTS load from the start of the cycle containing the window start, not from the window start itself, so a mid-cycle `?from=` still evaluates the running aggregate over `[cycle_from, day]`); day-count mode — root passed on that day. `currentDayValue`, boolean upsert on edit, and `per_cycle` uniqueness re-check excluding the edited punch: all as in the reference.

## Errors — `ProblemDetails` + code

Validation and conflict failures return RFC 7807 `ProblemDetails` (`application/problem+json`) with the machine-readable code in `extensions.code`:

```json
{
  "type": "https://www.alvachien.com/errors/duplicate-entry",
  "title": "Unprocessable Content",
  "status": 422,
  "detail": "Entry 'ephemeral' already recorded for 'word' this cycle.",
  "code": "duplicateEntry"
}
```

(If the project's existing controllers already use a custom error envelope for the learning APIs, match that envelope instead — the invariant is a stable machine-readable `code` string plus a human `detail`.)

Code names follow the reference's snake_case values converted to camelCase to stay consistent with the new wire casing (the UI maps `code` → i18n key). The complete table:

| `code` | HTTP | Meaning |
|---|---|---|
| `notFound` | 404 | Resource missing or belongs to another user |
| `unauthenticated` | 401 | The authenticated token carries no user-id claim (every habit endpoint's identity gate; a bare `Unauthorized()`/`NotFound()` body is never returned — the filter's ProblemDetails envelope always is) |
| `habitInactive` | 422 | Punch rejected — habit is inactive |
| `alreadyInactive` | 422 | Deactivation rejected — already inactive |
| `reactivationNotSupported` | 422 | Reserved — FR-2.4 is enforced by construction: `state` is not editable and no reactivate route exists, so the API never emits this code (constant kept for the UI i18n key) |
| `outOfWindow` | 422 | New punch rejected — its punch date (default: today) is outside the habit's start/end window (or a future date) |
| `invalidDateRange` | 422 | `endDate` before `startDate` (also inverted `from`/`to` on list endpoints) |
| `duplicateEntry` | 422 | List punch violates `itemUniqueness` — the entire punch record for that item is rejected atomically (spec FR-3.2) |
| `duplicateName` | 422 | Duplicate item / property / criterion name (also from unique-index violation) |
| `structuralChangeBlocked` | 422 | Edit blocked while punch records exist (changing `cycle` to a different value; removing items/properties with punch history; type/uniqueness changes) |
| `itemHasPunches` | 422 | Item/property deletion blocked by punch values |
| `missingCycleTarget` | 422 | A `daily`-mode root criterion is missing the required `cycleTarget` |
| `invalidTarget` | 422 | successType/cycleTarget placement, presence, or range violation (missing `successType` on the root; either field on a non-root; `cycleTarget` submitted on a cumulative root; daily-mode cycleTarget ≤ 0/non-integral; daily cycle ≠ 1; weekly > 7; monthly > 31) |
| `invalidThreshold` | 422 | Condition threshold not > 0 |
| `missingCycle` | 422 | Habit create/update body omitted `cycle` (never silently defaults to `daily`) |
| `missingPropertyType` | 422 | Property create body (standalone, nested in item create, or nested in habit create) omitted `propertyType` (never silently defaults to `boolean` = 0) |
| `missingCriterionType` | 422 | Criterion create body (standalone or nested in habit create) omitted `criterionType` (never silently defaults to `condition` = 0) |
| `scopeItemsRequired` | 422 | `subset`-scoped condition criterion without `scopeItemIds` |
| `itemWithoutProperties` | 422 | Item create/update with zero properties |
| `invalidEnumValue` | 422 | Enum-valued request field carries a non-string / unparseable value (raised with a JSON path during binding). **Note:** in the normal MVC pipeline a body-binding failure surfaces as a plain 400 validation ProblemDetails before any action runs (no `code`), so this code is emitted only for post-binding `JsonException`s — it is effectively unreachable on the wire and kept as a filter safety net (§ Enums & Wire Format) |
| `duplicateReference` | 422 | A criterion's `scopeItemIds`/`scopeItemNames` or `operandCriterionIds`/`operandCriterionNames` list contains a duplicate value (rejected before insert; would otherwise hit the join-table PK → 500) |
| `validationTooLong` | 422 | A name (max 500), the description (max 2000), a bound `propertyName` (max 500), a list entry (max 1000 chars), or a grantee id (max 200) / grantee name (max 500) exceeds its design-DDL max length — SQLite does not enforce these, the write paths reject them |
| `tooManyEntries` | 422 | A create/update payload exceeds a server-side count cap: items ≤ 100, properties per item ≤ 100, criteria ≤ 100, scope/operand references ≤ 100, punch values ≤ 100, list entries ≤ 100 |
| `noItems` | 422 | Habit create without items (reserved for exactly that — an item missing properties is `itemWithoutProperties`) |
| `noCriteria` | 422 | Habit create without criteria |
| `noRootCriterion` | 422 | Not exactly one root criterion |
| `rootRequired` | 422 | Clearing root flag without designating a new root |
| `rootCriterionProtected` | 422 | Delete attempted on the root criterion |
| `circularCriterion` | 422 | Criterion update would create a cycle in the DAG |
| `criterionInUse` | 422 | Criterion referenced as an operand by another criterion |
| `invalidOperandCount` | 422 | `not` ≠ exactly 1 operand; `and`/`or` < 2 operands |
| `unknownOperandName` | 422 | HabitCreate name reference unresolvable (rollback) |
| `invalidPropertyType` | 422 | Value/type mismatch; name-based/id-based field collision; non-positive numeric; aggregation mode not valid for the property type; same property name used with two different types across one habit |
| `invalidBaseRate` | 422 | `baseRate` on non-numeric or ≤ 0 |
| `invalidItemUniqueness` | 422 | `itemUniqueness` misplaced/missing for the type |
| `invalidName` | 422 | Required name blank/whitespace (habit, item, property, or criterion) |

## Validation & Business Rules

Everything from the reference § Validation Rules carries over verbatim; summarized with implementation notes:

1. **Habit create** — atomic single transaction (items + properties + criteria); ≥1 item, ≥1 criterion, exactly one root; `endDate ≥ startDate`; the root carries `successType` and its mode's target rules (daily: `cycleTarget` bounds — positive finite integer; cumulative: no `cycleTarget`); required wire enums are explicit — omitted `cycle`/`propertyType`/`criterionType` fail with `missingCycle`/`missingPropertyType`/`missingCriterionType` instead of binding to the 0 member; same property name ⇒ same type across items; condition names resolve to ≥1 in-scope item; name-based references resolved within the payload or rollback with `unknownOperandName`; duplicate entries inside a scope/operand reference list are rejected up front with `duplicateReference` (the join-table PK would otherwise 500); numeric bounds reject NaN/Infinity (`invalidThreshold` / `invalidTarget` / `invalidBaseRate` / `invalidPropertyType`).
2. **Habit edit** — structural changes blocked when *any* punch exists (FR-2.3): removing items/properties with punch history, changing `propertyType`, `itemUniqueness`, or *changing* `cycle` to a different value (resubmitting the unchanged value is permitted). Name/order/`baseRate`/dates/criteria edits always allowed; history is a fully derived view — edits to criteria or dates change what past cycles display (no snapshots). `state` not editable; no reactivation path exists; deactivation records `deactivatedDate`.
3. **Item/property add** — always permitted.
4. **Deletions** — item delete blocked by punches (`itemHasPunches`) only (subset-scope membership rows cascade and the scope shrinks); property delete blocked by punch values (`itemHasPunches`) only — criteria bind names and survive (if the last in-scope definition disappears, the criterion never passes, spec FR-2.3); criterion delete blocked for root (`rootCriterionProtected`) / referenced (`criterionInUse` — FK Restrict + EF `DeleteBehavior.Restrict`, with the `DbUpdateException` caught and mapped).
5. **Criterion graph** — DAG validated at write time before commit: build operand edges, run a DFS/topological check for cycles (reject `circularCriterion`); operator operand-count checks.
6. **Punch create** — habit active (else `habitInactive`); the **punch date** within `[startDate, endDate?]` and not in the future (else `outOfWindow`) — FR-3.3 follows the punched day, not "today"; property belongs to path's item (else `notFound`); type/value match rules; boolean same-day upsert **applies to both create and update** — editing a punch routes boolean values to the day's existing row (wherever it lives, last write wins) instead of inserting a second live row for the same property/item/day; `per_day` / `per_cycle` uniqueness scoped per the **punch date's** day/cycle (update re-check excludes the edited punch). Uniqueness rejection is **atomic per punch record**: any offending entry voids the whole session for that item and the response names the duplicates (spec FR-3.2). Numeric edits replace the session value (running total moves by the delta; non-finite values → `invalidPropertyType`). Both write paths run their same-day read-then-write inside one explicit `IDbContextTransaction` opened with `IsolationLevel.Serializable` (→ SQLite `BEGIN IMMEDIATE` — see § PunchValues), so a concurrent writer can never interleave between the read and the insert. `from`/`to` inversions on the punch list endpoints are rejected with `invalidDateRange`, matching History.
   - **Punch update / delete** — exempt from the state and window checks: existing records stay correctable after deactivation or the end date (spec FR-3.4). `punchDate` is immutable — moving a punch means delete + create.
   - **Back-fill (`punchDate` on create).** An omitted `punchDate` = server today. A supplied one must be ≤ server today and inside `[startDate, endDate?]` (else `outOfWindow`) — an ACTIVE habit still accepts a back-fill after "today" has passed `endDate`, because the window check is on the punch date, not today; deactivation closes the door for new punches. All day/window scoping on create (per-day uniqueness scope, cycle window, boolean same-day upsert target, stored `PunchDate`) follows the **punched day**, not today. A back-filled session keeps `punchedAt = UtcNow`, so it sorts first among its day's sessions in the punch lists (informational UI ordering only). A malformed `punchDate` string is a model-binding 400 (generic ProblemDetails, no habit `code`) — by design; clients only ever emit `yyyy-MM-dd`.
7. **Root switching** — `POST`/`PUT` criteria with `isRoot: true` clears the previous root atomically (single transaction; partial unique index is the backstop) **and nulls the demoted row's `successType`/`cycleTarget`** — both are valid on the root only.
8. **Server-side payload limits** — SQLite ignores the declared max lengths, so the write paths enforce them: names (habit/item/property/criterion, bound `propertyName`) ≤ 500 characters; habit `description` ≤ 2000; share `granteeUserId` ≤ 200 / `granteeUserName` ≤ 500; list-entry strings ≤ 1000. Count caps per request: items and criteria in a habit-create ≤ 100; properties on an item ≤ 100; scope/operand references on a criterion ≤ 100; values in a punch session ≤ 100; entries in a punch value ≤ 100. Violations fail with 422 `validationTooLong` / `tooManyEntries` before any insert (constants live in `HabitRuleValidator`).
9. **Share invites** — the grantee id/name are trimmed and validated (`invalidGrantee` blank/self — the self check compares the TRIMMED grantee against the TRIMMED token id; `validationTooLong` over-length); the owner name snapshot comes from the caller's `name` claim (`missingShareOwnerName` when absent). The `AnyAsync` duplicate pre-check and the insert run in one `IsolationLevel.Serializable` transaction; the `(HabitId, GranteeUserId)` unique index backstops a lost race and the exception filter maps its violation to 422 `duplicateName` (never a 500).

## Cycle Window Computation

Implemented in `HabitEvaluationService.ComputeCycleWindow(habit, today)` → `(From, To?)` — algorithm per the reference with the revised spec's extensions:

0. **Whole-period shortcut** (`cycle == whole`): return `(startDate, endDate)` immediately — a single non-resetting cycle; an open-ended habit reports **`To = null`** (spec FR-4.1 — never a sentinel). `NaturalWindow` throws for `whole` so a new calendar cycle can never silently fall through to monthly. Day-count ceilings intentionally do not cap `whole` (its span is user-defined, unbounded `cycleTarget`).
1. Standard window for `cycle` containing the anchor: daily = anchor; weekly = Monday–Sunday (ISO); monthly = 1st–last of month.
2. **First-cycle clamp**: anchor in the same period as `startDate` → `from = startDate`.
3. **End clamp**: `endDate` earlier than the natural window end → `to = endDate`.
4. **Out-of-window anchoring** (FR-2.2): `today < startDate` anchors at `startDate` (first cycle shown); `today > endDate` anchors at `endDate` (final cycle); an **inactive** habit anchors at `min(endDate?, deactivatedDate − 1)` — the last actual cycle, frozen in the display while punches remain derivable. Aggregation always runs `[from, min(to ?? today, today)]` — future days hold no data.

The same window rule drives `perCycle` uniqueness — evaluated against the cycle containing the **new punch's punch date** (which may be a backfilled past day).

## Criterion Evaluation

`HabitEvaluationService.BuildProgressAsync` / `BuildHistoryAsync` load all punches in the window (one query, grouped in memory) and evaluate the criterion DAG bottom-up. **Conditions** resolve their bound property NAME over the scope (every in-scope item's same-named row contributes; habit-wide same-name ⇒ same-type keeps this well-defined; no in-scope definition → aggregate permanently 0, never passes), apply the condition's effective aggregation mode — boolean `ever_true` (item count), numeric `sum/avg/max/min/latest` over `value × baseRate`, list `union_distinct` (per-item distinct in the window, **summed across items** — spec RC-9) or `latest` — and pass iff aggregate ≥ threshold (the only operator). **Composites**: `and` = all pass; `or` = any; `not` = invert; progress = count of passing operands (OR/NOT target 1). **Daily mode** (root `successType: daily`, valid for condition AND composite roots): the tree is evaluated per calendar day with only that day's punches — a condition root's day passes against its own threshold, a composite root contributes the 1/0 pass bit (never the operand count); successful days accumulate vs `cycleTarget`. **Cumulative mode**: single evaluation over the window; the pass is LATCHED — once the root has evaluated true at any prefix of the cycle the cycle is marked passed (relevant for `avg`/`min`/`latest`, which are non-monotonic; the service detects them and replays per-prefix days). Results populate `ProgressOutDto` / `DayHistoryOutDto`. History derives EVERY window day (punch-less days included, evaluated on empty data — NOT-style criteria depend on this). Evaluation is cached per request only (no cross-request cache — NFR-1).

## Habit Templates

No server support required — templates are client-side constants in the UI (knowledgebuilder), pre-filling `HabitCreate` payloads (reference § Templates; FR-5). The API stores no template id.

## Authentication & Configuration

- **JWT validation**: the existing `aclearningutil` bearer configuration already validates issuer (`https://localhost:7228` dev / `https://www.alvachien.com/idserver` prod) and audience `api.knowledgebuilder`, with framework-managed OIDC metadata caching and key rotation — this replaces the reference's manual JWKS fetch, one-shot refresh retry, and `asyncio.Lock`.
- **User id**: `CurrentUserService.GetUserId()` = `ClaimTypes.NameIdentifier` ?? `sub` — the project's standard extraction; values are acidserver `AspNetUsers.Id`. All habit tables store it as `OwnerId` and **every** query filters on it (FR-6, NFR-4) — except the read-only `api/SharedHabits` surface, which joins `HabitShareGrants` so that ONLY explicitly invited users see a habit, and then only its data plus the owner's name snapshot (see § SharedHabits).
- **Cross-app user lookup**: invitation pickers resolve usernames via acidserver's own `GET /api/users/search?q=` (JwtBearer, audience `api.knowledgebuilder` — the same token this API's clients already hold; returns `userId` + `userName` only, never email). This API stores `granteeUserId` verbatim; it never queries the identity database directly.
- **Admin policy**: not applicable — habits are strictly user-scoped; the spec lists admin/user management as out of scope, so `Admin:UserIds` / `RequireAdmin` is untouched.
- **CORS**: already permits `http://localhost:4200`, `http://localhost:29800` (dev) and `https://www.alvachien.com` (prod) — no changes needed.
- **appsettings**: no new keys required. Optional: `HabitTracker:DateComparisonTimeZone` if the host timezone must be pinned explicitly instead of using `TimeZoneInfo.Local` (NFR-5).

## Database Initialization & Deployment

- Entities are registered on the existing DbContext. Because the project uses `EnsureCreated()` (no migrations) and `EnsureCreated()` is a no-op when the database file already exists, a dedicated **idempotent schema bootstrap** was added rather than requiring a fresh database: `Utility/HabitSchemaBootstrap.EnsureTablesAsync(db, logger)` runs at startup (after the existing `EnsureColumnAsync` calls in `Program.cs`) in two phases, all inside one transaction:
  - **Phase 1 (create)**: replays `db.Database.GenerateCreateScript()` statement-by-statement with every `CREATE TABLE`/`CREATE INDEX` rewritten to `IF NOT EXISTS` — safe against fresh and pre-existing databases alike, preserving all rows.
  - **Phase 2 (upgrade)**: PRAGMA-driven, idempotent column/table migration to the CURRENT model — adds `Habits.DeactivatedDate`; adds `Criteria.SuccessType` and backfills it from the legacy `DailyTarget` column (`'Daily'` when set, `'Cumulative'` on roots); and rebuilds `CriterionLeaves` from the legacy `PropertyId` shape to the name-based shape (rows copied with ids preserved, names resolved from `ItemProperties`, scope-item children detached and re-attached, the 1:1 unique index recreated). The orphan-detection path (a condition whose property row vanished) is defensive only — the old Restrict FK made it unreachable. Finally it rewrites pre-rename enum strings: `UPDATE Criteria SET CriterionType='Condition' WHERE CriterionType='Leaf'` (the criteria type persisted as the string 'Leaf' before the leaf→condition terminology rename; idempotent no-op once migrated).
  - Rationale: `IRelationalDatabaseCreator.CreateTablesAsync()` was tried first and **fails on any pre-existing database** (it throws as soon as it hits an existing table), so it cannot add just the new tables; the create-script replay is the working approach. Dev and `www.alvachien.com/learningutil` both converge on next start with no recreate/reseed step.
  - **Model indexes ride Phase 1 automatically:** because the replay regenerates the DDL from the CURRENT EF model, indexes added to the model later — e.g. `IX_Punches_OwnerId` — reach every existing database on the next start as a `CREATE INDEX IF NOT EXISTS` replay, with no hand-written migration step (verified by `HabitSchemaBootstrapTests`).
  - **Fail-fast policy (decision):** the bootstrap logs every phase (per-statement applied/skipped with the created object's name, and the offending statement on failure) and then **rethrows** — startup aborts rather than serving traffic against a half-migrated habit schema. The replay is one transaction (all-or-nothing), so a failure leaves the previous consistent schema; silently continuing would risk reads/writes against missing indexes or legacy-shaped tables. Accepted blast radius: a habit-DDL failure takes down the whole learning API for that instance (it boots before the request pipeline), which is the intended, visible failure mode; a subsequent start re-attempts automatically.
- Publish: `publish-learning-all.ps1` already publishes `aclearningutil`; nothing new (no Storage/ content, no extra processes). Dev: `start-learning-all.ps1` unchanged — habit endpoints come up with the existing service on ports 7135/5069.
- NFR-2 (Docker-Compose local-first) is superseded: the deployment target is this repo's existing IIS/`www.alvachien.com` model.

## Known Limitations / Deviations from Reference

- **No DB CHECK constraints** on enum columns (EF `EnsureCreated` doesn't emit them). Enforcement is application-level + unique/partial indexes at DB level. Acceptable at NFR-1 scale; add raw-SQL CHECKs later if ever bypassed.
- **`DateOnly`/`DateTime` storage**: SQLite TEXT via EF conversions — identical behavior to the reference's ISO strings.
- **Wire casing** changed snake_case → camelCase (breaking vs the reference UI); the new knowledgebuilder design uses the new contract throughout.
- **`GET /version`** not duplicated; reuse `aclearningutil`'s existing endpoint.
- **Database upgrade path** with existing data (no migrations) — resolved by `HabitSchemaBootstrap` (see § Database Initialization); enum-as-string columns and `DateOnly` TEXT layout verified by round-trip tests against real SQLite files.
- **Entity/DTO ids are `int`** (existing aclearningutil convention — all current tables use `int` PKs), not the reference's 64-bit ids. Practically irrelevant at the spec's scale; the wire format is unaffected.
- **Template targets**: templates are client-side constants; the knowledgebuilder UI pre-fills the FR-5.4 defaults, all of which satisfy this API's daily-mode bounds (`weekly ≤ 7` — the current spec's `meditation` default is 5). Server validation stays strict.
- **Wire contract tracks the 2026-10-01 spec revision** (success_type on the root, name-bound conditions, aggregation modes, derived cumulative targets, null open-ended `cycleTo`, all-day history, atomic uniqueness rejection, punch-date gating). The older `docs/habit-tracker-python-api-design.md` in the parent repo describes the standalone reference service and was updated alongside this document.
