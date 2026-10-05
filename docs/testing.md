# Testing Guide — aclearningutil

Unit tests for the habit-tracking API (and the older TTS/LLM/learning-content
endpoints) live in `test/aclearningutil.test` — xUnit + FluentAssertions (+ Moq
for controller tests), targeting `net10.0`. `test/aclearningutil.test.common` is
an empty shared-utilities shell for future cross-project helpers.

Reference: [`design-habit-api.md`](design-habit-api.md) § Cycle Window / §
Criterion Evaluation. Functional spec real cases: **multi-week verdict
scenarios** for RC-1, RC-2, RC-7, RC-8, RC-9, RC-10 live in § 2;
RC-4/RC-16 (base_rate), RC-5/RC-5b (list dedup + `per_cycle` rejection), RC-6
(boolean checklist), RC-11 (day-count weekly), RC-12 (window clamping), RC-13
(subset scope) and RC-14 (NOT) are covered as single-behavior tests in the
earlier regions of `HabitEvaluationServiceTests` (tagged in comments);
RC-15 (deactivation) and the FR-2.3 structural rules live in the controller
tests. **RC-3 is retired** — its shape is superseded by RC-5 + RC-9 (see spec
§ RC-3 superseded); its mid-habit item-add rule is now exercised end to end by
`HabitItemsControllerTests.Create_MidHabitItemAdd_...`.

## 1. Test Setup

### Running the tests

```bash
dotnet test                                        # everything
dotnet test --filter "FullyQualifiedName~Habit"    # all habit tests (306)
dotnet test --filter "FullyQualifiedName~Running_TwoWeeks"  # the weekly-verdict scenarios
```

### Database providers

Every test builds its schema with `context.Database.EnsureCreated()` — no
migrations, no shared state; each test class gets a private database and
disposes it in `IDisposable.Dispose()`.

| Factory | Provider | Used by | Notes |
|---|---|---|---|
| `TestDbContextFactory.CreateInMemoryDbContext()` | **real SQLite, `:memory:`** (`Mode=Memory;Cache=Shared`) | every data-touching test class | The whole suite. Full provider semantics — SQL translation, FK + unique-index enforcement, real transactions, `DateOnly` TEXT round-trips — the same engine production runs on, but in RAM: no disk file, and cleanup is just `context.Dispose()`. EF owns the pinned-open connection, so the database lives exactly as long as its context. |
| *(file-based, per class)* | SQLite on a temp file | `HabitSchemaBootstrapTests`, `UserLoginHistorySchemaBootstrapTests`, `HabitUtcTimestampTests`, `HabitExceptionFilterTests`, `SqliteSerializableTransactionTests` | Deliberately *not* memory mode — these inspect raw schema / PRAGMA / UTC storage behavior on an actual file (and, for the transaction tests, production-style writer concurrency against a file DB); cleaned up with `SqliteConnection.ClearAllPools()` + file delete. `SqliteSerializableTransactionTests` additionally uses shared-cache named databases to prove the `IsolationLevel.Serializable` → `BEGIN IMMEDIATE` mapping that the habit punch/share write transactions rely on. |

Each `CreateInMemoryDbContext()` call gets a fresh isolated database (unique
shared-cache name); pass the same `databaseName` to have two contexts share one.
The EF Core InMemory provider has been removed from the project entirely — the
migration surfaced one latent bug it had been hiding (a ratings test seeded two
rows with the same `(UserId, ContentId, ItemId)` triple, which the production
unique index rejects).

### Fixed-clock convention

No test depends on the machine's current date. The evaluation service takes the
"today" date as an **explicit parameter** (`BuildProgressAsync(habit, today)`,
`BuildHistoryAsync(habit, from, to, today)`), so every scenario injects real
calendar dates and asserts exact windows. (`HabitTestData.Today` exists for
the few server-dating paths only.)

### Shared fixtures

- **`Helpers/HabitTestData`** — `TestUser = "habit-user-1"` (the owner id all
  seeded rows share), `SeedPunchAsync(db, habit, item, property, day, bool?,
  num?, list[])` for direct punch insertion (bypasses the endpoint, can date
  into past/future), plus `HabitCreateDto` builders (`WeeklyRunning`,
  `DailyChecklist`, `WeeklyExerciseDayCount`, `DailyVocabulary`) used by
  controller tests.
- **`Services/HabitEvaluationServiceTests`** (private helpers) — `BuildAsync`
  creates a habit + items + properties + a **condition root criterion** (targets
  tuple `(Daily, Cycle)`; `Daily: null` ⇒ cumulative mode) and returns a
  `Graph` record; `SeedAsync` adds a punch; `AddCompositeRootAsync` /
  `AddExtraConditionAsync` wire composite trees.

## 2. Meaningful Habit Scenarios

End-to-end verdict scenarios on `HabitEvaluationService`, each grounded in an
**Illustrative Use Case (RC-n)** of
[`habit-tracker-functional-spec.md`](../../docs/habit-tracker-functional-spec.md)
— realistic multi-week histories whose expected cycle judgements are stated up
front, in contrast to the single-behavior tests above.

### 2.1 RC-1 — Weekly "Running", 10 km/week, two consecutive cycles

Spec **RC-1 "Weekly running target (10 km)"**: weekly, `Run`/`distance`
numeric, `C1 = sum of distance ≥ 10` (root, cumulative, `cycle_target` 10).
The RC-1 table shows one passing week; these scenarios walk the same habit
through every two-week fail/success combination.

Class: `test/aclearningutil.test/Services/HabitEvaluationServiceTests.cs`,
region *RC-1 — Running, 10 km/week over two consecutive weekly cycles*,
helpers `BuildRunningAsync` / `SeedRunAsync` / `AssertRunningWeekAsync`.

**Habit under test** — name `Running`, `Cycle = Weekly`, start **Mon
2026-09-21**, end **Sun 2026-10-04** (exactly two full ISO weeks), one item
`Run` with numeric property `distance`, root cumulative condition `C1`
(`dailyTarget = null`, `cycleTarget = 10`, threshold 10).

**Judgement rule** — each ISO week is its own cycle window
(`ComputeCycleWindow`: Mon–Sun, clamped to the habit's start/end). A week
*passes* when the distances punched **within that week's window** sum to ≥ 10
km (`RootPasses`: `value >= CycleTarget ?? Threshold`). The verdict is evaluated
as of the week's last day (`BuildProgressAsync(habit, <Sunday>)`) so each
window closes with its own total. Each scenario asserts `CycleFrom`, `CycleTo`,
`RootCriterion.CurrentValue`, and `RootCriterion.Passed` for **both** weeks
(8 assertions per scenario), and the four scenarios together exhaust the
fail/success matrix: fail→success, success→fail, fail→fail, success→success.

Spec case: **RC-1**. All four tests live in `HabitEvaluationServiceTests`:

| # | Test | Punches (km @ date) | Week 1 (09-21 → 09-27) | Week 2 (09-28 → 10-04) |
|---|---|---|---|---|
| 1 | `Running_TwoWeeks_FirstWeekFail_SecondWeekSuccess_RC1` | 2·09-21, 3·09-23, 2·09-28, 3·09-30, 5·10-03 | 5 km — **fail** | 10 km — **success** |
| 2 | `Running_TwoWeeks_FirstWeekSuccess_SecondWeekFail_RC1` | 2·09-21, 3·09-23, 5·09-26, 1·09-30, 5·10-03 | 10 km — **success** | 6 km — **fail** |
| 3 | `Running_TwoWeeks_BothWeeksFail_RC1` | 2·09-21, 3·09-23, 1·09-28, 2·09-30, 2·10-03 | 5 km — **fail** | 5 km — **fail** |
| 4 | `Running_TwoWeeks_BothWeeksSuccess_RC1` | 2·09-21, 3·09-23, 5·09-26, 3·09-28, 2·09-30, 5·10-03 | 10 km — **success** | 10 km — **success** |

What these scenarios pin down that single-cycle tests don't:

- **Window boundaries** — 09-26 (Sat, week 1) must *not* leak into week 2's
  total, and 10-03 (Sat, week 2) is counted in week 2 only.
- **Per-cycle re-evaluation** — the cumulative total restarts at each window
  (`CurrentValue` = 5/10/6/10 per row above), it never runs across weeks.
- **Both verdict directions** — fail→success and success→fail transitions in
  the same habit, matching the client UI's per-cycle progress display.

### 2.2 RC-7 — "Book1": four chapters, the weekly failure and its whole-cycle success

Spec **RC-7 "Book reading: tick chapters + track hours"** (`book_chapters`
template) shows the passing shape: three chapters, all marked completed within
one week (3/3 ✓). Book1 is the same model with four chapters whose marks spread
over several weeks — run **twice**, once on the `weekly` cycle (where the window
resets, so even a fully finished book never passes) and once on the `whole`
cycle (one non-resetting span, where finishing the book finally flips the
verdict). This pair is what motivated the `whole` cycle addition.

Region *RC-7 — Book1: four chapters, completion-tracked reading habit*,
helpers `BuildBook1Async` / `SeedReadingAsync` / `AssertBook1WeekAsync` /
`SeedBook1HistoryAsync`. Four tests, two histories × two cycles:

- `Book1_NotAllChaptersCompleted_EveryWeeklyCycleFails_RC7` — weekly; ch4 read
  but never marked; all three weeks fail.
- `Book1_AllChaptersEventuallyCompleted_NoSingleWeeklyCyclePasses_RC7` — weekly;
  ch4 **is** marked (**10-05**, the 18th session) but the marks sit 2+1+1 in
  three different weeks, so no window reaches 4 (a span-level assertion pins
  that all 4 `completed` punches do exist in the data).
- `Book1_WholeCycle_Chapter4Missing_WholeSpanFails_RC7` — whole; same case-1
  history; the single window sees 3/4 — still fail.
- `Book1_WholeCycle_AllChaptersCompleted_WholeSpanPasses_RC7` — whole; ch4 done
  10-05 → count 4/4 → **pass**, with a mid-span check (3/4 fail on 10-04).

**Habit under test** — name `Book1`, weekly, **Mon 2026-09-21 → Mon
2026-10-05**, shaped like the `book_chapters` template but with **four**
chapter items, each carrying boolean `completed` + numeric `hours` (*hours is
tracking-only — no criterion aggregates it*). Root `C1` is cumulative: count of
`completed = true` ≥ **4**. On the weekly cycle the end date falls on a
**Monday**, so the third ISO week is truncated to the single day 10-05: windows
`09-21→09-27`, `09-28→10-04`, `10-05→10-05`. On the whole cycle there is one
window, `09-21→10-05` (the `Window_Whole_*` unit tests cover this math).

**Punch history** — 17 reading sessions spread over 09-21 → 10-04: chapter 1
marked completed **09-23**, chapter 2 **09-27**, chapter 3 **10-01**; chapter 4
is read (5 h across 10-01 → 10-04). Test 1 stops there (ch4 never marked); test
2 adds the 18th session — **10-05, ch4 marked completed, 2 h** — so all four
chapters are done by span's end, but the marks sit 2 + 1 + 1 across weeks 1–3.

| Window | Weekly verdict | Whole verdict |
|---|---|---|
| Wk 1 · 09-21 → 09-27 | 2 (ch1, ch2) — fail | — |
| Wk 2 · 09-28 → 10-04 | 1 (ch3) — fail | — |
| Wk 3 · 10-05 → 10-05 (truncated) | 0/1 — fail | — |
| Whole span · 09-21 → 10-05, as of 10-04 | — | 3/4 — fail (running total) |
| Whole span · 09-21 → 10-05, as of 10-05 | — | ch4 missing: **3/4 fail** · all done: **4/4 PASS** |

The contrast is the point: identical data, two cycle definitions, opposite
final verdicts. Weekly resets, so even a finished book fails every week (the
second weekly test even asserts all 4 `completed` punches *do* exist in the
data — "finished the book" and "passed a weekly cycle" are different
questions). The `whole` cycle — added to the spec precisely for this — gives
"all chapters eventually done" its verdict: 3/4 fails, 4/4 passes, with the
mid-span 3/4 showing the running total honestly. Boolean aggregation stays
window-filtered in both modes (the window is simply the whole span now), which
keeps the reference's `_aggregate_property` semantics intact.

What this scenario pins down:

- **A boolean completion does not carry across weekly windows** — chapters 1–3
  were genuinely finished by 10-01, yet no *single* week holds all four marks.
- **The whole cycle accumulates across the span** — the same marks sum to 4/4
  in one window; completion of the book finally flips a verdict.
- **Tracking-only properties stay out of the verdict** — the 13 `hours` punches
  never move the count in either mode.
- **Window clamping** — weekly truncates the final partial week to the end date
  (10-05 → 10-05); whole clamps to `[startDate, endDate ?? today]` everywhere.

### 2.3 The simple "all items completed" checklist — supported, with a boundary

Region *"All items completed" checklist*, helpers `BuildChecklistAsync` /
`VerdictAsync`. Habit: 3 items each with a single boolean `done`; root `C1` =
count of `done = true` ≥ 3 (cumulative). Tests:

| Test | Cycle | Marks | Verdict |
|---|---|---|---|
| `Checklist_AllItemsDone_SameDay_DailyCyclePasses` | daily | A+B → **fail**, then C same day → | **pass** |
| `Checklist_AllItemsDone_SameWeek_WeeklyCyclePasses` | weekly | A/B/C on Mon/Tue/Wed of one week | **pass** |
| `Checklist_MarksSpreadAcrossWeeks_NeverPasses_TheBook1Problem` | weekly | one mark per ISO week | every week 1/3, **fail** |
| `Checklist_MarksSpreadAcrossWeeks_WholeCycleAccumulatesAndPasses` | whole | same history, single span | 2/3 mid-run, then **pass** once done |

The boundary these pin down: **"all items completed" is fully supported as long
as the marks fall inside one cycle window** — a daily checklist (did all items
today), a weekly one (all items sometime this week, even on different days),
and a whole one (all items sometime during the habit) all evaluate to pass.
What fails is accumulation across *resetting* windows (the weekly row above —
the Book1 problem), which is a property of the chosen cycle, not of the
criterion.

### 2.4 Extended real-case scenarios (RC-2 / RC-8 / RC-9 / RC-10)

One seeded multi-week history per spec use case, each asserting **both**
verdict directions on explicit calendar dates via the generic
`AssertVerdictAsync(g, at, from, to, value, passed)` helper. Region *Extended
spec scenarios* in `HabitEvaluationServiceTests`.

| Scenario | RC | Cycle | What it pins |
|---|---|---|---|
| `Reading_Monthly300_…_RC2` | RC-2 | monthly | multi-session/day accumulates; **pass** at 305 mid-month; October window restarts at 60 → **fail** (calendar reset) |
| `BookPages_Whole_…60AndStaysPassed_RC8` / `…LeftAt45Pages_Fails_RC8` | RC-8 | whole | pages across chapters accumulate over two weeks to 61 → **pass** and stays passed; 45 → **fail** |
| `BookExercises_Whole_PerDayDedup_…_RC9` / `…StoppedAt8…_Fails_RC9` | RC-9 | whole | `per_day` list: same-day dup counts once, a repeat on a NEW day counts again; 9 → **pass**, 8 → **fail** |
| `Composite_ChaptersAndExercisesInOneWeek_Passes_RC10` / `…ExercisesShort_Fails_RC10` | RC-10 | weekly | root C3 = AND(C1≥3 chapters, C2≥9 exercises); value = passing-operand count. Both → 2/2 **pass**; 6 exercises → 1/2 **fail** |
| `Composite_MarksSplitAcrossWeeks_WeeklyNeverPasses_RC10` | RC-10 | weekly | chapters wk1 / exercises wk2 — no single window holds both → **fail** |
| `Composite_MarksSplitAcrossWeeks_WholeCyclePasses_RC10` | RC-10 | whole | same split data on a whole cycle → both operands accumulate → **pass** |

The composite scenarios reuse `AddExtraConditionAsync` + `AddCompositeRootAsync` to
turn the base condition into `C3 = C1 AND C2`. The former RC-3 scenario was deleted
when RC-3 was retired (superseded by RC-5's weekly `per_day` list + RC-9's
whole-cycle completion — spec § RC-3 superseded); its one non-redundant
behavior, the **mid-habit item add**, is exercised end to end at the API level
by
`HabitItemsControllerTests.Create_MidHabitItemAdd_LeavesExistingPunches_And_NewPunchesAccumulate`
— create a weekly book-study habit, punch exercises via the punch endpoint, add
Chapter 5 through `POST .../Items`, punch more, and assert progress
5 → 5 (add changes nothing) → 7 **fail** → 10 **pass**, original punch
sessions intact.

### Adding a scenario

1. Model the habit in one helper (`BuildRunningAsync` / `BuildBook1Async`
   pattern, or raw `BuildAsync` for a new shape).
2. Seed the dated history one call per punch (`SeedRunAsync`,
   `SeedReadingAsync`).
3. Assert each cycle's verdict with `AssertVerdictAsync` (generic) or the
   scenario's own `Assert…WeekAsync` wrapper: window bounds + `CurrentValue` +
   `Passed`, evaluated as of the window's last day (or any day mid-span for
   running-total checks).

These tests run on **real SQLite in memory** (§ 1) — the same aggregation SQL
the production endpoints execute, so a scenario here failing means the service
verdict itself is wrong, not a provider artifact.

## 3. Login-history tests

`UserLoginHistoriesController` (§ 15 of [`design-controllers.md`](design-controllers.md))
upserts one row per user per **server-local calendar day**.

- **`Controllers/UserLoginHistoriesControllerTests`** (in-memory SQLite factory)
  — POST creates today's row (`count = 1`, `first = last`), a same-day re-POST
  bumps `LoginCount` and `LastLoginAt` while `FirstLoginAt` stays immutable;
  rows are scoped per caller (two users → two rows); a token without a user-id
  claim → 401. GET returns only the caller's days newest-first, defaults to the
  trailing-90-day window (a row at `Today-200` is excluded), honors explicit
  `from`/`to`, and rejects an inverted range with 400. The controller computes
  "today" itself from the server clock, so — like the sanctioned
  `HabitTestData.Today` paths — these tests derive the expected day from
  `DateOnly.FromDateTime(DateTime.Now)` rather than an injected clock.
- **`Data/UserLoginHistorySchemaBootstrapTests`** (temp-file SQLite) — proves
  the new table + `IX_UserLoginHistories_UserId_LoginDate` reach
  pre-existing databases through the generic Phase-1 replay (drop the table,
  run `HabitSchemaBootstrap.EnsureTablesAsync`, assert both are recreated and
  a second run is a no-op), and that real SQLite enforces the
  one-row-per-user-per-day unique index (`SQLITE_CONSTRAINT`, while a
  different user on the same day inserts fine).

The transaction-level race guarantee (Serializable → `BEGIN IMMEDIATE`) is not
re-implemented here; it is already proven generically by
`SqliteSerializableTransactionTests` (§ 1), which the punch writes and this
upsert both rely on.
