"""Use the synthetic CI database, never production data."""
import hashlib
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "database_recovery.py"


def operation(kind, source, destination, success=True):
    result = subprocess.run([sys.executable, str(SCRIPT), kind, "--source", str(source),
                             "--destination", str(destination)], capture_output=True, text=True, timeout=90)
    assert (result.returncode == 0) == success, (result.returncode, result.stdout, result.stderr)
    if success:
        assert "PASS:" in result.stdout
        assert hashlib.sha256(destination.read_bytes()).hexdigest() in result.stdout
        assert destination.stat().st_mode & 0o777 == 0o600


def counts(path):
    with sqlite3.connect(path) as db:
        assert db.execute("PRAGMA integrity_check").fetchone() == ("ok",)
        return {table: db.execute(f'SELECT count(*) FROM "{table}"').fetchone()[0]
                for table in ("Tenants", "WhatsAppTenants", "WhatsAppConversations",
                              "WhatsAppMessages", "WhatsAppOutbox", "DataProtectionKeys")}


with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    source = root / "source.db"
    fixture = Path(os.environ["CHATPLATFORM_DATA_DIR"]) / "platform.db"
    operation("backup", fixture, source)
    snapshot, restored = root / "snapshot.db", root / "restored.db"
    with sqlite3.connect(source) as active:
        assert active.execute("PRAGMA journal_mode=WAL").fetchone() == ("wal",)
        active.execute("PRAGMA wal_autocheckpoint=0")
        active.execute("CREATE TABLE BackupProbe(value TEXT)")
        active.execute("INSERT INTO BackupProbe VALUES ('committed-wal')")
        active.execute("INSERT INTO PlatformSessions VALUES ('backup-probe','synthetic','ci-tenant-a','2099-01-01')")
        active.commit()
        assert Path(str(source) + "-wal").stat().st_size > 0
        operation("backup", source, snapshot)
        active.execute("INSERT INTO BackupProbe VALUES ('later-change')")
        active.commit()
        counts_before = counts(snapshot)
        operation("restore", snapshot, restored)
        assert counts(restored) == counts_before
        with sqlite3.connect(restored) as db:
            assert db.execute("SELECT value FROM BackupProbe").fetchall() == [("committed-wal",)]
            assert db.execute("SELECT count(*) FROM PlatformSessions").fetchone() == (0,)
        with sqlite3.connect(snapshot) as db:
            assert db.execute("SELECT count(*) FROM PlatformSessions WHERE Id='backup-probe'").fetchone() == (1,)
        assert active.execute("SELECT count(*) FROM BackupProbe").fetchone() == (2,)
        previous = restored.read_bytes()
        operation("restore", snapshot, restored, success=False)
        assert restored.read_bytes() == previous
    corrupt = root / "corrupt.db"
    corrupt.write_text("not a database")
    operation("restore", corrupt, root / "invalid.db", success=False)
    assert not (root / "invalid.db").exists()
    operation("backup", root / "missing.db", root / "missing-copy.db", success=False)
    assert not (root / "missing.db").exists()
    assert not list(root.glob(".chatplatform-*"))
print("PASS: online WAL backup, offline restore, data preservation, session revocation and non-overwrite guards")
