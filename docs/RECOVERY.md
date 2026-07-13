# Backup and recovery

Edge Password Bulk Manager creates an integrity-checked SQLite snapshot before every delete or restore. Backups are stored under `data/backups` by default. Each operation receives a unique directory, even when several operations run during the same second.

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