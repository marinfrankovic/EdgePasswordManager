# Changelog

All notable changes to Edge Password Bulk Manager are documented here.

## 2026-07-13 - Security, recovery and reliability hardening

### Security

- Changed Docker publishing to loopback-only (`127.0.0.1`) by default.
- Changed application and Edge volume defaults to read-only. Write mode now requires both `READ_ONLY=false` and `EDGE_MOUNT_MODE=rw`.
- Changed the runtime container to the non-root .NET `app` account (UID 1654).
- Updated `Microsoft.Data.Sqlite` from 8.0.10 to 8.0.28.
- Overrode the vulnerable transitive `SQLitePCLRaw` native bundle with 3.0.3, resolving CVE-2025-6965 / GHSA-2m69-gcr7-jv3q.
- Added strict HTTPS enforcement for category-list downloads, including redirect validation.
- Added response status, content-type, byte-size and parsed-domain-count validation for downloaded lists.
- Added equivalent service-level limits for uploaded lists. The defaults are 30 MiB and 2 million unique domains.
- Tightened domain parsing to reject malformed labels, overlong names, IP addresses and non-DNS host values.

### Backup and restore

- Replaced sequential database/WAL/SHM file copies with consistent SQLite snapshots.
- Added `PRAGMA integrity_check` validation to every snapshot and staged restore.
- Made backup directory names collision-resistant with millisecond timestamps and random suffixes.
- Added exclusive-access verification before restore.
- Added same-directory restore staging and rollback of the original database and sidecars when activation fails.
- Added operation-specific restore diagnostics and a safety backup before every restore.
- Added a normal and emergency recovery runbook in `docs/RECOVERY.md`.

### Reliability

- Serialized automatic and manual list refreshes to prevent overlapping downloads.
- Serialized uploaded-list imports and changed both ingestion paths to unique temporary files with guaranteed cleanup.
- Added cleanup for partial SQLite working-copy and sidecar failures.
- Added warning logs for delete requests that match no rows without logging usernames or URLs.
- Added `/health` and a Docker Compose health check.
- Added explicit Linux ownership and permission guidance for non-root deployments.

### Testing and automation

- Added an xUnit project with 25 regression tests using temporary SQLite databases.
- Added coverage for modern ID deletion, legacy composite-key deletion, read-only enforcement, zero-row mismatches and full transaction rollback.
- Added coverage for WAL snapshots, unique backups, integrity validation and staged restore.
- Added coverage for domain parsing, upload limits, temp-file cleanup, category activation and concurrent refresh protection.
- Added a GitHub Actions workflow for restore, Release build, tests and NuGet vulnerability reporting.
- Included the test project in root-solution Dependabot NuGet monitoring.

### Documentation and deployment

- Documented loopback binding, explicit write-mode activation, lack of built-in LAN authentication, health status, list limits and non-root permissions.
- Documented backup guarantees, restore behavior, emergency recovery and validation commands.
- Updated the local Docker installation to keep intentional write mode explicit while remaining bound to loopback.