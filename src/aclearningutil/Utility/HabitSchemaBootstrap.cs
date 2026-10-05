using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using aclearningutil.Data;

namespace aclearningutil.Utility;

/// <summary>
/// Creates the habit-tracking tables on pre-existing SQLite databases and upgrades
/// databases that still carry the superseded (v0.3.13-lineage) habit schema.
/// EnsureCreated() only creates a missing database — it never adds tables to an
/// already-created one — so deployed/older databases need this explicit bootstrap
/// (same schema-evolution strategy as the EnsureColumnAsync calls in Program.cs).
///
/// Phase 1 (create): take the model-generated create script (so the DDL can never drift
/// from the EF model), rewrite every CREATE TABLE / CREATE [UNIQUE] INDEX statement as
/// IF NOT EXISTS, and replay it. SQLite DDL is transactional, so the whole bootstrap
/// (create + upgrade) runs inside ONE transaction and is all-or-nothing: a mid-way
/// failure rolls back to the previous state instead of leaving a half-applied schema.
///
/// Phase 2 (upgrade, idempotent — PRAGMA table_info drives every decision):
/// - Habits.DeactivatedDate            → additive ALTER (spec FR-2.4).
/// - Criteria.SuccessType              → additive ALTER; backfilled from the legacy
///   DailyTarget column ('Daily' when set, 'Cumulative' on roots, NULL on non-roots).
///   The legacy DailyTarget column itself is left in place; the EF model ignores it.
/// - CriterionLeaves (legacy shape)    → rebuilt in place: condition rows bind the
///   property by NAME now (spec FR-2.3), and the old NOT NULL PropertyId column would
///   block the new inserts. Child scope-item rows are detached and re-attached around
///   the rebuild, ids are preserved, and rows whose property name can no longer be
///   resolved are dropped (defensive — the old FK made this unreachable).
///   (The FROZEN physical table/column names CriterionLeaves / CriterionLeafScopeItems /
///   CriterionLeafId predate the leaf→condition terminology rename and are pinned in
///   AppDbContext — do not "clean them up".)
/// - Criteria.CriterionType            → terminology UPDATE 'Leaf' → 'Condition' for
///   rows written before the criterion-type rename (the enum persists as its string).
///
/// Note: IRelationalDatabaseCreator.CreateTables() is NOT usable here — it fails on
/// databases where unrelated tables already exist (verified by HabitSchemaBootstrapTests).
///
/// Because Phase 1 replays the MODEL-generated script, every table and index declared on
/// the EF model — including later additions such as IX_Punches_OwnerId — automatically
/// reaches existing databases on the next start-up as a <c>CREATE ... IF NOT EXISTS</c>
/// replay; adding a model index needs no hand-written DDL step here.
///
/// FAIL-FAST POLICY (decision, reviewed 2026-10): a bootstrap failure is logged with the
/// offending statement and then RETHROWN — the app refuses to start. Rationale: serving
/// traffic against a half-migrated habit schema would produce silently wrong reads
/// (missing index/columns, legacy-shaped tables); failing at boot keeps every deployed
/// instance either fully migrated or visibly down. The accepted blast radius is that a
/// SQLite DDL failure blocks the whole learning API, not just the habit surface —
/// deliberate, because the bootstrap runs before any request pipeline is configured and
/// a corrupted-habit DB would break unrelated EnsureCreated/startup paths anyway.
/// </summary>
public static partial class HabitSchemaBootstrap
{
    public static async Task EnsureTablesAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var script = db.Database.GenerateCreateScript();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var applied = 0;
        var skipped = 0;
        var created = 0;
        var currentStatement = string.Empty;

        // Objects already present before the replay — lets the log call out at Information
        // level the tables/indexes this start-up actually CREATED (e.g. a model index added
        // after the database was built) while the full per-statement detail stays at Debug.
        var preExisting = await GetObjectNamesAsync(db, ct);

        try
        {
            foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // The create script also carries INSERT statements for the HasData seed rows
                // (LearningContentCategories). Replaying those against a seeded DB would throw
                // a PK conflict and poison the whole transaction, so only DDL is replayed —
                // seeding is Program.cs' own idempotent job.
                if (!IsDdlStatement(statement))
                {
                    skipped++;
                    logger.LogDebug("Habit schema bootstrap Phase 1: skipped non-DDL statement {Statement}.",
                        Truncate(statement));
                    continue;
                }

                currentStatement = MakeIdempotent(statement);
                if (currentStatement.Length == 0)
                {
                    skipped++;
                    continue;
                }

                // IF NOT EXISTS makes re-running this over existing objects a no-op —
                // no per-statement exception is expected; anything thrown is a real failure.
                await db.Database.ExecuteSqlRawAsync(currentStatement, ct);
                applied++;
                var objectName = ObjectName(statement);
                if (!preExisting.Contains(objectName, StringComparer.Ordinal))
                {
                    created++;
                    logger.LogInformation("Habit schema bootstrap Phase 1: created {Object}.", objectName);
                }
                else
                {
                    logger.LogDebug("Habit schema bootstrap Phase 1: applied (already present) {Object}.", objectName);
                }
            }

            logger.LogInformation(
                "Habit schema bootstrap Phase 1 (create): {Applied} DDL statement(s) applied ({Created} object(s) newly created), {Skipped} skipped.",
                applied, created, skipped);

            currentStatement = "habit schema upgrade (Phase 2)";
            await UpgradeLegacySchemaAsync(db, script, logger, ct);

            await transaction.CommitAsync(ct);
            logger.LogInformation("Habit-tracking schema bootstrap replayed {Count} statement(s).", applied);
        }
        catch (Exception ex)
        {
            // All-or-nothing: roll the whole replay back so a later start-up re-attempts it
            // against a consistent (pre-bootstrap) schema instead of a half-applied one.
            try
            {
                await transaction.RollbackAsync(ct);
            }
            catch (Exception rollbackEx)
            {
                // SQLite rolls the transaction back automatically on some DDL errors; the
                // explicit rollback then fails with "no transaction is active" — harmless.
                logger.LogDebug(rollbackEx, "Habit schema bootstrap rollback was already performed by SQLite.");
            }

            // Fail fast (see class doc): the log names the phase/statement so the crash
            // reason is obvious, then the exception propagates and startup aborts.
            logger.LogError(ex,
                "Habit-tracking schema bootstrap failed and was rolled back; startup aborts and the next start-up will retry. Offending statement: {Statement}",
                Truncate(currentStatement));
            throw;
        }
    }

    /// <summary>
    /// Extracts the created object's name (table or index) from a DDL statement for logging —
    /// e.g. <c>CREATE TABLE "Punches" (…</c> → <c>Punches</c>, <c>CREATE INDEX "IX_…"</c> →
    /// <c>IX_…</c>. Falls back to the truncated statement when the shape is unexpected.
    /// </summary>
    private static string ObjectName(string ddlStatement)
    {
        var match = ObjectNameRegex().Match(ddlStatement);
        return match.Success ? match.Groups[1].Value : Truncate(ddlStatement);
    }

    [GeneratedRegex(@"^CREATE\s+(?:TABLE|UNIQUE\s+INDEX|INDEX)\s+(?:IF\s+NOT\s+EXISTS\s+)?""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ObjectNameRegex();

    // ── Legacy-schema upgrade (idempotent) ───────────────────────────────────────────

    private static async Task UpgradeLegacySchemaAsync(AppDbContext db, string script, ILogger logger, CancellationToken ct)
    {
        // 1. Habits.DeactivatedDate (nullable, additive).
        if (!(await GetColumnsAsync(db, "Habits", ct)).Contains("DeactivatedDate", StringComparer.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Habits\" ADD COLUMN \"DeactivatedDate\" TEXT NULL", ct);
            logger.LogInformation("Habit schema upgrade: added Habits.DeactivatedDate.");
        }

        // 2. Criteria.SuccessType, backfilled from the legacy DailyTarget column when present.
        var criterionColumns = await GetColumnsAsync(db, "Criteria", ct);
        if (!criterionColumns.Contains("SuccessType", StringComparer.OrdinalIgnoreCase))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Criteria\" ADD COLUMN \"SuccessType\" TEXT NULL", ct);
            if (criterionColumns.Contains("DailyTarget", StringComparer.OrdinalIgnoreCase))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "Criteria" SET "SuccessType" = CASE
                        WHEN "DailyTarget" IS NOT NULL THEN 'Daily'
                        WHEN "IsRoot" = 1 THEN 'Cumulative'
                        ELSE NULL END
                    WHERE "SuccessType" IS NULL
                    """, ct);
            }

            logger.LogInformation("Habit schema upgrade: added Criteria.SuccessType (backfilled from DailyTarget).");
        }

        // 3. CriterionLeaves: legacy (PropertyId-based) → name-based rebuild.
        var conditionTableColumns = await GetColumnsAsync(db, "CriterionLeaves", ct);
        if (conditionTableColumns.Contains("PropertyId", StringComparer.OrdinalIgnoreCase)
            || !conditionTableColumns.Contains("PropertyName", StringComparer.OrdinalIgnoreCase))
        {
            await RebuildConditionsTableAsync(db, script, logger, ct);
        }

        // 4. Criteria.CriterionType: pre-rename rows persist the enum string 'Leaf';
        // the model now reads/writes 'Condition'. Idempotent — a no-op once migrated.
        var renamed = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE "Criteria" SET "CriterionType" = 'Condition' WHERE "CriterionType" = 'Leaf'
            """, ct);
        if (renamed > 0)
        {
            logger.LogInformation("Habit schema upgrade: renamed {Count} criteria row(s) CriterionType 'Leaf' → 'Condition'.", renamed);
        }
    }

    /// <summary>
    /// Rebuilds CriterionLeaves onto the model's current DDL: copy rows (resolving each
    /// legacy PropertyId to its property NAME), detach scope-item children, drop the old
    /// table, create the model version, re-insert rows with preserved ids, re-attach the
    /// children, and recreate the 1:1 unique index.
    /// </summary>
    private static async Task RebuildConditionsTableAsync(AppDbContext db, string script, ILogger logger, CancellationToken ct)
    {
        var createStatement = ExtractCreateTable(script, "CriterionLeaves");

        // Snapshot rows + resolved names (NULL name = the property row is gone; drop such
        // orphans defensively — the old Restrict FK made this unreachable).
        var rows = new List<(int Id, int CriterionId, string? Name, string ItemScope, double Threshold)>();
        await using (var read = await RunReaderAsync(db,
            """
            SELECT l."Id", l."CriterionId", l."ItemScope", l."Threshold",
                   (SELECT p."Name" FROM "ItemProperties" p WHERE p."Id" = l."PropertyId") AS "PropertyName"
            FROM "CriterionLeaves" l
            """, ct))
        {
            while (await read.ReadAsync(ct))
            {
                // GetValue/Convert: legacy hand-created tables may store Threshold as an
                // INTEGER literal, which GetDouble would reject.
                rows.Add((Convert.ToInt32(read.GetValue(0)), Convert.ToInt32(read.GetValue(1)),
                    read.IsDBNull(4) ? null : read.GetString(4),
                    read.GetString(2), Convert.ToDouble(read.GetValue(3))));
            }
        }

        var dropped = rows.Count(r => r.Name is null);
        if (dropped > 0)
        {
            logger.LogWarning("Habit schema upgrade: dropping {Count} orphaned CriterionLeaves row(s) whose property no longer exists.", dropped);
        }

        // Snapshot + detach scope-item children. Snapshot the FULL table (no IN filter) so
        // it matches the full DELETE below — rows referencing unknown condition ids round-trip
        // instead of being silently dropped.
        var scopeRows = new List<(int ConditionId, int ItemId)>();
        await using (var read = await RunReaderAsync(db,
            """
            SELECT s."CriterionLeafId", s."ItemId" FROM "CriterionLeafScopeItems" s
            """, ct))
        {
            while (await read.ReadAsync(ct))
            {
                scopeRows.Add((Convert.ToInt32(read.GetValue(0)), Convert.ToInt32(read.GetValue(1))));
            }
        }

        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CriterionLeafScopeItems\"", ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"CriterionLeaves\"", ct);
        await db.Database.ExecuteSqlRawAsync(createStatement, ct);

        foreach (var row in rows.Where(r => r.Name is not null))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "CriterionLeaves" ("Id", "CriterionId", "PropertyName", "ItemScope", "AggregationMode", "Threshold")
                VALUES ({0}, {1}, {2}, {3}, NULL, {4})
                """,
                [row.Id, row.CriterionId, row.Name!, row.ItemScope, row.Threshold], ct);
        }

        foreach (var (conditionId, itemId) in scopeRows)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"CriterionLeafScopeItems\" (\"CriterionLeafId\", \"ItemId\") VALUES ({0}, {1})",
                [conditionId, itemId], ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_CriterionLeaves_CriterionId\" ON \"CriterionLeaves\" (\"CriterionId\")", ct);

        logger.LogInformation(
            "Habit schema upgrade: rebuilt CriterionLeaves as name-based ({Count} row(s), {Dropped} orphan(s) dropped).",
            rows.Count - dropped, dropped);
    }

    /// <summary>Names of tables and indexes already present in sqlite_master.</summary>
    private static async Task<List<string>> GetObjectNamesAsync(AppDbContext db, CancellationToken ct)
    {
        var names = new List<string>();
        await using var reader = await RunReaderAsync(db,
            """SELECT "name" FROM "sqlite_master" WHERE "type" IN ('table', 'index')""", ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Column names of a table via PRAGMA table_info (empty when the table is absent).</summary>
    private static async Task<List<string>> GetColumnsAsync(AppDbContext db, string table, CancellationToken ct)
    {
        var names = new List<string>();
        await using var reader = await RunReaderAsync(db, $"PRAGMA table_info(\"{table}\")", ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    /// <summary>
    /// Opens a reader on the DbContext's current (transaction-bound) connection so upgrade
    /// reads join the same atomic unit as the DDL writes.
    /// </summary>
    private static async Task<DbDataReader> RunReaderAsync(AppDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        var transaction = db.Database.CurrentTransaction;
        if (transaction is not null)
        {
            command.Transaction = transaction.GetDbTransaction();
        }

        // Default behavior: disposing the reader closes the command, NOT the shared
        // connection — it is EF's transaction-bound connection and must stay open.
        return await command.ExecuteReaderAsync(ct);
    }

    /// <summary>Pulls the raw CREATE TABLE statement for one table out of the model script.</summary>
    internal static string ExtractCreateTable(string script, string table)
    {
        foreach (var statement in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (statement.StartsWith($"CREATE TABLE \"{table}\"", StringComparison.OrdinalIgnoreCase))
            {
                return statement;
            }
        }

        throw new InvalidOperationException($"Model create script has no CREATE TABLE for '{table}'.");
    }

    /// <summary>True for CREATE TABLE / CREATE [UNIQUE] INDEX (the only statements replayed).</summary>
    private static bool IsDdlStatement(string statement) =>
        TableRegex().IsMatch(statement) || IndexRegex().IsMatch(statement);

    /// <summary>Keeps log lines bounded; the DDL statement text is model-generated, never user input.</summary>
    private static string Truncate(string statement) =>
        statement.Length <= 200 ? statement : statement[..200] + "…";

    internal static string MakeIdempotent(string statement)
    {
        var result = TableRegex().Replace(statement, "CREATE TABLE IF NOT EXISTS ", 1);
        result = IndexRegex().Replace(result, m => $"CREATE {(m.Groups[1].Success ? "UNIQUE " : string.Empty)}INDEX IF NOT EXISTS ", 1);
        return result.Trim();
    }

    [GeneratedRegex(@"^CREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS)", RegexOptions.IgnoreCase)]
    private static partial Regex TableRegex();

    [GeneratedRegex(@"^CREATE\s+(UNIQUE\s+)?INDEX\s+(?!IF\s+NOT\s+EXISTS)", RegexOptions.IgnoreCase)]
    private static partial Regex IndexRegex();
}
