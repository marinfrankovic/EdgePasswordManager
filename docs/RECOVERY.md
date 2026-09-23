# Backup and recovery

Edge Password Bulk Manager creates an integrity-checked SQLite snapshot before every delete or restore. Backups are stored under `data/backups` by default. Each operation receives a unique directory, even when several operations run during the same second.

## Delete blocked by an integrity error

`Page N: never used` means SQLite found pages that belong to neither a database structure nor its free-page list. It is an integrity error, not a report of unused saved logins. Readable rows or a successful dry-run do not prove that the database is safe to modify.

The application validates the source before creating a snapshot, then validates the snapshot. A message saying **the source database failed SQLite validation** identifies a source problem. The delete for that store is aborted without changing its rows; failed backup directories are removed. In an aggregate operation, other stores may have completed their own deletes.

1. Close Edge completely, including background processes, and retry once. Confirm the exact profile and store selected in the app; do not assume the file is named `Login Data` rather than `Login Data New` or an account store.
2. If validation still fails, stop write attempts. Stop the application/container and preserve the affected store and any matching `-wal` and `-shm` sidecars together in a separate safety directory. Keep this original copy unchanged and protect it as sensitive data.
3. Check a working copy with a local SQLite client in read-only mode. Retain the original filenames so SQLite can find matching sidecars:

   ```text
   sqlite3 -readonly "path/to/working-copy/Login Data New" "PRAGMA integrity_check;"
   ```

4. Prefer a known-good backup of the **same profile and store** that returns `ok`. Follow **Emergency manual restore** below. The in-app restore may also refuse because it cannot create a validated safety backup of the damaged current database. Restoring an older backup loses subsequent local changes and may interact with Edge sync.
5. If no valid backup exists, investigate recovery on a disposable copy only. A specialist can attempt a SQLite rebuild/recovery into a new database, but a clean integrity check alone does not prove every login, encrypted password blob, or Edge metadata record survived. Compare schema and data before considering replacement, and keep the original until Edge has been verified.

Do not bypass validation, delete sidecars from the original, or run `VACUUM`, `REINDEX`, or SQLite recovery against the live store. The application does not automatically repair password databases. Its CSV export contains metadata only and cannot restore passwords.

## Preferred restore procedure

1. Close Microsoft Edge completely, including background `msedge.exe` processes.
2. Open Edge Password Bulk Manager and select the affected profile and login store.
3. Open **Backups & restore**.
4. Select the required snapshot and start the restore.
5. The application verifies the snapshot with `PRAGMA integrity_check` and creates a safety backup of the current database.
6. After restoration, refresh the selected store and confirm the expected entries are present.

The restore is staged beside the live database. The original database and its WAL/SHM sidecars are retained as rollback files until the replacement passes its integrity check. If replacement fails, the application restores the original files.

## Emergency manual restore

Use this only when the web application cannot perform the restore.

1. Stop the container and close Edge completely.
2. Locate the intended backup directory under `data/backups`. A valid directory contains `Login Data`.
3. Copy the current live database and any `-wal` and `-shm` sidecars to a separate safety directory.
4. Validate the backup with a local SQLite client:

   ```text
   sqlite3 "path/to/backup/Login Data" "PRAGMA integrity_check;"
   ```

   Continue only when the result is `ok`.

5. Remove the live database's stale `-wal` and `-shm` sidecars, then copy the validated backup `Login Data` over the live store file.
6. Start Edge and verify the saved-login metadata.

Never restore while Edge is running. Do not mix a backup database with WAL/SHM files from a different snapshot.

## Container cannot write

The runtime uses the .NET image's non-root `app` account (`UID 1654`). On a Linux Docker host, prepare persistent directories with:

```bash
mkdir -p data/{backups,exports,logs,lists}
chown -R 1654:1654 data
chmod -R u=rwX,go= data
```

Write mode also requires the mounted Edge `User Data` path to be writable by UID 1654. Read-only preview mode does not require write permission to Edge data.