"""Isolated SQLite index/lookup benchmark; NOT full application or concurrent load certification.
Run: python3 tests/tenant_directory_scale.py [count] (default 1,000,000).
"""
import sqlite3, tempfile, time, sys
from pathlib import Path

count = int(sys.argv[1]) if len(sys.argv) > 1 else 1_000_000
assert 100 <= count <= 2_000_000
with tempfile.TemporaryDirectory(prefix="chatplatform-directory-") as tmp:
    db = sqlite3.connect(Path(tmp) / "directory.db")
    db.executescript("""
        CREATE TABLE Tenants (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, IsActive INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL);
        CREATE INDEX IX_Tenants_Name_Id ON Tenants(Name,Id);
        CREATE TABLE ManagedTenantAdmins (Username TEXT PRIMARY KEY, TenantId TEXT NOT NULL);
        CREATE INDEX IX_ManagedTenantAdmins_TenantId ON ManagedTenantAdmins(TenantId);
    """)
    started = time.perf_counter()
    with db:
        db.executemany("INSERT INTO Tenants VALUES (?, ?, 1, '2026-01-01')",
            ((f"tenant-{i:09d}", f"Organization {i:09d}") for i in range(count)))
        db.executemany("INSERT INTO ManagedTenantAdmins VALUES (?, ?)",
            ((f"admin-{i:09d}", f"tenant-{i:09d}") for i in range(count)))
    print(f"Inserted {count:,} synthetic directory records and admin mappings in {time.perf_counter()-started:.2f}s")
    cursor = f"tenant-{count-51:09d}"
    queries = [
        ("deep keyset", "SELECT Id FROM Tenants WHERE Id > ? ORDER BY Id LIMIT 101", (cursor,), 50),
        ("exact ID", "SELECT Id FROM Tenants WHERE Id = ?", (f"tenant-{count-1:09d}",), 1),
        ("exact name", "SELECT Id FROM Tenants WHERE Name = ? ORDER BY Id LIMIT 101", (f"Organization {count-1:09d}",), 1),
        ("admin lookup", "SELECT TenantId FROM ManagedTenantAdmins WHERE Username = ?", (f"admin-{count-1:09d}",), 1),
    ]
    for label, sql, params, expected in queries:
        plan = db.execute("EXPLAIN QUERY PLAN " + sql, params).fetchall()
        assert any("SEARCH" in row[-1] for row in plan), (label,plan)
        samples=[]
        for _ in range(30):
            before=time.perf_counter()
            rows=db.execute(sql,params).fetchall()
            samples.append((time.perf_counter()-before)*1000)
            assert len(rows)==expected
        samples.sort()
        print(f"{label}: p95 {samples[28]:.3f} ms; {plan}")
    db.close()
print("PASS: indexed synthetic directory queries only; API, authentication, traffic and Meta capacity not certified.")
