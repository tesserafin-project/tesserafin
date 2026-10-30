#!/usr/bin/env python3
"""Count two prospective FK violations on an existing SQLite database, read-only."""

import argparse
import json
from pathlib import Path
import sqlite3
import sys


REFERENCES = (
    ("MediaSegments", "ItemId", "BaseItems"),
    ("CustomItemDisplayPreferences", "UserId", "Users"),
)


def diagnose(database):
    """Return counts from one snapshot; never create, migrate or repair a database."""
    path = Path(database).resolve(strict=True)
    # as_uri quotes filenames containing ?, # and %. Do not use immutable=1: it ignores WAL.
    connection = sqlite3.connect(path.as_uri() + "?mode=ro", uri=True, timeout=5)
    try:
        connection.execute("PRAGMA query_only = ON")
        connection.execute("BEGIN")
        required = {"MediaSegments": {"Id", "ItemId"},
                    "CustomItemDisplayPreferences": {"Id", "UserId"},
                    "BaseItems": {"Id"}, "Users": {"Id"}}
        for table, columns in required.items():
            if connection.execute(
                    "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = ?",
                    (table,)).fetchone() is None:
                raise ValueError("missing table: " + table)
            actual = {row[1] for row in connection.execute(f'PRAGMA table_info("{table}")')}
            if not columns <= actual:
                raise ValueError("missing columns in " + table + ": " + ", ".join(sorted(columns - actual)))

        references = []
        for table, column, parent in REFERENCES:
            # Parent on the left preserves the parent key's collation/affinity, like SQLite FKs.
            total, nulls, orphans, missing_keys = connection.execute(f'''
                SELECT COUNT(*),
                       COALESCE(SUM(child."{column}" IS NULL), 0),
                       COALESCE(SUM(child."{column}" IS NOT NULL AND NOT EXISTS (
                           SELECT 1 FROM "{parent}" AS parent WHERE parent.Id = child."{column}"
                       )), 0),
                       COUNT(DISTINCT CASE WHEN NOT EXISTS (
                           SELECT 1 FROM "{parent}" AS parent WHERE parent.Id = child."{column}"
                       ) THEN child."{column}" END)
                FROM "{table}" AS child
            ''').fetchone()
            references.append({"table": table, "column": column, "parent": parent,
                               "rows": total, "nullReferences": nulls,
                               "orphanRows": orphans, "missingParentKeys": missing_keys})
        return {"reportVersion": 1, "scope": "prospective-references-only", "references": references}
    finally:
        connection.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path, help="existing Tesserafin SQLite database or consistent backup")
    args = parser.parse_args(argv)
    try:
        report = diagnose(args.database)
    except (OSError, sqlite3.Error, ValueError) as error:
        print("Diagnostic failed: " + str(error), file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2, sort_keys=True))
    return int(any(row["orphanRows"] or row["nullReferences"] for row in report["references"]))


if __name__ == "__main__":
    sys.exit(main())
