# Performance Agent

A .NET 8 background agent for Windows that records user activity (active windows, browser history, hardware and installed-app inventory) and forwards it, encrypted, to a remote ingest API.

> **Privacy notice:** the agent collects window titles and browsing history. Only deploy it where the people being monitored have been informed and have consented, as required by your local law and policy.

## Features

- **Window activity** – tracks the active window and how long each one stays in focus.
- **Browser history** – near real-time capture from Chrome, Edge, Firefox, Opera and Brave (all profiles).
- **System state events** – lock, unlock, screensaver, sleep.
- **Inventory** – hardware (model, serial, disk, RAM) and installed apps (Win32 and UWP), refreshed periodically.
- **Offline-safe storage** – everything is written to local JSONL files first and synced later.
- **Encrypted sync** – batches are encrypted (AES-256-CBC) and sent over HTTPS; local files are removed after a successful upload.

## Requirements

| | |
|---|---|
| OS | Windows 10 (1809+) / Windows 11 |
| Runtime | .NET 8 (not needed for a self-contained build) |
| SDK (to build) | .NET 8 SDK |

## Run modes

The executable has two modes:

| Mode | Command | Responsibility |
|------|---------|----------------|
| Agent (default) | `PerformanceAgent.exe` or `--agent` | Runs in the user session. Tracks windows, browser history, hardware and app inventory and writes them to local files. Never reads API secrets. |
| Service | `PerformanceAgent.exe --service` | Runs as a Windows service (`PerformanceAgentService`). Reads local files and syncs them to the API. Holds the API secrets. |

Sync only runs in service mode, so the API token and encryption key do not need to be readable by normal users.

## Build and run

```powershell
dotnet build
dotnet run                 # agent mode
dotnet run -- --service    # service mode
```

Self-contained single-file publish:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Configuration

Non-secret settings live in `appsettings.json`:

```json
{
  "PerformanceAgent": {
    "PollIntervalMs": 2000,
    "AppsRefreshIntervalHours": 24,
    "DataDirectory": "",
    "EnableConsoleOutput": false
  },
  "DataSync": {
    "Enabled": true,
    "SyncIntervalMinutes": 1,
    "BatchSize": 100,
    "DeleteAfterSync": true
  }
}
```

`DataDirectory` defaults to `C:\ProgramData\PerformanceAgent` so the agent and the service share the same data.

### Secrets (`ApiUrl`, `ApiToken`, `AesKey`)

Secrets are never stored in the repository. They are read in this order:

1. Registry: `HKLM\SOFTWARE\Perfas\PerformanceAgent`
2. Environment variables: `PERFAS_APIURL`, `PERFAS_APITOKEN`, `PERFAS_AESKEY`
3. `appsettings.json` (`DataSync` section; leave empty in source control)

Rules:

- `ApiUrl` must be HTTPS (loopback addresses are allowed for development).
- `AesKey` must be exactly 32 bytes when UTF-8 encoded.
- If any value is missing or invalid, sync is disabled and the app keeps running.

Restrict the registry key to SYSTEM and Administrators so normal users cannot read the token.

## Data layout

```
C:\ProgramData\PerformanceAgent\
└── data\
    ├── activities\   activities_<user>_<date>.jsonl
    ├── browser\      browser_<user>_<date>.jsonl
    └── system\       hardware_<date>.jsonl, apps_<timestamp>.jsonl
```

Each line is one JSON record. After a batch is uploaded, the records that were sent are removed; lines appended during the upload are kept.

## Architecture

| Area | Types |
|------|-------|
| Composition | `Program.cs` (DI wiring) |
| Scheduling | `Worker` + `IPeriodicTask` (`HardwareInventoryTask`, `AppInventoryTask`) |
| Collection | `ActivityTrackingService`, `BrowserHistoryService`, `BrowserHistoryReader`, `SystemInfoService`, `HardwareValueSanitizer`, `ApplicationCollectionService`, `SystemStateService` |
| Storage | `JsonlFileStore<T>`, `JsonlStorageService`, `IActivityStore` / `IBrowserStore` / `IInstalledAppStore` / `IHardwareStore` |
| Sync | `DataSyncService`, `DataSyncOptions`, `SyncDtoMapper`, `IIngestClient` (`HttpIngestClient`, `DisabledIngestClient`), `IEncryptionService` (`AesEncryptionService`) |

## Tests

```powershell
dotnet test Tests
```

The tests cover encryption key validation, JSONL storage safety (no data loss, no deletes outside the data directory) and parsing of browser history and hardware values.

## Security notes

- No secrets in source control. If a token or key was ever committed, rotate it on the server.
- Responses from the API are not logged.
- HTTP redirects are disabled for the ingest client.
- The data directory grants full control to SYSTEM and Administrators and Modify to Users only.
- The encryption format (AES-CBC, `IV + ciphertext`, Base64) is fixed by the server contract and does not include an integrity check. Rely on HTTPS for transport integrity.

## Troubleshooting

**Sync does nothing.** Check the log for "DataSync disabled": the URL, token or key is missing or invalid, or the service is not running.

**Access denied on the data directory.** Run once as administrator, or set `DataDirectory` to a path writable by the account that runs the agent.

**No browser history captured.** Only installed browsers with standard profile paths are monitored. Portable browsers are not supported.

**Antivirus blocks the agent.** Add an exclusion for the install folder and `C:\ProgramData\PerformanceAgent`.

## License

Proprietary. Intended for private or commercial use.
