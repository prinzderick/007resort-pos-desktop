# POS configuration

The POS reads `appsettings.json` (next to the executable), then optional `appsettings.local.json` (git-ignored,
per-machine overrides), then environment variables prefixed `R007_` (a double underscore `__` separates
sections, e.g. `R007_Pos__Printer__Kind=Windows`). **No secrets live in configuration.** The device token issued at
registration and the emergency queue key are stored DPAPI-protected under the data directory, never in these files.

## Demo / mock mode

| Setting | Env var | Notes |
|---|---|---|
| `Pos:Mock` | `R007_Pos__Mock=true` **or** the shorthand `R007_MOCK=true` | Swaps the network for the in-memory mock server with seeded data. A banner shows `DEMO MODE`. Mock state lives under `%LOCALAPPDATA%\R007Pos\mock\` so it never mixes with a real enrolment. |

## All keys

```json
{
  "Pos": {
    "Mock": false,
    "ApiBaseUrl": "http://localhost:8080",
    "RequestTimeoutSeconds": 10,
    "DataDirectory": null,
    "RequireNfcAndPin": false,
    "IdleLockMinutes": 15,
    "WedgeMaxKeyIntervalMs": 50,
    "DeviceRegistration": { "DeviceName": "" },
    "Offline": { "MaxEntries": 200, "MaxAgeMinutes": 30, "ProbeIntervalSeconds": 10 },
    "Printer": { "Kind": "File", "Name": null, "OutputDirectory": null, "CharactersPerLine": 48 },
    "Approvals": { "PollIntervalSeconds": 2, "WaitTimeoutMinutes": 10 },
    "QuickNotes": [ "No ice", "Extra spicy", "Well done", "Takeaway" ]
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `Pos:ApiBaseUrl` | `http://localhost:8080` | Default shown in the Setup screen; the entered URL is stored with the device identity. |
| `Pos:RequestTimeoutSeconds` | `10` | Per attempt. Idempotent requests are retried (3x, exponential backoff + jitter, `Retry-After` honoured). |
| `Pos:DataDirectory` | `%LOCALAPPDATA%\R007Pos` | Holds `site\device.bin` (DPAPI), `site\emergency-queue.bin` (+ `.key`), `receipts\`, `logs\`. |
| `Pos:RequireNfcAndPin` | `false` | Fixed sensitive stations: a card **and** a PIN are required (architecture/06 §4). NFC alone never signs in. The contract has no operating rule for this yet, so it is local configuration (see api-contract-notes). |
| `Pos:IdleLockMinutes` | `15` | Signs out after this long without input or an open dialog. `0` disables. |
| `Pos:WedgeMaxKeyIntervalMs` | `50` | Max gap between characters for keyboard-wedge input (scanner / NFC reader) to count as a scan rather than typing. |
| `Pos:Offline:MaxEntries` / `MaxAgeMinutes` | `200` / `30` | Emergency queue bounds; beyond them new offline actions are refused. |
| `Pos:Offline:ProbeIntervalSeconds` | `10` | Connectivity probe (`GET /health/live`) and queue drain cadence. |
| `Pos:Printer:Kind` | `File` | `File` (txt preview + ESC/POS `.bin` in `OutputDirectory`), `Console` (debug output), `Simulated` (in memory), `Windows` (raw ESC/POS to the named Windows printer). |
| `Pos:Printer:Name` | null | Windows printer queue name when `Kind=Windows` (the same queue also kicks the cash drawer). |
| `Pos:Printer:CharactersPerLine` | `48` | 80 mm Font A. Use `42` for Font B paper widths that need it. |
| `Pos:Approvals:PollIntervalSeconds` / `WaitTimeoutMinutes` | `2` / `10` | While waiting for a supervisor. A pushed `approval.decided` event only shortens the wait. |
| `Pos:QuickNotes` | four notes | Quick chips that become line `notes` (the contract has no modifier model). |

Everything else a terminal does (screens, what it can sell, offline policy, cash-session requirement, open tabs) comes
from the API: the registered device's facility capabilities and operating rules, and the signed-in staff member's
permissions.

## Where secrets and identity live

| Item | Storage |
|---|---|
| Device token, server URL, device id | `device.bin`, DPAPI `CurrentUser` scope |
| Emergency queue | `emergency-queue.bin`, AES-256-GCM; the 32-byte key is in `emergency-queue.bin.key`, DPAPI-wrapped |
| Staff access/refresh tokens | memory only, never written to disk |
| PINs, passwords | never stored; cleared from view models as soon as they are sent |

Copying these files to another machine or Windows user makes them unreadable (the terminal then asks to be
re-registered; a queue whose key is lost is moved to `*.unreadable` and reported).
