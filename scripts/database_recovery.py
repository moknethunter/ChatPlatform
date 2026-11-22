#!/usr/bin/env python3
"""Local-only SQLite backup and offline restore. Never overwrites a file or uploads data."""
import argparse
from contextlib import closing
import hashlib
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import time

REQUIRED_TABLES = {"Tenants", "WhatsAppTenants", "WhatsAppConversations", "WhatsAppMessages",
                   "WhatsAppOutbox", "DataProtectionKeys", "PlatformSessions"}


def verify_database(db):
    rows = db.execute("PRAGMA integrity_check").fetchall()
    if rows != [("ok",)]:
        raise ValueError("SQLite integrity check failed")
    tables = {row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if not REQUIRED_TABLES.issubset(tables):
        raise ValueError("Not a supported ChatPlatform database")


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def copy_database(source, destination, *, restore=False, timeout=60):
    source = Path(source).resolve(strict=True)
    destination = Path(destination).absolute()
    if not source.is_file():
        raise ValueError("Source must be an existing database file")
    if os.path.lexists(destination):
        raise FileExistsError("Destination already exists; choose a new file")
    if timeout <= 0:
        raise ValueError("Timeout must be positive")
    # SQLite online backup sees committed WAL data; copying just platform.db does not.
    fd, temporary = tempfile.mkstemp(prefix=".chatplatform-", suffix=".db", dir=destination.parent)
    os.close(fd)
    temporary = Path(temporary)
    deadline = time.monotonic() + timeout

    def progress(status, remaining, total):
        if time.monotonic() > deadline:
            raise TimeoutError("SQLite backup exceeded its deadline")

    try:
        with closing(sqlite3.connect(source.as_uri() + "?mode=ro", uri=True, timeout=5)) as original:
            with closing(sqlite3.connect(temporary, timeout=5)) as copied:
                original.backup(copied, pages=128, progress=progress, sleep=0.1)
                verify_database(copied)
                # Produce a self-contained file (no WAL files to copy separately).
                if copied.execute("PRAGMA journal_mode=DELETE").fetchone()[0].lower() != "delete":
                    raise ValueError("Could not switch snapshot to standalone journal mode")
                if restore:
                    copied.execute("DELETE FROM PlatformSessions")
                    copied.commit()
                verify_database(copied)
        os.chmod(temporary, 0o600)
        with temporary.open("rb") as f:
            os.fsync(f.fileno())
        sha = digest(temporary)
        # Atomic publication without clobbering even if another process races us.
        os.link(temporary, destination)
        return sha
    finally:
        temporary.unlink(missing_ok=True)
        for suffix in ("-wal", "-shm", "-journal"):
            Path(str(temporary) + suffix).unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=["backup", "restore"])
    parser.add_argument("--source", required=True)
    parser.add_argument("--destination", required=True, help="New file in an existing private directory")
    parser.add_argument("--timeout", type=int, default=60, help="SQLite backup deadline in seconds")
    args = parser.parse_args()
    os.umask(0o077)
    try:
        sha = copy_database(args.source, args.destination, restore=args.operation == "restore", timeout=args.timeout)
    except (sqlite3.Error, OSError, ValueError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print(f"PASS: {args.operation} integrity verified; SHA256={sha}")
    if args.operation == "restore":
        print("Offline restore only: sessions revoked. Reconcile pending messages, receipts and campaigns before enabling network or replacing a production volume.")
    else:
        print("Keep the backup private: it contains customer data and data-protection keys. Store encrypted off-host.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
