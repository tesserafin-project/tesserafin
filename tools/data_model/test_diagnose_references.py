"""SQLite query/CLI controls. The reduced fixture is not EF migration acceptance."""

from contextlib import redirect_stderr, redirect_stdout
import hashlib
from io import StringIO
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from diagnose_references import diagnose, main


class ReferenceDiagnosticTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.database = Path(self.directory.name) / "database #1?%.db"
        with sqlite3.connect(self.database) as connection:
            connection.executescript('''
                CREATE TABLE BaseItems (Id TEXT PRIMARY KEY);
                CREATE TABLE Users (Id TEXT PRIMARY KEY);
                CREATE TABLE MediaSegments (Id TEXT PRIMARY KEY, ItemId TEXT NOT NULL);
                CREATE TABLE CustomItemDisplayPreferences (
                    Id INTEGER PRIMARY KEY, UserId TEXT NOT NULL, ItemId TEXT NOT NULL,
                    Client TEXT NOT NULL, Key TEXT NOT NULL, Value TEXT);
                INSERT INTO BaseItems VALUES ('item');
                INSERT INTO Users VALUES ('user');
                INSERT INTO MediaSegments VALUES ('segment', 'item');
                INSERT INTO CustomItemDisplayPreferences VALUES
                    (1, 'user', '00000000-0000-0000-0000-000000000000', 'web', 'theme', 'secret'),
                    (2, 'user', 'virtual-usersettings-scope', 'web', 'theme', 'secret');
            ''')

    def cli(self, path=None):
        output, errors = StringIO(), StringIO()
        with redirect_stdout(output), redirect_stderr(errors):
            status = main([str(path or self.database)])
        return status, output.getvalue(), errors.getvalue()

    def test_valid_global_and_virtual_item_scopes_are_not_orphans(self):
        report = diagnose(self.database)
        self.assertEqual([1, 2], [row['rows'] for row in report['references']])
        self.assertEqual([0, 0], [row['orphanRows'] for row in report['references']])
        self.assertEqual(0, self.cli()[0])

    def test_counts_orphan_rows_and_distinct_missing_parents_separately(self):
        with sqlite3.connect(self.database) as connection:
            connection.executescript('''
                INSERT INTO MediaSegments VALUES ('bad1', 'absent'), ('bad2', 'absent');
                INSERT INTO CustomItemDisplayPreferences VALUES
                    (3, 'missing-user', 'item', 'web', 'private-key', 'private-value');
            ''')
        media, preferences = diagnose(self.database)['references']
        self.assertEqual((3, 2, 1), (media['rows'], media['orphanRows'], media['missingParentKeys']))
        self.assertEqual((3, 1, 1), (preferences['rows'], preferences['orphanRows'], preferences['missingParentKeys']))
        status, output, errors = self.cli()
        self.assertEqual(1, status)
        self.assertEqual('', errors)
        for private in ('missing-user', 'private-key', 'private-value', 'secret'):
            self.assertNotIn(private, output)

    def test_database_bytes_schema_and_rows_are_preserved(self):
        before = hashlib.sha256(self.database.read_bytes()).digest()
        self.assertEqual(0, self.cli()[0])
        self.assertEqual(before, hashlib.sha256(self.database.read_bytes()).digest())

    def test_nonexistent_database_is_not_created(self):
        absent = self.database.parent / 'missing.db'
        status, output, errors = self.cli(absent)
        self.assertEqual(2, status)
        self.assertEqual('', output)
        self.assertTrue(errors)
        self.assertFalse(absent.exists())

    def test_missing_table_fails_without_clean_report(self):
        with sqlite3.connect(self.database) as connection:
            connection.execute('DROP TABLE Users')
        status, output, errors = self.cli()
        self.assertEqual((2, ''), (status, output))
        self.assertIn('missing table: Users', errors)

    def test_missing_column_fails_without_clean_report(self):
        with sqlite3.connect(self.database) as connection:
            connection.execute('ALTER TABLE MediaSegments RENAME COLUMN ItemId TO OldId')
        status, output, errors = self.cli()
        self.assertEqual((2, ''), (status, output))
        self.assertIn('ItemId', errors)

    def test_corrupt_database_fails_without_clean_report(self):
        self.database.write_bytes(b'not a SQLite database')
        self.assertEqual((2, ''), self.cli()[:2])

    def test_empty_tables_are_zero_not_null(self):
        with sqlite3.connect(self.database) as connection:
            connection.execute('DELETE FROM MediaSegments')
            connection.execute('DELETE FROM CustomItemDisplayPreferences')
        self.assertEqual(0, self.cli()[0])
        for row in diagnose(self.database)['references']:
            for column in ('rows', 'nullReferences', 'orphanRows', 'missingParentKeys'):
                self.assertEqual(0, row[column])

    def test_committed_wal_is_included_but_uncommitted_rows_are_not(self):
        connection = sqlite3.connect(self.database)
        self.addCleanup(connection.close)
        connection.execute('PRAGMA journal_mode=WAL')
        connection.execute('PRAGMA wal_autocheckpoint=0')
        connection.execute("INSERT INTO MediaSegments VALUES ('wal', 'absent')")
        connection.commit()
        connection.execute("INSERT INTO MediaSegments VALUES ('uncommitted', 'absent')")
        self.assertTrue(Path(str(self.database) + '-wal').exists())
        self.assertEqual(1, diagnose(self.database)['references'][0]['orphanRows'])
        connection.rollback()

    def test_parent_key_collation_matches_foreign_key_comparison(self):
        with sqlite3.connect(self.database) as connection:
            connection.executescript('''
                DROP TABLE BaseItems;
                CREATE TABLE BaseItems (Id TEXT COLLATE NOCASE PRIMARY KEY);
                INSERT INTO BaseItems VALUES ('ITEM');
            ''')
        self.assertEqual(0, diagnose(self.database)['references'][0]['orphanRows'])

    def test_nulls_are_separate_from_missing_parents_and_fail_the_check(self):
        with sqlite3.connect(self.database) as connection:
            connection.executescript('''
                DROP TABLE MediaSegments;
                CREATE TABLE MediaSegments (Id TEXT PRIMARY KEY, ItemId TEXT);
                INSERT INTO MediaSegments VALUES ('null', NULL);
            ''')
        media = diagnose(self.database)['references'][0]
        self.assertEqual((1, 0, 0), (media['nullReferences'], media['orphanRows'], media['missingParentKeys']))
        self.assertEqual(1, self.cli()[0])

    def test_both_relationships_use_one_snapshot_during_a_commit(self):
        writer = sqlite3.connect(self.database)
        self.addCleanup(writer.close)
        writer.execute('PRAGMA journal_mode=WAL')
        reader = sqlite3.connect(self.database.as_uri() + '?mode=ro', uri=True)

        class CommitBetweenQueries:
            def execute(inner, sql, *args):
                result = reader.execute(sql, *args)
                if 'FROM "MediaSegments" AS child' in sql:
                    writer.execute('''INSERT INTO CustomItemDisplayPreferences VALUES
                        (3, 'absent', 'item', 'web', 'key', 'value')''')
                    writer.commit()
                return result

            def close(inner):
                reader.close()

        with patch('diagnose_references.sqlite3.connect', return_value=CommitBetweenQueries()):
            self.assertEqual(0, diagnose(self.database)['references'][1]['orphanRows'])
        # The new orphan is visible in the next snapshot, not halfway through the first report.
        self.assertEqual(1, diagnose(self.database)['references'][1]['orphanRows'])

    def test_script_entry_point(self):
        result = subprocess.run([sys.executable, str(Path(__file__).with_name('diagnose_references.py')),
                                 str(self.database)], capture_output=True, text=True, check=False)
        self.assertEqual((0, ''), (result.returncode, result.stderr))
        self.assertIn('"reportVersion": 1', result.stdout)


if __name__ == '__main__':
    unittest.main()
