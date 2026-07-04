# Edge Password Bulk Manager

A local, containerized admin tool for **bulk-managing the passwords Microsoft Edge already has saved** for your Windows user. It lets you search, filter, categorize, multi-select and **bulk-delete** saved logins — especially useful for wiping out large groups of entries by site, category (e.g. adult sites), duplicates, or an uploaded domain list.

> This is **not** a password manager. It creates no vault, no cloud sync, and no separate password store. It operates directly on Edge's own local `Login Data` database and only ever reads/deletes **metadata** rows. It never decrypts, reveals, or exports passwords.

---

## What it does

- **Profile discovery** — finds every Edge profile (Default, Profile 1, …) in the mounted `User Data` folder, across Stable/Beta/Dev.
- **List saved logins** — origin URL, sign-on realm, username, created/last-used dates, times-used. Sortable grid, async loading.
- **Powerful filtering** — free-text, site/realm substring, username substring, duplicates-only, and **category** (adult, etc.).
- **Category tagging via blocklists** — loads domain blocklists and tags each entry (e.g. `adult`). Ships with a starter list and auto-loads any list you add.
- **Daily auto-refresh** — a background service re-downloads configured blocklist URLs every 24h (configurable) and reloads them. Manual "Refresh now" too.
- **Upload your own lists** — drop in a hosts/domain list through the UI, assigned to any category.
- **Smart selection** — select visible / invert / duplicates (keep newest) / never-used / insecure (HTTP) / by category / **by uploaded domain list**.
- **Bulk delete with safety** — dry-run report, preview, typed confirmation for large deletes, automatic timestamped DB backup, single SQLite transaction with rollback, per-row success/failure.
- **Restore & undo** — browse the backups this tool made and one-click **restore**, or **undo last delete**.
- **Cross-profile aggregate** — view and act across all profiles at once (deletes are routed to the correct profile DB).
- **Export** — metadata-only CSV (never passwords).
- **Stats & audit** — totals, adult/duplicate/insecure/never-used counts, and a local timestamped audit log (no plaintext passwords).
- **Dark/light mode**, read-only mode, schema/debug panel.

---

## How it works (and an important limitation)

Edge stores each saved login in a Chromium SQLite database named `Login Data` (table `logins`). Two very different kinds of data live there:

| Data | Encryption | Used by this tool |
|------|------------|-------------------|
| `origin_url`, `signon_realm`, `username_value`, `date_created`, `date_last_used`, `times_used` | **plaintext** | ✅ yes — list, filter, delete |
| `password_value` | **DPAPI + app-bound encryption (ABE)** | ❌ never touched |

Because passwords are protected by DPAPI **and** app-bound encryption (tied to your Windows user and Edge's own process), **decryption is not possible from inside a Docker container** — and this tool deliberately does not attempt it. Your entire use case (searching and bulk-deleting by metadata) needs none of that, since all the metadata is plaintext.

The tool operates on a **copy** of the DB for listing (so it can read even while Edge is open) and on the **live** DB only for deletes/restores (which require Edge to be closed).

### Schema handling
The `logins` schema varies across Chromium versions. On load the tool runs `PRAGMA table_info(logins)` and:
- selects only columns that actually exist (gracefully degrades if `date_last_used`, `times_used`, or `blacklisted_by_user` are missing);
- deletes rows by the stable **`id`** column when present (modern Chromium), otherwise by the **legacy composite key** (`origin_url`, `username_element`, `username_value`, `password_element`, `signon_realm`);
- converts Chromium timestamps (microseconds since 1601-01-01 UTC).

---

## Project structure

```
EdgePassManager/
├─ EdgePasswordBulkManager.sln
├─ Dockerfile
├─ compose.yaml
├─ .env.example
├─ README.md
└─ src/EdgePasswordBulkManager/
   ├─ Program.cs
   ├─ appsettings.json
   ├─ Models/         EdgeProfile, LoginEntry, AppOptions, Results
   ├─ Helpers/        ChromiumTime, DomainHelper, DomainListParser
   ├─ Services/       ProfileDiscoveryService, LoginDatabaseReader, DeleteService,
   │                  BackupExportService, RestoreService, CategoryService,
   │                  ListRefreshService, AuditLog
   ├─ State/          PasswordManagerState (MVVM view-model)
   ├─ Components/      App, Routes, Layout, Pages/Home.razor
   └─ adult-lists/    bundled starter blocklist
```

---

## Build & run (Docker)

### 1. Configure
```bash
cp .env.example .env
```
Edit `.env` and set `EDGE_USER_DATA` to your Edge `User Data` folder, e.g.
```
EDGE_USER_DATA=C:\Users\<you>\AppData\Local\Microsoft\Edge\User Data
HOST_PORT=8088
READ_ONLY=false
```

### 2. Run
```bash
docker compose up --build -d
```
Open **http://localhost:8088**.

Pull the prebuilt image instead of building:
```bash
docker pull mfrankovic/edge-password-manager:latest
```

### 3. Manage
```bash
docker compose logs -f       # logs
docker compose down          # stop
docker compose up --build -d # rebuild after changes
```

Persisted host folders (created next to `compose.yaml`):
- `data/backups` — DB backups taken before deletes/restores
- `data/exports` — CSV metadata exports
- `data/lists`   — downloaded + uploaded blocklists
- `data/logs`    — audit log

### Run locally without Docker (dev)
```bash
cd src/EdgePasswordBulkManager
dotnet run
```
Development config reads Edge from `%LOCALAPPDATA%\Microsoft\Edge\User Data` and writes artifacts under `./data`.

---

## Category lists

- **Filename convention:** files in the list directory are named `<category>__<anything>.txt` (e.g. `adult__mylist.txt`). Files without `__` fall into the default category (`adult`).
- **Formats accepted:** hosts (`0.0.0.0 example.com`), plain domains (`example.com`), `#`/`!` comments. Subdomains match a listed parent domain automatically.
- **Auto-refresh:** configured under `EdgePassManager:Categories` in `appsettings.json`. The default includes the Block List Project "porn" list, refreshed every 24h.

Add another category by editing `appsettings.json`:
```json
"Categories": [
  { "Name": "adult",    "Urls": ["https://raw.githubusercontent.com/blocklistproject/Lists/master/porn.txt"] },
  { "Name": "gambling", "Urls": ["https://raw.githubusercontent.com/blocklistproject/Lists/master/gambling.txt"] }
]
```

---

## Safety & privacy

- **Close Edge before deleting/restoring.** The DB is locked while Edge runs; the tool detects the lock and tells you.
- Every delete/restore takes an automatic **timestamped backup** first; deletes run inside a **transaction** and roll back on error.
- **No passwords** are ever decrypted, displayed, exported, or logged. CSV export and the audit log contain metadata only.
- Only your own machine and your own LAN are involved. Blocklist URLs are the only outbound requests (public files), and only when auto-refresh/refresh-now runs.
- `ReadOnlyMode` disables all writes for a look-but-don't-touch deployment.

---

## Known limitations

- **No password reveal** — impossible in a container due to app-bound encryption (by design).
- Deletes/restores require Edge to be fully closed (including background `msedge.exe` processes).
- Very large blocklists (the default is ~930k domains, ~25 MB) use tens of MB of RAM — expected.
- Timestamps are shown in the server/container local time zone.

---

## Tech

.NET 8 · Blazor Server (interactive) · `Microsoft.Data.Sqlite` · Docker (Linux container). No third-party vault logic, no browser extension, no cloud services.
