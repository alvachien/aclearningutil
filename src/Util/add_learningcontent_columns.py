"""One-off schema patch: add nullable columns to LearningContents.

Why this exists
---------------
This project initializes its SQLite database with ``db.Database.EnsureCreated()``
(see Program.cs). ``EnsureCreated`` only creates the database when it does not yet
exist -- it never alters an existing schema, and the project has no EF Core
migrations folder. So when the ``LearningContent`` entity gains a new column, the
already-existing ``aclearningutil.db`` must be patched by hand.

This script applies that patch non-destructively. It mirrors the model changes in
``LearningContent.cs`` and the EF config in ``AppDbContext.cs`` for every column
that has been added to ``LearningContents`` after the initial ``EnsureCreated``:

- ``byte? Version``             -- ``entity.Property(e => e.Version).HasColumnType("tinyint")`` (INTEGER affinity)
- ``bool? IncludeLatex``        -- ``entity.Property(e => e.IncludeLatex)``                    (INTEGER affinity, 0/1)
- ``bool? TranslationDisabled`` -- ``entity.Property(e => e.TranslationDisabled)``             (INTEGER affinity, 0/1)

SQLite has no native BOOLEAN type; EF Core stores bools as INTEGER (0/1), and
'tinyint' is INTEGER affinity, so ``ADD COLUMN ... INTEGER`` matches exactly what
EF Core would have created.

Usage
-----
    python add_learningcontent_columns.py <path-to-aclearningutil.db>

Behavior
--------
- Backs up the db to ``<db>.bak`` before touching it.
- Idempotent: for each column, if it already exists it is skipped; the script
  never fails on a re-run and is safe to run on a fresh DB as well.
- Prints the column list before and after, plus a row-count sanity check
  (existing rows are left untouched and remain NULL for the new columns).
"""
import os
import shutil
import sqlite3
import sys

TABLE = "LearningContents"
# (column name, SQLite type) -- kept in sync with LearningContent.cs / AppDbContext.cs.
# Add new columns here when the LearningContent entity gains fields.
COLUMNS = [
    ("Version", "INTEGER"),
    ("IncludeLatex", "INTEGER"),
    ("TranslationDisabled", "INTEGER"),
]

if len(sys.argv) != 2:
    print("usage: python add_learningcontent_columns.py <path-to-aclearningutil.db>", file=sys.stderr)
    sys.exit(2)

db_path = os.path.abspath(sys.argv[1])
if not os.path.isfile(db_path):
    print(f"db not found: {db_path}", file=sys.stderr)
    sys.exit(1)

backup_path = db_path + ".bak"
shutil.copy2(db_path, backup_path)
print(f"backup written: {backup_path}")

con = sqlite3.connect(db_path)
try:
    cur = con.execute(f"PRAGMA table_info({TABLE});")
    cols = [row[1] for row in cur.fetchall()]
    print(f"current columns: {', '.join(cols)}")

    for col, col_type in COLUMNS:
        if col in cols:
            print(f"column '{col}' already present -- nothing to do")
        else:
            con.execute(f"ALTER TABLE {TABLE} ADD COLUMN {col} {col_type};")
            con.commit()
            print(f"ALTER TABLE {TABLE} ADD COLUMN {col} {col_type}; -- applied")
            cols.append(col)

    cur = con.execute(f"PRAGMA table_info({TABLE});")
    cols_after = [row for row in cur.fetchall()]
    print("columns now:")
    for row in cols_after:
        print(f"  cid={row[0]} name={row[1]} type={row[2]} notnull={row[3]} dflt={row[4]} pk={row[5]}")

    # Sanity: existing data should be untouched and the new columns NULL for old rows.
    n = con.execute(f"SELECT COUNT(*) FROM {TABLE};").fetchone()[0]
    print(f"row count: {n}")
    for col, _ in COLUMNS:
        nulls = con.execute(f"SELECT COUNT(*) FROM {TABLE} WHERE {col} IS NULL;").fetchone()[0]
        print(f"  {col} NULL for {nulls} of {n} rows")
finally:
    con.close()
