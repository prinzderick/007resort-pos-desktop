# otueke-pos-desktop

Windows point-of-sale client for the **Otueke Integrated Facility Operations Platform**.

> **Status: Phase 0 — scaffolding only.** Solution structure, API client, device abstractions with
> simulators, tests and CI. No sales features yet; the architecture is under review.

## What this is

**One configurable, lightweight POS application** used at every fixed station. It is a **thin
client of the Otueke API** (`otueke-api`):

```
POS (this app)  ──HTTP──▶  on-site Otueke API  ──▶  on-site MySQL 8.4
                                   │
                                   └── sync ──▶ Otueke Cloud
```

- **No local database.** The POS never talks to MySQL and holds no master data.
- **No business rules in the client.** Prices, taxes, discounts, permissions, stock and
  entitlements are decided by the API; the POS renders and captures.
- **Behaviour is configuration, not code.** What a terminal shows and allows is driven by its
  **registered device**, its **facility** and **operating point**, and the signed-in staff member's
  **role/permissions** — all returned by the API (`TerminalContext`).
- **Staff sign-in at fixed stations:** NFC badge + PIN, verified by the API.
- **Emergency offline queue** (optional, per API policy): encrypted, append-only, idempotency-keyed
  operations that are replayed **through the API only** once it is reachable (`IOfflineQueue`).

## Fixed terminals (10)

| Location | Terminals |
|---|---|
| Main Reception | 2 |
| Restaurant | 1 |
| Indoor Club | 1 |
| Beauty Spa | 1 |
| Bush Bar / Event Centre | 1 |
| Cafe / Cyber Cafe | 1 |
| Salon | 1 |
| Supermarket | 2 |
| **Total** | **10** |

All ten run the same build; they differ only by registration and API-provided configuration.

## Repository layout

```
Otueke.Pos.sln
src/
  Otueke.Pos.App/      WPF shell (net10.0-windows) — composition root, screens
  Otueke.Pos.Core/     API client, offline-queue contract, terminal context, options (net10.0)
  Otueke.Pos.Devices/  Hardware abstractions + simulators: printer (80mm ESC/POS), NFC, scanner,
                       cash drawer, customer display (net10.0, no vendor SDKs)
tests/
  Otueke.Pos.Tests/    xUnit tests (API client via fake HttpMessageHandler, simulated devices)
docs/configuration.md  Configuration template and keys
```

## Prerequisites

- .NET 10 SDK (pinned in `global.json`)
- Windows 10/11 to run the WPF app. Core/Devices/Tests build and run on macOS/Linux; the WPF
  project also compiles there thanks to `EnableWindowsTargeting`, but can only run on Windows.
- A running `otueke-api` (default `http://localhost:5080`) — see that repo's README.

## Build, run, test

```bash
dotnet build Otueke.Pos.sln
dotnet test Otueke.Pos.sln
dotnet run --project src/Otueke.Pos.App        # Windows only
```

CI (`.github/workflows/ci.yml`) builds and tests the whole solution on `windows-latest` and runs a
gitleaks secret scan on every push/PR to `main`.

## Configuration

See [docs/configuration.md](docs/configuration.md). No secrets are stored in configuration files.

## Conventions

See [CONTRIBUTING.md](CONTRIBUTING.md). Platform architecture and ADRs:
[prinzderick/otueke-docs](https://github.com/prinzderick/otueke-docs).
