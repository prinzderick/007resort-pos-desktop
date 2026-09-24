# 007resort-pos-desktop

Windows point-of-sale client for the **007 Resort & Spa Integrated Facility Operations Platform**.

> **Status: MVP.** A real MVVM WPF application with a typed API client for the v1 contract, a built-in
> **mock server** (demo without a backend), touch selling, tabs, supervisor approvals, split payments,
> ESC/POS receipts, Reception ticket/booking flows, cash sessions and an encrypted emergency queue.
> What can only be proven on Windows hardware is listed in
> [docs/windows-hardware-verification.md](docs/windows-hardware-verification.md).

## What this is

**One configurable, lightweight POS application** used at every fixed station. It is a **thin
client of the 007 Resort & Spa API** (`007resort-api`, contract: `api/openapi/v1.yaml` in
[007resort-docs](https://github.com/prinzderick/007resort-docs)):

```
POS (this app)  --HTTP-->  on-site 007 Resort & Spa API  -->  on-site MySQL 8.4
                                   |
                                   +-- sync --> 007 Resort & Spa Cloud
```

- **No local database, no MySQL.** The only local persistence is the encrypted emergency queue and the
  DPAPI-protected device identity.
- **No business rules in the client.** Every price, tax, discount, total, balance and change figure on screen
  is the API's. (The one exception is the clearly labelled *estimate* shown during an outage.)
- **Behaviour is configuration, not code.** Which tabs exist (Sell, Tables & tabs, Reception, Approvals, Cash
  session, History) is computed from the registered device's facility **capabilities** and the signed-in staff
  member's **permissions** (`TerminalFeatures`). There is no per-facility build and no station-name `if`.
- **Money** is `decimal` everywhere, parsed with `InvariantCulture`, sent as decimal strings, never `double`.
- **Idempotent by construction:** every mutation carries an `Idempotency-Key` that the caller generates once per
  user intent and reuses on every retry (including double taps and lost responses).

## Screens and flows

| Area | What it does |
|---|---|
| Setup | Server URL + one-time registration code -> `POST /devices/register`; token stored with DPAPI |
| Login | Touch PIN pad (staff number + PIN), username + password, **NFC card as keyboard-wedge input**. Stations that require NFC + PIN never sign in on a card alone |
| Sell | Category tabs, touch grid, search, **barcode scan box**, cart with quantity and line notes (quick notes), server-priced totals, send to kitchen/bar |
| Tables & tabs | Table map, open a tab, add rounds (each an order), send, **settle on exit** with split tenders |
| Approvals | Void, discount, comp, price override, refund, reversal: request -> the API holds it (`202`) -> supervisor decides on their device (this screen polls; a push can wake it) **or** a supervisor authorises inline with `step-up` |
| Payments | Cash (server-computed change), card / POS terminal / transfer as recorded tenders with a reference, **split** (several tenders, one atomic request), partial settle, **Paystack pay-link + verify** |
| Receipts | API returns a structured receipt; the POS lays it out for 80 mm (48 cols) and prints via `IReceiptPrinter` (ESC/POS bytes, QR, cut, drawer kick). File/console printers for demos and tests; raw Windows spooler printer for real hardware. Reprint by permission (marked DUPLICATE, no drawer kick) |
| Reception | Sports/pool booking (hold -> rentals -> pay -> confirm) and ticket/rental sales; prints a **QR entitlement receipt** |
| Cash session | Open with float; close with a **blind count** (system figure shown only after closing); shift report (print) |
| History | Payment history by permission; reprint, refund, reversal |
| Queue / status | Connectivity indicator, items waiting to be confirmed, items the server refused (acknowledge) |

## Emergency offline queue

If the API is unreachable and the facility's `operatingRules.allowOfflineOrders` / `allowOfflinePayments`
permit it, staff can keep trading with a **small, bounded, encrypted queue** (not a database):

- Queueable: create order, add line, send order, open tab (client UUIDv7 ids), and **CASH-only** payments.
  **Never** queued: card, POS terminal, transfer, Paystack, refunds, voids, adjustments, approvals, cash-session
  operations, booking holds, ticket redemption. Nothing that needs provider authorisation is ever shown as paid.
- AES-256-GCM per record, key wrapped by DPAPI; append-only file; the record sequence number is bound into the
  authentication data, so deleted/reordered/spliced records are detected; a torn final write is discarded.
- Bounded by count and age (default 200 entries / 30 minutes); when bounded out, new offline actions are refused.
- Replay is **through the API only**, strictly in order, one request in flight, reusing the original
  `Idempotency-Key` (a crash between "server applied" and "marked replayed" is harmless), tracking `rowVersion` for
  `If-Match`. A transient failure stops the drain; a definite refusal is recorded and shown to staff, never dropped.
- The UI labels queued work **PENDING CONFIRMATION** and shows no receipt for unconfirmed money.

## Demo without a backend (mock mode)

```bash
export R007_MOCK=true          # PowerShell:  $env:R007_MOCK = "true"
dotnet run --project src/R007.Pos.App        # Windows only
```

The whole real client stack (typed client, retry, auth refresh, offline replay) runs against
`MockApiHandler`, an in-memory implementation of the contract (idempotency replay, `If-Match`, `202` approvals,
step-up, client ids, cash sessions, bookings, entitlements, Paystack). Registration codes: `RESTAURANT`, `CLUB`,
`RECEPTION`. Staff: `S-1001` cashier / `1234`, `S-1002` waiter / `1111`, `S-1003` supervisor / `9999` (or
`cashier|waiter|supervisor` with the same PIN as password). Card UIDs: `04A1B2C3D4` (cashier), `04FFEE0011`
(supervisor). Receipts are written to `%LOCALAPPDATA%\R007Pos\receipts\*.txt` (+ `.bin` ESC/POS bytes).
Set `R007_Pos__RequireNfcAndPin=true` to try the NFC + PIN station.

## Repository layout

```
R007.Pos.sln
src/
  R007.Pos.Core/        Typed API client (System.Text.Json source-generated), HTTP pipeline (retry, auth
                        refresh, connectivity), money, encrypted emergency queue + replay, mock server,
                        terminal features. net10.0, no WPF.
  R007.Pos.Devices/     Hardware abstractions and implementations: ESC/POS renderer, raw Windows printer port,
                        file/console printers, printer cash drawer, keyboard-wedge decoder, simulators.
  R007.Pos.ViewModels/  MVVM: shell, screens, dialogs, PosContext. net10.0, no WPF -> unit-tested on any OS.
  R007.Pos.App/         WPF views (XAML), composition root, wedge input hook. net10.0-windows.
tests/R007.Pos.Tests/   xUnit: view models, API client vs mock/fake handlers, queue crypto/replay, money,
                        receipt snapshot, approvals, contract conformance, XAML binding guard.
tests/R007.Pos.IntegrationTests/  category "RealNode": the harness scenarios as xunit tests, skipped unless R007_API_BASE_URL is set.
tools/R007.Pos.Harness/ console harness: the real Core/client/view-model/receipt stack against a REAL running node
                        (enrolment, login, orders, approvals, payments, cash, receipts, Reception, offline replay).
docs/                   configuration.md, api-contract-notes.md, REAL_API_TEST_REPORT.md, windows-hardware-verification.md
```

## Prerequisites

- .NET 10 SDK (pinned in `global.json`).
- Windows 10/11 to **run** the WPF app. Everything builds and all tests run on macOS/Linux (`EnableWindowsTargeting`).
- A running `007resort-api` (or `R007_MOCK=true`).

## Build, run, test

```bash
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH   # macOS dev machine
dotnet build R007.Pos.sln -c Release
dotnet test  R007.Pos.sln -c Release
dotnet run --project src/R007.Pos.App                          # Windows only
```

CI (`.github/workflows/ci.yml`) builds and tests the whole solution on `windows-latest` and runs a gitleaks
secret scan on every push/PR to `main`. The `RealNode` tests are skipped there (no node).

### Against a real node (no mock)

```bash
export R007_API_BASE_URL=http://127.0.0.1:8080/api/v1                       # the local node (LOCAL_NODE.md)
export R007_NODE_SCRIPT=<repos>/work/api-integration/scripts/local-node.sh  # optional: enables the stop/start outage scenario
dotnet run --project tools/R007.Pos.Harness -- --list
dotnet run --project tools/R007.Pos.Harness -- -v                            # all scenarios (PASS/FAIL per scenario)
dotnet test tests/R007.Pos.IntegrationTests                                  # same, as xunit (category RealNode)
```

Each scenario enrols a fresh terminal with a new one-time registration code, so it needs the seeded demo data. Results and the bugs this
found on both sides: [docs/REAL_API_TEST_REPORT.md](docs/REAL_API_TEST_REPORT.md).

## Configuration

See [docs/configuration.md](docs/configuration.md). No secrets are stored in configuration files.

## Contract

Types mirror the node's `docs/openapi/v1.yaml` as verified live (statuses stay strings so a newer API cannot break parsing). Spec drift is
guarded by `ContractConformanceTests` (example payloads and schema property lists copied into
`tests/R007.Pos.Tests/ContractFixtures`). Assumptions and requests to the contract owners:
[docs/api-contract-notes.md](docs/api-contract-notes.md).

## Conventions

See [CONTRIBUTING.md](CONTRIBUTING.md). Platform architecture and ADRs:
[prinzderick/007resort-docs](https://github.com/prinzderick/007resort-docs).
